using System.Text;

namespace VisualRelay.Core.Init;

/// <summary>
/// Structured diagnostic captured when a setup/validation test-command run
/// fails outside a task pipeline (init/Create-config validation,
/// EnsureRunnableAsync pre-run gate, baseline verify). Exposed through the
/// control API /state endpoint so every failure is diagnosable without
/// inspecting the filesystem, and persisted as .relay/setup-check.log so the
/// full output (tail-truncated) is preserved.
/// </summary>
public sealed record SetupCheckDiagnostic(
    string Command,
    string Cwd,
    int TimeoutMs,
    int ExitCode,
    bool TimedOut,
    string? OutputTail,
    string? ArtifactPath,
    DateTimeOffset CapturedUtc)
{
    private const int ArtifactTailCapBytes = 64 * 1024; // 64 KB
    private const int StateTailCapChars = 4096;         // 4 KB for /state

    /// <summary>
    /// One short, generic hint derived from the failure shape so the user
    /// can act without language-specific knowledge.
    /// </summary>
    public string Hint => TimedOut
        ? $"the command exceeded {TimeoutMs / 1000}s; raise testTimeoutMs in .relay/config.json or verify the command finishes non-interactively in this environment"
        : ExitCode == 127
            ? "the command's binary was not found on VR's PATH — VR runs commands through its own shell environment, which can differ from your terminal"
            : $"the command exited with code {ExitCode}; check that the test runner is installed and the command is correct";

    /// <summary>
    /// Truncates <paramref name="output"/> to <see cref="StateTailCapChars"/>
    /// (4 KB) for embedding in the /state JSON. Returns null when the input
    /// is null/empty.
    /// </summary>
    public static string? CapForState(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;
        if (output.Length <= StateTailCapChars)
            return output;
        return "[…truncated…]\n" + output[^StateTailCapChars..];
    }

    /// <summary>
    /// Writes <c>.relay/setup-check.log</c> with the full diagnostic and
    /// tail-truncated output. Overwrites per attempt — this is a diagnostic
    /// scratch file, not history. Returns the absolute path or null on failure.
    /// </summary>
    public static string? WriteArtifact(
        string rootPath,
        string command,
        string cwd,
        int timeoutMs,
        int exitCode,
        bool timedOut,
        string output)
    {
        try
        {
            var relayDir = Path.Combine(rootPath, ".relay");
            Directory.CreateDirectory(relayDir);
            var path = Path.GetFullPath(Path.Combine(relayDir, "setup-check.log"));

            var sb = new StringBuilder();
            sb.AppendLine($"capturedUtc: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine($"command: {command}");
            sb.AppendLine($"cwd: {cwd}");
            sb.AppendLine($"timeoutMs: {timeoutMs}");
            sb.AppendLine($"exitCode: {exitCode}");
            sb.AppendLine($"timedOut: {timedOut}");
            sb.AppendLine();

            var tail = output;
            var truncated = false;
            if (output.Length > ArtifactTailCapBytes)
            {
                tail = output[^ArtifactTailCapBytes..];
                truncated = true;
            }

            if (truncated)
                sb.AppendLine("[…output truncated to last 64 KB…]");
            sb.Append(tail);

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="SetupCheckDiagnostic"/> from a failed validation,
    /// persisting the artifact and capping the output tail for /state.
    /// The <paramref name="allRejections"/> list (if non-empty) is recorded as
    /// one section per candidate in the artifact; the diagnostic itself
    /// represents the LAST (decisive) failure.
    /// </summary>
    public static SetupCheckDiagnostic FromFailedValidation(
        string rootPath,
        string command,
        int timeoutMs,
        int exitCode,
        bool timedOut,
        string output,
        IReadOnlyList<(string Candidate, string Reason, int ExitCode, bool TimedOut, string Output)>? allRejections = null)
    {
        var artifactPath = allRejections is { Count: > 0 }
            ? WriteMultiCandidateArtifact(rootPath, timeoutMs, allRejections)
            : WriteArtifact(rootPath, command, rootPath, timeoutMs, exitCode, timedOut, output);
        return new SetupCheckDiagnostic(
            Command: command,
            Cwd: rootPath,
            TimeoutMs: timeoutMs,
            ExitCode: exitCode,
            TimedOut: timedOut,
            OutputTail: CapForState(output),
            ArtifactPath: artifactPath,
            CapturedUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Writes <c>.relay/setup-check.log</c> with one section per rejected
    /// candidate, then the decisive failure at the end. Overwrites per attempt.
    /// </summary>
    private static string? WriteMultiCandidateArtifact(
        string rootPath,
        int timeoutMs,
        IReadOnlyList<(string Candidate, string Reason, int ExitCode, bool TimedOut, string Output)> candidates)
    {
        try
        {
            var relayDir = Path.Combine(rootPath, ".relay");
            Directory.CreateDirectory(relayDir);
            var path = Path.GetFullPath(Path.Combine(relayDir, "setup-check.log"));

            var sb = new StringBuilder();
            sb.AppendLine($"capturedUtc: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine($"timeoutMs: {timeoutMs}");
            sb.AppendLine($"candidatesTried: {candidates.Count}");
            sb.AppendLine();

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                sb.AppendLine($"--- Candidate {i + 1}/{candidates.Count} ---");
                sb.AppendLine($"command: {c.Candidate}");
                sb.AppendLine($"exitCode: {c.ExitCode}");
                sb.AppendLine($"timedOut: {c.TimedOut}");
                sb.AppendLine($"reason: {c.Reason}");

                var tail = c.Output;
                var truncated = false;
                if (tail.Length > ArtifactTailCapBytes)
                {
                    tail = tail[^ArtifactTailCapBytes..];
                    truncated = true;
                }

                if (truncated)
                    sb.AppendLine("[…output truncated to last 64 KB…]");
                sb.AppendLine("--- output ---");
                sb.AppendLine(tail);
                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }
}
