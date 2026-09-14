namespace VisualRelay.Core.Execution;

/// <summary>
/// Keeps a flag reason to one line. A reason travels into NEEDS-REVIEW, run.log, status.json and
/// the drain log; measured on commons-lang, a reaped test run's reason carried its whole 80 KB
/// Maven log into all four. The first line stays the reason and the rest joins the details, except
/// the hint <see cref="Domain.ErrorHintClassifier.WithHint"/> ends a raw error with, which is
/// guidance for the reviewer and stays in the reason.
/// </summary>
internal static class FlagReason
{
    /// <summary>The longest reason kept; a longer first line is cut and its rest moved.</summary>
    internal const int MaxChars = 500;

    /// <summary>
    /// The reason's first line (cut to <see cref="MaxChars"/>), and the details: whatever the
    /// reason carried past that, ahead of the <paramref name="details"/> the caller already had.
    /// </summary>
    internal static (string Reason, string? Details) Split(string reason, string? details)
    {
        var text = reason.Trim();
        var hintStart = text.LastIndexOf("\n\nHint: ", StringComparison.Ordinal);
        var hint = hintStart < 0 ? null : OneLine(text[(hintStart + 2)..].ReplaceLineEndings(" "));
        if (hintStart >= 0)
            text = text[..hintStart].TrimEnd();
        var newline = text.IndexOf('\n');
        var head = (newline < 0 ? text : text[..newline]).TrimEnd('\r', ' ');
        var rest = newline < 0 ? string.Empty : text[(newline + 1)..].Trim();
        if (head.Length > MaxChars)
        {
            rest = rest.Length == 0 ? head[MaxChars..] : head[MaxChars..] + "\n" + rest;
            head = head[..MaxChars] + "…";
        }

        if (hint is not null)
            head += " " + hint;
        if (rest.Length == 0)
            return (head, details);
        return (head, string.IsNullOrWhiteSpace(details) ? rest : rest + "\n\n" + details);
    }

    /// <summary>One line of at most <see cref="MaxChars"/> characters, for a log that writes a line per event.</summary>
    internal static string OneLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        var head = (newline < 0 ? trimmed : trimmed[..newline]).TrimEnd('\r', ' ');
        return head.Length > MaxChars ? head[..MaxChars] + "…" : head;
    }
}
