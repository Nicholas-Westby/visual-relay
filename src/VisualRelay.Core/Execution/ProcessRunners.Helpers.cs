using System.Text.Json;
using System.Text.RegularExpressions;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// General helpers: launch target building, prompt construction, contract
// validation, trace/autopsy persistence. Command PATH resolution lives in
// ProcessRunners.CommandResolution.cs.
public sealed partial class SwivalSubagentRunner
{
    // Resolve the process to actually launch. The sandbox is always on, so we run
    // `nono` as the wrapper around swival. skipDirs become --skip-dir flags so
    // nono's rollback preflight stays under budget.
    internal (string FileName, IReadOnlyList<string> Arguments) BuildLaunchTarget(
        List<string> swivalArguments, IReadOnlyList<string>? skipDirs = null, StageInvocation? invocation = null)
    {
        // Windows has no nono; wrap swival in the OS-selected sandbox instead.
        if (OperatingSystem.IsWindows())
        {
            var (mode, wxc, policy) = MxcProvisioner.ResolvePlan(ExtractBaseDir(swivalArguments));
            if (invocation is { } inv)
                PublishSandboxMode(mode, inv);
            return BuildWindowsLaunchTarget(swivalArguments, mode, wxc, policy);
        }

        var prefix = BuildNonoPrefix(_config, rollback: true, skipDirs: skipDirs);
        var nonoArguments = new List<string>(prefix) { _swivalBinary };
        nonoArguments.AddRange(swivalArguments);
        return (_nonoBinary, nonoArguments);
    }

    // Windows: wrap swival in MXC (default), the degraded builtin (opt-in), or block
    // when no sandbox is available — execution is never silently uncontained.
    internal (string FileName, IReadOnlyList<string> Arguments) BuildWindowsLaunchTarget(
        List<string> swivalArguments, WindowsSandboxMode mode, string? wxcExec, string? policyPath)
    {
        var swival = ResolveWindowsSwival();
        return mode switch
        {
            WindowsSandboxMode.Mxc => WindowsSandbox.BuildMxcLaunch(wxcExec!, policyPath!, swival, swivalArguments),
            WindowsSandboxMode.Builtin => WindowsSandbox.BuildBuiltinSwivalLaunch(swival, swivalArguments),
            _ => throw new InvalidOperationException(WindowsSandbox.BlockedMessage),
        };
    }

    // Resolve swival to a directly-launchable path: CreateProcess (UseShellExecute
    // false) only auto-appends .exe, so a bare "swival" backed by a PATHEXT shim
    // (swival.cmd/.bat) would not launch even though the preflight found it. An
    // already-rooted binary passes through unchanged.
    private string ResolveWindowsSwival() =>
        Path.IsPathRooted(_swivalBinary) || _swivalBinary.Contains(Path.DirectorySeparatorChar)
            ? _swivalBinary
            : PathExecutables.Find(_swivalBinary) ?? _swivalBinary;

    // Surface the active Windows sandbox mode (the degraded builtin escape is a
    // warning) to the run log so an operator can see whether writes are contained.
    private void PublishSandboxMode(WindowsSandboxMode mode, StageInvocation invocation)
    {
        _eventSink?.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow,
            mode == WindowsSandboxMode.Builtin ? "warn" : "info",
            "sandbox_mode",
            invocation.RunId, invocation.TargetRoot, invocation.TaskName,
            invocation.Stage.Number, invocation.Tier,
            Data: new Dictionary<string, string> { ["mode"] = WindowsSandbox.DescribeMode(mode) }));
    }

    // The --base-dir value (the workspace root the MXC policy confines writes to).
    private static string? ExtractBaseDir(IReadOnlyList<string> swivalArguments)
    {
        for (var i = 0; i + 1 < swivalArguments.Count; i++)
        {
            if (swivalArguments[i] == "--base-dir")
                return swivalArguments[i + 1];
        }
        return null;
    }

    internal static string BuildPrompt(StageInvocation invocation)
    {
        var parts = new List<string>
        {
            $"# Relay stage {invocation.Stage.Number}: {invocation.Stage.Name}",
            $"Task: {invocation.TaskName}",
            $"Working directory: {invocation.TargetRoot}",
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
            ? $"The previous completion had a valid fenced JSON block but it was rejected: {shapeError}. " +
              "Reply with ONLY a corrected fenced JSON block — fix the issue, derive the values from the prior answer below. " +
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
    private static string? ValidateContractShape(string json, string contract)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return $"contract root must be a JSON object, but got {doc.RootElement.ValueKind}";

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

    [GeneratedRegex("\"(\\w+)\"(?!\\s*\\?)\\s*:")]
    private static partial Regex ContractKeyRegex();

    /// <summary>
    /// Returns the TAIL of <paramref name="value"/> (last <paramref name="tailChars"/>
    /// characters), prepended with "…" when truncated. The real error is usually
    /// at the tail after a sandbox banner, so we keep the end rather than the head.
    /// </summary>
    private static string TrimForTail(string value, int tailChars = 600)
    {
        var text = value.Trim();
        return text.Length <= tailChars ? text : "…" + text[^tailChars..];
    }

    private static string TrimForTrace(string value)
    {
        var text = value.Trim();
        return text.Length <= 1_500 ? text : string.Concat(text.AsSpan(0, 1_500), "...");
    }

    /// <summary>
    /// Best-effort persistence of a killed attempt's captured stdout/stderr
    /// next to the other attempt artifacts. Returns the path, or null when
    /// the write failed (never throws — autopsy data must not break the run).
    /// </summary>
    private static string? TryPersistKilledOutput(
        string traceDirParent, int stageNum, int attempt,
        ActivityWatchdog.Result wdResult, int firstOutputMs, int inactivityMs, string output)
    {
        try
        {
            var path = Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}.killed-output.txt");
            var reason = wdResult.Outcome switch
            {
                ActivityWatchdog.Outcome.FiredAbsoluteCeiling => "absolute_ceiling",
                ActivityWatchdog.Outcome.FiredSocketWedge => "socket_wedge",
                _ => "stall"
            };
            var header =
                $"# killed-attempt output (autopsy artifact){Environment.NewLine}" +
                $"# reason: {reason}  lastSignal: {wdResult.LastPulseSource}  silenceMs: {wdResult.SilenceMs}{Environment.NewLine}" +
                $"# firstOutputTimeoutMs: {firstOutputMs}  inactivityTimeoutMs: {inactivityMs}{Environment.NewLine}" +
                $"# capturedUtc: {DateTimeOffset.UtcNow:O}  bytes: {output.Length}{Environment.NewLine}{Environment.NewLine}";
            File.WriteAllText(path, header + output);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort persistence overload for nonzero-exit paths (no watchdog result).
    /// Writes the same stageN-attemptM.killed-output.txt artifact with a simple
    /// reason label so the autopsy trail is uniform.
    /// </summary>
    private static string? TryPersistKilledOutput(
        string traceDirParent, int stageNum, int attempt,
        string reason, string output)
    {
        try
        {
            var path = Path.Combine(traceDirParent, $"stage{stageNum}-attempt{attempt}.killed-output.txt");
            var header =
                $"# killed-attempt output (autopsy artifact){Environment.NewLine}" +
                $"# reason: {reason}{Environment.NewLine}" +
                $"# capturedUtc: {DateTimeOffset.UtcNow:O}  bytes: {output.Length}{Environment.NewLine}{Environment.NewLine}";
            File.WriteAllText(path, header + output);
            return path;
        }
        catch
        {
            return null;
        }
    }

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
                ["message"] = "corrective retry for rejected or missing JSON contract block"
            }), cancellationToken);
    }

    private Task PublishTraceAsync(StageInvocation invocation, TraceEntry entry, CancellationToken cancellationToken) =>
        _eventSink!.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "trace",
            invocation.RunId, invocation.TargetRoot, invocation.TaskName,
            invocation.Stage.Number, invocation.Tier,
            Data: new Dictionary<string, string>
            {
                ["kind"] = entry.Kind.ToString(),
                ["title"] = entry.Title,
                ["content"] = TrimForTrace(entry.Content)
            }), cancellationToken);
}
