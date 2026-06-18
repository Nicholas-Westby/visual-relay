using System.Text.RegularExpressions;
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
    // sandbox advisory dump. internal (not private) so the GUI gate
    // (MainWindowViewModel.EnsureRunnableAsync) reuses the exact same copy instead
    // of hand-copying it — both surfaces stay identical.
    internal static string MissingToolsMessage(IReadOnlyList<string> missing) =>
        $"{string.Join(" and ", missing)} is not installed or not on PATH on this machine — " +
        "Visual Relay can't run tasks here. It's set up on the VM, not this host. " +
        "Install swival and retry.";

    // Substituted when nothing survives filtering (all advisory noise or empty
    // output) so the caller never emits a dangling "swival exit 1: " with no cause
    // while still keeping its "(full output: <path>)" breadcrumb.
    private const string NoDiagnosticOutput = "(no diagnostic output captured)";

    /// <summary>
    /// Distills the real failure text from merged stdout/stderr by dropping
    /// nono's per-run advisory WARNs (lines containing <c>is blocked by '</c> and
    /// <c>use --bypass-protection</c>, which print every run regardless of the
    /// failure) and pure banner/decoration rows, then keeping the most relevant
    /// remainder. Anchoring is two-pass: first prefer a high-confidence failure
    /// marker (<c>cannot find binary path</c>, <c>command execution failed</c>,
    /// <c>command not found</c>); only when none is present fall back to
    /// word-boundary weak keywords (<c>error</c>/<c>fatal</c>/… as whole words, so
    /// a benign "loaded config with 0 errors" line is NOT mis-anchored). The reason
    /// runs from the anchor line down to the end (so a multi-line traceback
    /// survives), or the tail of the surviving lines when nothing looks like a
    /// failure, capped to <paramref name="tailChars"/>. Returns
    /// <see cref="NoDiagnosticOutput"/> when the output is nothing but noise/empty.
    /// </summary>
    internal static string ExtractFailureReason(string output, int tailChars = 600)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var strongFailure = -1;
        var weakFailure = -1;
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
            // Drop "Verified N pack(s)" — nono prints it every run regardless of outcome.
            if (VerifiedPacksLine.IsMatch(line))
                continue;
            // Drop a BARE nono advisory token line (e.g. "deny_read_user_home") that printed
            // without the full "is blocked by … use --bypass-protection" phrase (already
            // handled above). Match only a line that is ONLY such a token, so a real error
            // that merely contains the substring is never dropped.
            if (BareDenyAdvisoryLine.IsMatch(line))
                continue;
            if (strongFailure < 0 && HasStrongFailureSignal(line))
                strongFailure = kept.Count;
            if (weakFailure < 0 && HasWeakFailureSignal(line))
                weakFailure = kept.Count;
            kept.Add(line);
        }

        if (kept.Count == 0)
            return NoDiagnosticOutput;

        // Strong signal wins outright; the weak keyword pass is only a fallback so
        // benign pre-failure lines (e.g. "… 0 errors") can never lead the reason
        // when a real fatal line exists.
        var firstFailure = strongFailure >= 0 ? strongFailure : weakFailure;

        // Anchor on the failure-looking line (the start of the error block),
        // dropping the startup banner that precedes it; otherwise fall back to the
        // tail of everything that survived filtering.
        var relevant = firstFailure >= 0
            ? string.Join('\n', kept.Skip(firstFailure))
            : string.Join('\n', kept);
        return TrimForTail(relevant, tailChars);
    }

    // High-confidence markers: when present, anchor here regardless of any earlier
    // benign line that merely mentions an error keyword.
    private static bool HasStrongFailureSignal(string line) =>
        line.Contains("cannot find binary path", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("command execution failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
        // A real test failure is exactly what we want to surface. "Failed " at line
        // start matches this codebase's failing-test format (see ExtractFailureIds);
        // \bFAIL\b (uppercase) matches bun/jest "FAIL path/to/test". NOT "N fail" —
        // a benign "0 failed" summary must never anchor.
        line.StartsWith("Failed ", StringComparison.Ordinal) ||
        FailToken.IsMatch(line);

    // Weak keywords, matched only as whole words so substrings like "0 errors" in a
    // benign info line do not get mis-selected. Used only when no strong signal is
    // found anywhere in the surviving output.
    private static readonly Regex WeakFailureKeywords = new(
        @"\b(error|fatal|traceback|exception|critical)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool HasWeakFailureSignal(string line) => WeakFailureKeywords.IsMatch(line);

    private static readonly Regex VerifiedPacksLine = new(
        @"^Verified\s+\d+\s+pack\(s\)\s*$", RegexOptions.Compiled);
    // A line that is ONLY a bare nono advisory token like "deny_read_user_home".
    private static readonly Regex BareDenyAdvisoryLine = new(
        @"^deny_[a-z0-9_]+\s*$", RegexOptions.Compiled);
    // Uppercase FAIL as a whole word (bun/jest/vitest failure rows). Case-SENSITIVE
    // so "failed" inside prose / "Command execution failed" is not matched here.
    private static readonly Regex FailToken = new(
        @"\bFAIL\b", RegexOptions.Compiled);
}
