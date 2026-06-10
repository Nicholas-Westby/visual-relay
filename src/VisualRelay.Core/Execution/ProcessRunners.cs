using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisualRelay.Core.Logging;
using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed class ShellTestRunner : ITestRunner
{
    private readonly TimeSpan _timeout;
    public ShellTestRunner(TimeSpan? timeout = null) => _timeout = timeout ?? Timeout.InfiniteTimeSpan;
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var result = await ProcessCapture.RunAsync("/bin/sh", $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", rootPath, _timeout, cancellationToken);
        var output = result.TimedOut
            ? $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{result.Output}"
            : result.Output;
        return new TestRunResult(result.ExitCode, output, result.TimedOut);
    }
}

public sealed partial class SwivalSubagentRunner : ISubagentRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // The nono capability-sandbox binary used to wrap swival when the sandbox is
    // enabled (BypassSandbox == false). nono is the WRAPPER command that runs
    // swival — `nono run <flags> -- swival <args>` — not flags passed to swival.
    private const string NonoBinary = "nono";

    // The vr-guard profile (~/.config/nono/profiles/vr-guard.json, extends the
    // registry-managed `swival` pack profile) grants broad read + network and
    // confines writes/deletes to the granted workspace. workdir.access=readwrite
    // in the swival profile means --allow-cwd grants read+write to the cwd.
    private const string NonoProfile = "vr-guard";

    private readonly RelayConfig _config;
    private readonly IRelayEventSink? _eventSink;
    private readonly string _swivalBinary;
    private readonly Func<CancellationToken, Task<BackendReadiness>> _probe;
    public SwivalSubagentRunner(
        RelayConfig config,
        string swivalBinary = "swival",
        IRelayEventSink? eventSink = null,
        Func<CancellationToken, Task<BackendReadiness>>? backendProbe = null)
    {
        _config = config;
        _swivalBinary = swivalBinary;
        _eventSink = eventSink;
        _probe = backendProbe ?? (token => BackendReadinessProbe.CheckAsync(ModelBackend.BaseUrl, ProbeTimeout, token));
    }
    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Pre-flight guard: fail fast (~1-2s) when the backend is down.
        var readiness = await _probe(cancellationToken);
        if (!readiness.IsReady)
            return new SubagentResult(string.Empty, null, false, readiness.Message);

        // Resolve whitelist against PATH so missing optional tools degrade
        // instead of crashing swival's startup preflight. Emit a
        // command_dropped event per unresolvable name.
        var resolvedCommands = ResolveCommandsOnPath(invocation.Stage.Commands, _eventSink, invocation);
        if (string.IsNullOrWhiteSpace(resolvedCommands))
        {
            return new SubagentResult(string.Empty, null, false,
                $"All whitelisted commands are missing from PATH. " +
                $"Commands: [{invocation.Stage.Commands}]. " +
                $"After dropping unresolvable names, no commands remain — refusing to run " +
                $"because swival treats an empty whitelist as unrestricted.");
        }

        // SubagentTimeoutMilliseconds is now an optional absolute ceiling (0 = disabled).
        var absoluteCeilingMs = _config.SubagentTimeoutMilliseconds;

        // Parse trace-dir name so retries follow stage{n}-attempt{k}.
        var traceDirParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
        RelayAttempt.TryParse(Path.GetFileName(invocation.TraceDirectory), out var stageNum, out var startAttempt);
        var maxStallAttempts = _config.MaxStallRetries + 1;
        var stallRetriesLeft = _config.MaxStallRetries;
        var contractRetriesLeft = _config.MaxContractRetries;
        var escalationUsed = false;
        var attempt = startAttempt;
        var currentInvocation = invocation;
        string? correctivePriorOutput = null;
        string? correctiveShapeError = null;

        while (true)
        {
            // Recompute first-output threshold for the current tier (may have escalated).
            var currentFirstOutputMs = _config.FirstOutputTimeoutMsByTier.TryGetValue(currentInvocation.Tier, out var ctMs)
                ? ctMs : _config.FirstOutputTimeoutMs;

            // Recompute inactivity timeout for the current tier (may have escalated).
            var currentInactivityMs = _config.InactivityTimeoutMsByTier?.TryGetValue(currentInvocation.Tier, out var itMs) == true
                ? itMs : _config.InactivityTimeoutMs;
            var traceDir = attempt == startAttempt
                ? invocation.TraceDirectory
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}");
            var reportFile = attempt == startAttempt
                ? invocation.ReportFile
                : Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}.report.json");
            var attemptInvocation = currentInvocation with { TraceDirectory = traceDir, ReportFile = reportFile };

            Directory.CreateDirectory(traceDir);
            await using var profileSession = await SwivalProfileSession.PrepareAsync(attemptInvocation.TargetRoot, cancellationToken);

            using var watchdogCts = new CancellationTokenSource();
            using var watchdogLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdogCts.Token);

            var watchdog = new ActivityWatchdog(currentFirstOutputMs, currentInactivityMs, absoluteCeilingMs, watchdogCts);

            await using var activeTraceTailer = RelayTraceTailer.Start(traceDir,
                _eventSink is null ? null : (entry, token) => PublishTraceAsync(attemptInvocation, entry, token),
                onActivity: () => watchdog.Pulse("trace"));

            var arguments = BuildArguments(attemptInvocation, resolvedCommands);
            arguments.Add(correctivePriorOutput is not null
                ? BuildCorrectivePrompt(attemptInvocation, correctivePriorOutput, correctiveShapeError)
                : BuildPrompt(attemptInvocation));
            var (fileName, launchArguments) = BuildLaunchTarget(arguments);
            var sandboxEnv = BuildSandboxEnvironment(_config);
            var processTimeout = absoluteCeilingMs <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(absoluteCeilingMs);

            var processTask = ProcessCapture.RunAsync(fileName, launchArguments, attemptInvocation.TargetRoot,
                processTimeout, cancellationToken, environment: sandboxEnv, killToken: watchdogCts.Token,
                onActivity: watchdog.Pulse);
            var watchdogTask = watchdog.WaitAsync(watchdogLinkedCts.Token);
            SubagentResult? stallResult = null;
            // Task.WhenAny may return processTask when the watchdog kill triggers
            // a near-simultaneous process exit (race).  Check watchdogCts cancellation
            // (set synchronously by the watchdog before it returns) so a stall is
            // never misreported as "exit 137".
            if (await Task.WhenAny(processTask, watchdogTask) == watchdogTask
                || watchdogCts.IsCancellationRequested)
            {
                var wdResult = await watchdogTask;
                if (wdResult.Outcome != ActivityWatchdog.Outcome.Disarmed)
                {
                    // Watchdog fired — killToken already triggered process.Kill().
                    await processTask;

                    // Publish stall/kill event with signal details.
                    if (_eventSink is not null)
                    {
                        await _eventSink.PublishAsync(new RelayEvent(
                            DateTimeOffset.UtcNow, "warn", "stall_kill",
                            attemptInvocation.RunId, attemptInvocation.TargetRoot,
                            attemptInvocation.TaskName, attemptInvocation.Stage.Number,
                            attemptInvocation.Tier, attempt,
                            Data: new Dictionary<string, string>
                            {
                                ["reason"] = wdResult.Outcome == ActivityWatchdog.Outcome.FiredAbsoluteCeiling
                                    ? "absolute_ceiling" : "stall",
                                ["lastSignal"] = wdResult.LastPulseSource,
                                ["silenceMs"] = wdResult.SilenceMs.ToString(),
                                ["firstOutputTimeoutMs"] = currentFirstOutputMs.ToString(),
                                ["inactivityTimeoutMs"] = currentInactivityMs.ToString()
                            }), cancellationToken);
                    }

                    if (wdResult.Outcome == ActivityWatchdog.Outcome.FiredAbsoluteCeiling)
                    {
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"swival timed out after {absoluteCeilingMs}ms absolute ceiling. " +
                                $"Last signal: {wdResult.LastPulseSource}, silence: {wdResult.SilenceMs}ms."));
                    }
                    else if (stallRetriesLeft > 0)
                    {
                        stallRetriesLeft--;
                        attempt++;
                        continue;
                    }
                    else
                    {
                        var phase = wdResult.LastPulseSource == "none" ? "first-output" : "inactivity";
                        var threshold = wdResult.LastPulseSource == "none" ? currentFirstOutputMs : currentInactivityMs;
                        stallResult = new SubagentResult(string.Empty, null, false,
                            ErrorHintClassifier.WithHint(
                                $"persistent model-backend stall: swival had no activity for " +
                                $"{wdResult.SilenceMs}ms (phase={phase}, threshold={threshold}ms). " +
                                $"Last signal: {wdResult.LastPulseSource}. " +
                                $"{maxStallAttempts} attempts exhausted."));
                    }
                }
            }

            if (stallResult is not null)
                return stallResult;

            var result = await processTask;
            watchdogCts.Cancel();
            try { await watchdogTask; } catch (OperationCanceledException) { }

            if (result.TimedOut)
            {
                // ProcessCapture's own timeout fired — only possible when
                // SubagentTimeoutMilliseconds > 0 (absolute ceiling backstop).
                var reason = $"swival timed out after {absoluteCeilingMs}ms absolute ceiling. " +
                    "If swival was running a test command that hung, fix the hang and re-run only the specific " +
                    "tests you need (use a targeted subset, e.g. the TestFileCommand \"{files}\" pattern).";
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
            }

            if (result.ExitCode != 0)
            {
                var reason = $"swival exit {result.ExitCode}: {TrimForError(result.Output)}";
                return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
            }

            var json = FencedJsonExtractor.Extract(result.Output);
            correctiveShapeError = null;
            if (json is not null)
            {
                // Validate required keys from the stage contract.
                correctiveShapeError = ValidateContractShape(json, attemptInvocation.Stage.OutputContract);
                if (correctiveShapeError is not null)
                    json = null;
            }

            if (json is null)
            {
                if (contractRetriesLeft > 0)
                {
                    contractRetriesLeft--;
                    correctivePriorOutput = result.Output;
                    await PublishContractRetryAsync(attemptInvocation, attempt, cancellationToken);
                    attempt++;
                    continue;
                }

                // Try one tier escalation before giving up.
                // Only escalate when corrective retries were configured but exhausted —
                // MaxContractRetries:0 means fail-fast across the board.
                if (_config.MaxContractRetries > 0 && !escalationUsed)
                {
                    var nextTier = NextTier(currentInvocation.Tier);
                    if (nextTier is not null)
                    {
                        escalationUsed = true;
                        correctivePriorOutput = result.Output;
                        currentInvocation = currentInvocation with { Tier = nextTier };
                        // Re-resolve commands against PATH for the escalated tier's stage.
                        resolvedCommands = ResolveCommandsOnPath(currentInvocation.Stage.Commands, _eventSink, currentInvocation);
                        if (string.IsNullOrWhiteSpace(resolvedCommands))
                        {
                            return new SubagentResult(string.Empty, null, false,
                                $"All whitelisted commands are missing from PATH after tier escalation. " +
                                $"Commands: [{currentInvocation.Stage.Commands}].");
                        }
                        await PublishContractRetryAsync(attemptInvocation, attempt, cancellationToken);
                        attempt++;
                        continue;
                    }
                }

                return new SubagentResult(result.Output, null, false,
                    ErrorHintClassifier.WithHint("no valid fenced json block"));
            }

            return new SubagentResult(result.Output, json, true, null);
        }
    }

    private Task PublishTraceAsync(StageInvocation invocation, TraceEntry entry, CancellationToken cancellationToken) =>
        _eventSink!.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow,
            "info",
            "trace",
            invocation.RunId,
            invocation.TargetRoot,
            invocation.TaskName,
            invocation.Stage.Number,
            invocation.Tier,
            Data: new Dictionary<string, string>
            {
                ["kind"] = entry.Kind.ToString(),
                ["title"] = entry.Title,
                ["content"] = TrimForTrace(entry.Content)
            }), cancellationToken);

    // Pure swival arguments. The sandbox is applied by WRAPPING this whole
    // invocation in `nono run` (see BuildLaunchTarget) — never by passing
    // sandbox flags to swival (swival has no --sandbox/--nono-* flags; doing so
    // made nono print its version and exit 1, breaking every call).
    internal List<string> BuildArguments(StageInvocation invocation, string? resolvedCommands = null)
    {
        var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
        var commands = resolvedCommands ?? invocation.Stage.Commands;
        return
        [
            "-q",
            "--profile", profile,
            "--api-key", "not-needed",
            "--base-dir", invocation.TargetRoot,
            "--system-prompt", invocation.Stage.SystemPrompt,
            "--no-lifecycle",
            "--no-history",
            "--files", invocation.Stage.Files,
            "--commands", commands,
            "--trace-dir", invocation.TraceDirectory,
            "--report", invocation.ReportFile,
            "--max-turns", invocation.MaxTurns.ToString()
        ];
    }

    // Intersect a --commands whitelist with PATH so missing optional tools
    // degrade gracefully instead of crashing swival's startup preflight. Emits
    // a "command_dropped" event per unresolvable name. The special values "all"
    // and "none" pass through unchanged (swival-special, not comma-separated).
    internal static string ResolveCommandsOnPath(
        string commands,
        IRelayEventSink? eventSink,
        StageInvocation invocation)
    {
        if (commands is "all" or "none")
            return commands;

        var names = commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return string.Empty;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var resolved = new List<string>(names.Length);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (pathDirs.Any(dir => File.Exists(Path.Combine(dir, name))))
            {
                resolved.Add(name);
            }
            else
            {
                eventSink?.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow,
                    "warn",
                    "command_dropped",
                    invocation.RunId,
                    invocation.TargetRoot,
                    invocation.TaskName,
                    invocation.Stage.Number,
                    invocation.Tier,
                    Data: new Dictionary<string, string>
                    {
                        ["name"] = name,
                        ["reason"] = "not found on PATH"
                    }));
            }
        }

        return string.Join(',', resolved);
    }

    // Build environment overrides that redirect transitive-dependency caches
    // into ~/.config/swival (already in the swival profile write-allow list)
    // so nono's vr-guard sandbox does not block them. See nono-grant-swival-
    // workspace-writes (stage 6).
    //   HF_HOME        → ~/.config/swival/huggingface  (huggingface_hub, pulled
    //                    in by litellm, defaults to ~/.cache/huggingface/).
    //   XDG_CACHE_HOME → ~/.config/swival/cache        (broad catch-all for
    //                    programs that follow the XDG spec).
    //   UV_CACHE_DIR   → ~/.config/swival/uv-cache     (uv's Python package
    //                    cache; defaults to ~/.cache/uv/).
    internal static IReadOnlyDictionary<string, string>? BuildSandboxEnvironment(RelayConfig config)
    {
        if (config.BypassSandbox)
            return null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Dictionary<string, string>
        {
            ["HF_HOME"] = Path.Combine(home, ".config", "swival", "huggingface"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, ".config", "swival", "cache"),
            ["UV_CACHE_DIR"] = Path.Combine(home, ".config", "swival", "uv-cache"),
        };
    }

    // Resolve the process to actually launch. When the sandbox is bypassed we run
    // swival directly. When it is enabled we run `nono` as the wrapper:
    //
    //   nono run -p vr-guard --allow-cwd --rollback --no-rollback-prompt \
    //       -- <swivalBinary> <swivalArgs...>
    //
    // Flag rationale (TESTED with nono v0.62.0 against swival 1.0.28):
    //   run                   sandbox-and-execute a command; `--` separates nono's
    //                         flags from the wrapped program + its args.
    //   -p vr-guard           the capability profile (broad read + net; writes
    //                         confined to the workspace).
    //   --allow-cwd           grant the target repo (process cwd == TargetRoot)
    //                         without an interactive prompt. The level is set by
    //                         the profile's workdir.access (readwrite for swival),
    //                         so swival can read/write the repo and spawn the test
    //                         commands `--commands all` requires.
    //   --rollback            atomic snapshot of in-scope writes for the session.
    //   --no-rollback-prompt  skip the post-exit interactive rollback review so the
    //                         relay stays fully non-interactive (no hang on stdin).
    internal (string FileName, IReadOnlyList<string> Arguments) BuildLaunchTarget(List<string> swivalArguments)
    {
        if (_config.BypassSandbox)
        {
            return (_swivalBinary, swivalArguments);
        }

        var nonoArguments = new List<string>
        {
            "run",
            "-p", NonoProfile,
            "--allow-cwd",
            "--rollback",
            "--no-rollback-prompt",
            "--",
            _swivalBinary
        };
        nonoArguments.AddRange(swivalArguments);
        return (NonoBinary, nonoArguments);
    }

    private static string BuildPrompt(StageInvocation invocation)
    {
        var parts = new List<string>
        {
            $"# Relay stage {invocation.Stage.Number}: {invocation.Stage.Name}",
            $"Task: {invocation.TaskName}",
            string.Empty,
            "## Task input",
            invocation.TaskInput,
            string.Empty,
            "## Manifest",
            invocation.Manifest.Count > 0 ? string.Join('\n', invocation.Manifest) : "(not set yet)"
        };
        if (!string.IsNullOrWhiteSpace(invocation.TaskContext))
        {
            parts.AddRange(["", "## Task context", invocation.TaskContext]);
        }

        if (invocation.LogSources.Count > 0)
        {
            parts.AddRange(["", "## Log sources", string.Join('\n', invocation.LogSources)]);
        }

        parts.AddRange(["", "## Prior stages", invocation.LedgerSoFar, "", invocation.Stage.OutputContract]);

        if (!string.IsNullOrWhiteSpace(invocation.LastTestOutput))
        {
            parts.AddRange(["", "## Failing verify output", TrimForTrace(invocation.LastTestOutput)]);
        }

        if (!string.IsNullOrWhiteSpace(invocation.TestCommand))
        {
            parts.AddRange(["", "## Verify command", "Run this exact command to reproduce and confirm the fix:", invocation.TestCommand]);
        }

        return string.Join('\n', parts);
    }

    private static string BuildCorrectivePrompt(StageInvocation invocation, string priorOutput, string? shapeError = null)
    {
        var problem = shapeError is not null
            ? $"The previous completion had a valid fenced JSON block but its shape was wrong: {shapeError}. " +
              "Reply with ONLY a corrected fenced JSON block — fix the shape issue, derive the values from the prior answer below. " +
              "Do NOT redo the work or add any other text."
            : "The previous completion was missing the required fenced JSON block. " +
              "Reply with ONLY that block — derive it from the prior answer below. " +
              "Do NOT redo the work or add any other text.";

        var parts = new List<string>
        {
            $"# Relay stage {invocation.Stage.Number}: {invocation.Stage.Name} — CORRECTIVE RETRY",
            $"Task: {invocation.TaskName}",
            string.Empty,
            problem,
            string.Empty,
            "## Expected contract",
            invocation.Stage.OutputContract,
            string.Empty,
            "## Prior output",
            priorOutput
        };
        return string.Join('\n', parts);
    }

    private static string? NextTier(string tier) => tier switch
    {
        "cheap" => "balanced",
        "balanced" => "frontier",
        _ => null
    };

    /// <summary>
    /// Validates that <paramref name="json"/> is a JSON object whose root contains
    /// every required key declared in <paramref name="contract"/>. Returns null on
    /// success or an error message describing the mismatch.
    /// </summary>
    internal static string? ValidateContractShape(string json, string contract)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return $"contract root must be a JSON object, but got {doc.RootElement.ValueKind}";

            // Extract required keys from the contract template: quoted names that
            // are NOT suffixed with "?" (optional).  Example:
            //   { "summary": string, "amendManifest"?: string[] }
            // yields ["summary"].
            var matches = ContractKeyRegex().Matches(contract);
            foreach (Match m in matches)
            {
                var key = m.Groups[1].Value;
                if (!doc.RootElement.TryGetProperty(key, out _))
                    return $"contract is missing required key \"{key}\" — root must be a JSON object with keys: [{string.Join(", ", matches.Select(x => x.Groups[1].Value))}]";
            }
        }
        catch (JsonException ex)
        {
            return $"contract is not valid JSON: {ex.Message}";
        }

        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"(\\w+)\"(?!\\s*\\?)\\s*:")]
    private static partial Regex ContractKeyRegex();

    private async Task PublishContractRetryAsync(StageInvocation invocation, int attempt, CancellationToken cancellationToken)
    {
        if (_eventSink is null)
            return;
        await _eventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow,
            "info",
            "contract_retry",
            invocation.RunId,
            invocation.TargetRoot,
            invocation.TaskName,
            invocation.Stage.Number,
            invocation.Tier,
            attempt,
            Data: new Dictionary<string, string>
            {
                ["message"] = "corrective retry for missing/malformed JSON contract block"
            }), cancellationToken);
    }

    private static string TrimForError(string value)
    {
        var text = value.Trim();
        return text.Length <= 600 ? text : string.Concat(text.AsSpan(0, 600), "...");
    }

    private static string TrimForTrace(string value)
    {
        var text = value.Trim();
        return text.Length <= 1_500 ? text : string.Concat(text.AsSpan(0, 1_500), "...");
    }
}

