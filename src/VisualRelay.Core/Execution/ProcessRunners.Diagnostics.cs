using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution;

// Run-diagnostics seams: distilling a legible failure reason out of nono's noisy
// per-run output. Tool-presence probing (MissingRequiredTools / MissingToolsMessage)
// lives in ProcessRunners.ToolPresence.cs.
public sealed partial class SwivalSubagentRunner
{
    // Substituted when nothing survives filtering (all advisory noise or empty
    // output) so the caller never emits a dangling "swival exit 1: " with no cause
    // while still keeping its "(full output: <path>)" breadcrumb.
    private const string NoDiagnosticOutput = "(no diagnostic output captured)";

    /// <summary>
    /// Distills the real failure text from merged stdout/stderr by dropping
    /// nono's per-run advisory WARNs (lines containing <c>is blocked by '</c> and
    /// <c>use --bypass-protection</c>, which print every run regardless of the
    /// failure), nono's standing system-services / keychain advisory and its
    /// remediation hints (<see cref="IsNonoSystemServiceAdvisory"/>, which trail
    /// AFTER the test summary), and pure banner/decoration rows, then keeping the
    /// most relevant remainder. Anchoring is two-pass: first prefer a high-confidence failure
    /// marker (<c>cannot find binary path</c>, <c>command execution failed</c>,
    /// <c>command not found</c>); only when none is present fall back to
    /// word-boundary weak keywords (<c>error</c>/<c>fatal</c>/… as whole words, so
    /// a benign "loaded config with 0 errors" line is NOT mis-anchored). The reason
    /// runs from the anchor line down to the end (so a multi-line traceback
    /// survives), or the tail of the surviving lines when nothing looks like a
    /// failure, capped to <paramref name="tailChars"/>. Returns
    /// <see cref="NoDiagnosticOutput"/> when the output is nothing but noise/empty.
    /// </summary>
    internal static string ExtractFailureReason(string output, int tailChars = 600) =>
        DistillFailure(output, tailChars).Reason;

    // Leads the reason when swival yields no usable diagnostic and the failure is
    // (or is presumed) a model-backend error — see BuildNonzeroExitReason.
    private const string ModelCallFailedPrefix = "model call failed";

    /// <summary>
    /// Builds the user-facing reason for a swival nonzero exit. When swival's output
    /// is a usable diagnostic (a failure marker, or a real non-echo line) it is
    /// surfaced and the proxy is NOT consulted (so the result never depends on machine
    /// proxy state). Only when swival yields no usable diagnostic — its tail is the
    /// echoed PROMPT (the ground-truth incident) or the no-output placeholder — does it
    /// consult the litellm proxy log for a model-backend error (auth / HTTP / rate-limit)
    /// and lead with "model call failed — &lt;cause&gt;", or, absent a proxy cause, say a
    /// model call likely failed and point at the proxy log rather than parroting the
    /// prompt. <paramref name="promptText"/> is the prompt the runner sent (to detect an
    /// echo); the <paramref name="killedOutputPath"/> breadcrumb is appended when present.
    /// </summary>
    internal static string BuildNonzeroExitReason(
        int exitCode, string swivalOutput, string promptText, string? proxyLogText, string? killedOutputPath)
    {
        var (distilled, hasMarker) = DistillFailure(swivalOutput, tailChars: 600);

        string core;
        if (hasMarker || IsUsableSwivalDiagnostic(distilled, promptText))
        {
            // swival's output is a usable diagnostic — surface it, do not consult the proxy.
            core = $"swival exit {exitCode}: {distilled}";
        }
        else if (IsProcessLaunchFailureExitCode(exitCode))
        {
            // The process could not START (DLL-init / image-load NTSTATUS): it never ran,
            // so no model call was made. Blaming the model backend here misdirects
            // debugging — name the launch failure and the escape hatch instead.
            core = $"swival exit {exitCode} (0x{(uint)exitCode:X8}): {ProcessLaunchFailedHint}";
        }
        else
        {
            // No usable diagnostic (prompt echo / no output). Surface the real
            // model-backend cause from the proxy log instead of echoing the prompt.
            var proxyReason = ExtractProxyLogReason(proxyLogText ?? string.Empty);
            core = proxyReason is not null
                ? $"swival exit {exitCode}: {ModelCallFailedPrefix} — {proxyReason}"
                : $"swival exit {exitCode}: {ModelCallFailedPrefix} — swival produced no diagnostic " +
                  $"output and the model backend rejected or failed the request. Check the litellm " +
                  $"proxy log ({BackendPaths.Resolve().LogFile}).";
        }

        return killedOutputPath is not null ? $"{core} (full output: {killedOutputPath})" : core;
    }

    // True when swival's distilled tail is a real diagnostic worth surfacing — i.e.
    // it is neither the no-output placeholder nor merely our own prompt echoed back.
    // A prompt echo is detected structurally (no machine state): the distilled tail's
    // non-empty lines all appear in the prompt the runner sent, so swival returned
    // the prompt instead of a result. Kept conservative — any line NOT from the
    // prompt makes it a usable diagnostic (so genuine output is never suppressed).
    private static bool IsUsableSwivalDiagnostic(string distilled, string promptText)
    {
        if (string.Equals(distilled, NoDiagnosticOutput, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(promptText))
            return true;

        var normalizedPrompt = promptText.Replace("\r\n", "\n");
        foreach (var line in distilled.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!normalizedPrompt.Contains(trimmed, StringComparison.Ordinal))
                return true; // a line swival produced that we did NOT send — a real diagnostic
        }

        return false; // every surviving line came from our prompt — a prompt echo
    }

    // nono's standing system-services / keychain advisory and its remediation hint
    // lines (the "Next steps:" block and bare --allow/--read/--write flag suggestions
    // it emits). Matched by nono's own distinctive wording and CLI-flag hint shapes —
    // never by any test-framework output — so the distilled reason reflects the test
    // command's failure and never nono's per-run keychain chatter. <paramref name="line"/>
    // is already trimmed, so indented hint lines match by their leading token.
    private static bool IsNonoSystemServiceAdvisory(string line) =>
        line.StartsWith("system services:", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("mach-lookup (com.apple.SecurityServer)", StringComparison.Ordinal) ||
        line.Contains("Keychain access requires granting the login keychain path", StringComparison.Ordinal) ||
        line.Contains("Library/Keychains", StringComparison.Ordinal) ||
        line.StartsWith("Next steps:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Discover paths:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Query policy:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("nono learn", StringComparison.Ordinal) ||
        line.StartsWith("nono why", StringComparison.Ordinal) ||
        NonoFlagHintLine.IsMatch(line);

    /// <summary>
    /// Consults the litellm proxy-log text for a recent MODEL-BACKEND failure
    /// (auth / HTTP 4xx-5xx / rate-limit) — the class of error that lives ONLY in
    /// the proxy log and never reaches swival's own merged stdout/stderr. Used as a
    /// fallback by the nonzero-exit path when <see cref="ExtractFailureReason"/>
    /// found no marker in swival's output, so the user sees the real cause (a failed
    /// model call) instead of a prompt echo. Scans only the tail
    /// (<paramref name="tailLines"/>) so a long-lived proxy's earlier traffic can't
    /// resurface a stale error. Returns the most recent failure line(s), or
    /// <c>null</c> when the tail shows only healthy traffic (so a healthy proxy
    /// never invents a cause). Pure: text in, text out.
    /// </summary>
    internal static string? ExtractProxyLogReason(string proxyLogText, int tailLines = 200, int tailChars = 400)
    {
        if (string.IsNullOrWhiteSpace(proxyLogText))
            return null;

        var lines = proxyLogText.Replace("\r\n", "\n").Split('\n');
        // Restrict to the recent tail so a long-lived proxy's earlier traffic can't
        // resurface a stale error, then anchor on the FIRST failure-looking line in
        // that window: litellm logs the descriptive error (e.g. AuthenticationError)
        // BEFORE the matching uvicorn access-line status (… "POST …" 401), so
        // anchoring on the first keeps both the named cause and its status code.
        var start = Math.Max(0, lines.Length - tailLines);
        var firstFailure = -1;
        for (var i = start; i < lines.Length; i++)
        {
            if (HasProxyFailureSignal(lines[i]))
            {
                firstFailure = i;
                break;
            }
        }

        if (firstFailure < 0)
            return null;

        // Keep the failure line plus any following lines (a multi-line litellm
        // traceback + the access-line status) capped to the tail-char budget.
        var relevant = string.Join('\n', lines.Skip(firstFailure)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));
        return TrimForTail(relevant, tailChars);
    }

    // A proxy-log line that signals a MODEL-BACKEND failure. Anchors on litellm's
    // own error names and on an HTTP status in the 4xx/5xx range as logged in the
    // uvicorn access line (… "POST /v1/chat/completions HTTP/1.1" 401). A healthy
    // "… 200"/"… 204" access line, or an INFO line that merely contains the digits,
    // is NOT matched — the status regex requires the request-line + quote + space
    // shape so a random "401" inside a message body can't false-positive.
    private static bool HasProxyFailureSignal(string line) =>
        line.Contains("AuthenticationError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("RateLimitError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("PermissionDeniedError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Invalid credentials", StringComparison.OrdinalIgnoreCase) ||
        HttpErrorStatusLine.IsMatch(line);

    // Matches the trailing HTTP status of a uvicorn access line, e.g.
    // …\" HTTP/1.1\" 401  or  …\" HTTP/1.1\\\" 502 — a 4xx or 5xx only. The escaped
    // closing quote (\" in JSON-encoded logs) plus a space precede the status, so a
    // success ("… 200") and any bare 3-digit number elsewhere are not matched.
    private static readonly Regex HttpErrorStatusLine = new(
        @"HTTP/\d(?:\.\d)?\\?""\s+(?:4\d{2}|5\d{2})\b", RegexOptions.Compiled);

    private static readonly Regex VerifiedPacksLine = new(
        @"^Verified\s+\d+\s+pack\(s\)\s*$", RegexOptions.Compiled);
    // A line that is ONLY a bare nono advisory token like "deny_read_user_home".
    private static readonly Regex BareDenyAdvisoryLine = new(
        @"^deny_[a-z0-9_]+\s*$", RegexOptions.Compiled);
    // A bare nono CLI-flag remediation hint, e.g. "--allow ~/…", "--read-file ~/…",
    // "--write ~/…". Anchored at line start (line is pre-trimmed) so a real diagnostic
    // that merely contains such a token mid-line is never dropped.
    private static readonly Regex NonoFlagHintLine = new(
        @"^--(allow|read|write)\b", RegexOptions.Compiled);
}
