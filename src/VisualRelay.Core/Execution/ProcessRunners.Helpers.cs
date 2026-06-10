using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
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
    // swival directly. When it is enabled we run `nono` as the wrapper.
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

    internal static string BuildPrompt(StageInvocation invocation)
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

    internal static string BuildCorrectivePrompt(StageInvocation invocation, string priorOutput, string? shapeError = null)
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

    internal static string? NextTier(string tier) => tier switch
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

    internal static string TrimForError(string value)
    {
        var text = value.Trim();
        return text.Length <= 600 ? text : string.Concat(text.AsSpan(0, 600), "...");
    }

    internal static string TrimForTrace(string value)
    {
        var text = value.Trim();
        return text.Length <= 1_500 ? text : string.Concat(text.AsSpan(0, 1_500), "...");
    }
}
