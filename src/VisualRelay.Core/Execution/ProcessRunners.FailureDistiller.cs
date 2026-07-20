using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution;

// Failure-distillation helpers split from ProcessRunners.Diagnostics.cs to stay
// under the 300-line file guard. Extended with Swift Testing concurrent-runner
// markers and head-first extraction when anchored — the failure line must be the
// first line of the distilled reason so the GUI banner, NEEDS-REVIEW headline,
// and flag reason actually name the failing test instead of a mid-word fragment
// of a passing-test duration line.
public sealed partial class SwivalSubagentRunner
{
    // Shared core for ExtractFailureReason and BuildNonzeroExitReason. Returns the
    // distilled reason AND whether it anchored on a genuine failure marker
    // (strong/weak signal) rather than falling back to the tail / placeholder. The
    // flag is what BuildNonzeroExitReason uses to decide whether swival's own output
    // is diagnostic, or whether it must consult the proxy log instead of echoing a
    // tail that is really just the prompt.
    private static (string Reason, bool HasMarker) DistillFailure(string output, int tailChars)
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
            // Drop nono's STANDING system-services / keychain advisory and its remediation
            // hint lines. Unlike the "is blocked by … use --bypass-protection" WARNs above,
            // nono prints this every run regardless of outcome AND trails it AFTER the test
            // command's own summary, so without dropping it the tail lands on the advisory
            // instead of the real failure. This is VR's own sandbox layer's output, so
            // filtering it is provider-agnostic (no test framework is parsed).
            if (IsNonoSystemServiceAdvisory(line))
                continue;
            if (strongFailure < 0 && HasStrongFailureSignal(line))
                strongFailure = kept.Count;
            if (weakFailure < 0 && HasWeakFailureSignal(line))
                weakFailure = kept.Count;
            kept.Add(line);
        }

        if (kept.Count == 0)
            return (NoDiagnosticOutput, false);

        // Strong signal wins outright; the weak keyword pass is only a fallback so
        // benign pre-failure lines (e.g. "… 0 errors") can never lead the reason
        // when a real fatal line exists.
        var firstFailure = strongFailure >= 0 ? strongFailure : weakFailure;

        // Anchor on the failure-looking line (the start of the error block),
        // dropping the startup banner that precedes it; otherwise fall back to
        // the tail of everything that survived filtering.
        var relevant = firstFailure >= 0
            ? string.Join('\n', kept.Skip(firstFailure))
            : string.Join('\n', kept);

        // When anchored, keep the HEAD so the failure line appears FIRST — a
        // concurrent-runner log can have hundreds of passing lines after the
        // failure, so tail-keeping would evict the anchor line. The unanchored
        // fallback preserves the existing tail behavior (TrimForTail).
        var distilled = firstFailure >= 0
            ? TrimForHead(relevant, tailChars)
            : TrimForTail(relevant, tailChars);

        return (distilled, firstFailure >= 0);
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
        FailToken.IsMatch(line) ||
        // Swift Testing concurrent-runner failure rows. "recorded an issue" and
        // "Expectation failed" are unique to real test failures — a passing test
        // never prints these. The regex matches "failed after N.N seconds with N
        // issue(s)" and requires at least one digit before "issue", so a
        // hypothetical "0 issues" / "0 failed" summary cannot anchor.
        line.Contains("recorded an issue", StringComparison.Ordinal) ||
        line.Contains("Expectation failed", StringComparison.Ordinal) ||
        SwiftTestFailureRow.IsMatch(line);

    // Weak keywords, matched only as whole words so substrings like "0 errors" in a
    // benign info line do not get mis-selected. Used only when no strong signal is
    // found anywhere in the surviving output.
    private static readonly Regex WeakFailureKeywords = new(
        @"\b(error|fatal|traceback|exception|critical)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool HasWeakFailureSignal(string line) => WeakFailureKeywords.IsMatch(line);

    // Uppercase FAIL as a whole word (bun/jest/vitest failure rows). Case-SENSITIVE
    // so "failed" inside prose / "Command execution failed" is not matched here.
    private static readonly Regex FailToken = new(
        @"\bFAIL\b", RegexOptions.Compiled);

    // Matches Swift Testing concurrent-runner failure-summary rows like
    // "Test run with 244 tests in 25 suites failed after 0.437 seconds with 1 issue."
    // The \b before "failed" prevents matching inside a word; the \d+ before "issue"
    // requires at least one digit so "0 issue" cannot anchor.
    private static readonly Regex SwiftTestFailureRow = new(
        @"\bfailed\s+after\s+\d+(?:\.\d+)?\s+seconds?\s+with\s+\d+\s+issue",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the HEAD of <paramref name="value"/> (first <paramref name="headChars"/>
    /// characters), appended with "…" when truncated. Used when an anchor line exists
    /// so the failure line itself is the first line of the distilled reason — critical
    /// for concurrent-runner output where the failure is mid-log followed by hundreds
    /// of passing lines.
    /// </summary>
    private static string TrimForHead(string value, int headChars)
    {
        var text = value.Trim();
        return text.Length <= headChars ? text : text[..headChars] + "…";
    }
}
