using System.Text;

namespace VisualRelay.Core.Authorship;

/// <summary>
/// Removes any commit-message <em>trailer</em> that mentions "Claude" so an
/// authored history carries no machine attribution. Pure (no I/O).
/// </summary>
/// <remarks>
/// <para>
/// Only the message's <strong>last paragraph</strong> — the final run of
/// non-blank lines that is preceded by a blank line — is considered, and only
/// when every line in it is a git trailer (<c>Key: value</c>) or a folded
/// continuation (a line beginning with whitespace). A single-line or
/// no-blank-line message therefore has <em>no</em> trailer block, which
/// protects Conventional Commit subjects (e.g. <c>feat(x): y</c>) from being
/// mistaken for trailers.
/// </para>
/// <para>
/// A trailer is dropped — its key line and all folded continuation lines —
/// when the trailer's key <strong>or value</strong> contains "claude"
/// (<see cref="StringComparison.OrdinalIgnoreCase"/>). Per the
/// "any trailer mentioning Claude" rule this is intentional even when only the
/// value matches: a non-Claude key such as
/// <c>Reviewed-by: claude-fan &lt;x@y&gt;</c> IS removed. Prose in the body that
/// mentions Claude is NOT removed because it is not in the trailer block.
/// Surviving trailers keep their original order and exact text; if removals
/// empty the trailer block the now-trailing blank separator line(s) are dropped.
/// The result ends with a single trailing newline.
/// </para>
/// </remarks>
public static class ClaudeTrailerStripper
{
    private const string Needle = "claude";

    /// <summary>
    /// Returns <paramref name="message"/> with every Claude-mentioning trailer
    /// removed from its trailing trailer block, or the message unchanged when it
    /// has no trailer block or no Claude trailer.
    /// </summary>
    public static string Strip(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Work on logical lines without the line terminators; remember whether
        // the original ended with a newline so we can restore exactly one.
        var lines = message.Split('\n');

        // A trailing '\n' produces a final empty element; drop it and treat the
        // message as newline-terminated (the normal git case).
        var hadTrailingNewline = lines.Length > 0 && lines[^1].Length == 0;
        var body = hadTrailingNewline ? lines[..^1] : lines;
        if (body.Length == 0)
            return message;

        var blockStart = FindTrailerBlockStart(body);
        if (blockStart < 0)
            return message;

        var kept = StripClaudeTrailers(body, blockStart);
        if (kept is null)
            return message; // nothing removed — byte-preserve the input.

        return Render(body, blockStart, kept, hadTrailingNewline);
    }

    /// <summary>
    /// Returns the index of the first line of the trailing trailer block, or -1
    /// when the message has no trailer block. The block is the final run of
    /// non-blank lines that is preceded by a blank line and whose lines are all
    /// trailers or folded continuations.
    /// </summary>
    private static int FindTrailerBlockStart(string[] body)
    {
        // Walk back over the final run of non-blank lines.
        var end = body.Length - 1;
        if (body[end].Length == 0)
            return -1; // trailing blank line(s) — no trailer paragraph.

        var start = end;
        while (start > 0 && body[start - 1].Length != 0)
            start--;

        // The paragraph must be preceded by a blank line; a message whose only
        // paragraph is the subject (start == 0) has no trailer block, so a
        // colon-bearing subject is never treated as a trailer.
        if (start == 0)
            return -1;

        // Every line in the paragraph must be a trailer or a folded continuation,
        // and the first line must be a trailer (not a fold).
        if (!IsTrailerStart(body[start]))
            return -1;
        for (var i = start; i <= end; i++)
        {
            if (!IsTrailerStart(body[i]) && !IsFoldedContinuation(body[i]))
                return -1;
        }

        return start;
    }

    /// <summary>
    /// Rebuilds the trailer block keeping only non-Claude trailers, returning the
    /// surviving lines, or <c>null</c> when nothing was removed.
    /// </summary>
    private static List<string>? StripClaudeTrailers(string[] body, int blockStart)
    {
        var kept = new List<string>();
        var removedAny = false;
        var i = blockStart;
        while (i < body.Length)
        {
            var trailerEnd = i + 1;
            while (trailerEnd < body.Length && IsFoldedContinuation(body[trailerEnd]))
                trailerEnd++;

            if (TrailerMentionsClaude(body, i, trailerEnd))
            {
                removedAny = true;
            }
            else
            {
                for (var j = i; j < trailerEnd; j++)
                    kept.Add(body[j]);
            }

            i = trailerEnd;
        }

        return removedAny ? kept : null;
    }

    private static string Render(string[] body, int blockStart, List<string> kept, bool hadTrailingNewline)
    {
        var sb = new StringBuilder();

        // Everything before the trailer block is byte-preserved.
        var prefixEnd = blockStart;

        // If the trailer block is now empty, also drop the blank separator
        // line(s) that immediately preceded it.
        if (kept.Count == 0)
        {
            while (prefixEnd > 0 && body[prefixEnd - 1].Length == 0)
                prefixEnd--;
        }

        for (var i = 0; i < prefixEnd; i++)
        {
            sb.Append(body[i]);
            sb.Append('\n');
        }

        foreach (var line in kept)
        {
            sb.Append(line);
            sb.Append('\n');
        }

        var result = sb.ToString();

        // Honor the input's terminator policy: when the input had no trailing
        // newline, drop the one we appended to the last emitted line.
        if (!hadTrailingNewline && result.Length > 0 && result[^1] == '\n')
            result = result[..^1];

        return result;
    }

    private static bool TrailerMentionsClaude(string[] body, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (body[i].Contains(Needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the line starts a trailer: <c>^[A-Za-z0-9][A-Za-z0-9-]*:\s</c>.
    /// </summary>
    private static bool IsTrailerStart(string line)
    {
        if (line.Length == 0 || !IsKeyHeadChar(line[0]))
            return false;

        var i = 1;
        while (i < line.Length && IsKeyTailChar(line[i]))
            i++;

        // Require the colon immediately after the key, then a whitespace char.
        return i < line.Length
            && line[i] == ':'
            && i + 1 < line.Length
            && char.IsWhiteSpace(line[i + 1]);
    }

    private static bool IsFoldedContinuation(string line) =>
        line.Length > 0 && char.IsWhiteSpace(line[0]);

    private static bool IsKeyHeadChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsKeyTailChar(char c) => IsKeyHeadChar(c) || c == '-';
}
