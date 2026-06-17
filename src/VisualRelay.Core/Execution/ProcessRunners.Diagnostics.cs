using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Pure, testable run-diagnostics seams: probing required tools against PATH and
// distilling a legible failure reason out of nono's noisy per-run output. Kept
// separate from ProcessRunners.Helpers.cs so each partial stays under the
// file-size guard and the diagnostics logic is cohesive.
public sealed partial class SwivalSubagentRunner
{
    // Returns the required launch tools that do NOT resolve against PATH (empty
    // ⇒ all present). swival is always required; nono is required only when the
    // sandbox is on (BypassSandbox == false), because that is when nono wraps
    // swival (see BuildLaunchTarget). PATH and the binary names are injectable so
    // a test can simulate missing/present without touching the real PATH; callers
    // probe the same PATH the launch uses (Environment.GetEnvironmentVariable).
    public static IReadOnlyList<string> MissingRequiredTools(
        RelayConfig config,
        string? pathValue = null,
        string swivalBinary = "swival",
        string nonoBinary = NonoBinary)
    {
        var pathDirs = (pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        bool OnPath(string name) =>
            // A bare path component (no directory) is taken as a cwd-relative
            // executable, mirroring how the process launcher resolves it.
            Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar)
                ? File.Exists(name)
                : pathDirs.Any(dir => File.Exists(Path.Combine(dir, name)));

        var missing = new List<string>(2);
        if (!OnPath(swivalBinary))
            missing.Add(swivalBinary);
        if (!config.BypassSandbox && !OnPath(nonoBinary))
            missing.Add(nonoBinary);
        return missing;
    }

    // Actionable, user-facing message for the fail-fast tool-presence gate. Names
    // the real cause (a missing binary on this host) so the user never sees the
    // sandbox advisory dump.
    private static string MissingToolsMessage(IReadOnlyList<string> missing) =>
        $"{string.Join(" and ", missing)} is not installed or not on PATH on this machine — " +
        "Visual Relay can't run tasks here. It's set up on the VM, not this host. " +
        "Install swival and retry.";

    /// <summary>
    /// Distills the real failure text from merged stdout/stderr by dropping
    /// nono's per-run advisory WARNs (lines containing <c>is blocked by '</c> and
    /// <c>use --bypass-protection</c>, which print every run regardless of the
    /// failure) and pure banner/decoration rows, then keeping the most relevant
    /// remainder: from the FIRST line that looks like the real failure
    /// (<c>cannot find binary path</c>, <c>Command execution failed</c>, an error)
    /// down to the end (so a multi-line traceback survives), or the tail of the
    /// surviving lines when nothing looks like a failure. The result is capped to
    /// <paramref name="tailChars"/>. This ensures the advisory red herrings (e.g.
    /// the <c>.envrc</c> / <c>deny_shell_configs</c> line) and the startup banner
    /// never lead the surfaced reason. Returns empty when the output is nothing but
    /// noise.
    /// </summary>
    internal static string ExtractFailureReason(string output, int tailChars = 600)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var firstFailure = -1;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            // Drop nono's standard per-run advisories.
            if (line.Contains("is blocked by '", StringComparison.Ordinal) &&
                line.Contains("use --bypass-protection", StringComparison.Ordinal))
                continue;
            // Drop pure banner/decoration rows (rules, box-drawing, separators).
            if (line.Trim('=', '-', '─', '━', '•', '*', ' ', '\t').Length == 0)
                continue;
            if (firstFailure < 0 && LooksLikeFailure(line))
                firstFailure = kept.Count;
            kept.Add(line);
        }

        if (kept.Count == 0)
            return string.Empty;

        // Anchor on the first failure-looking line (the start of the error block),
        // dropping the startup banner that precedes it; otherwise fall back to the
        // tail of everything that survived filtering.
        var relevant = firstFailure >= 0
            ? string.Join('\n', kept.Skip(firstFailure))
            : string.Join('\n', kept);
        return TrimForTail(relevant, tailChars);
    }

    private static bool LooksLikeFailure(string line) =>
        line.Contains("cannot find binary path", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("command execution failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("traceback", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("critical", StringComparison.OrdinalIgnoreCase);
}
