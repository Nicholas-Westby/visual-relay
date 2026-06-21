namespace VisualRelay.Core.CommitLint;

/// <summary>
/// The result of preprocessing a raw commit message: the subject (line 0) and
/// the remaining body lines, after comment, trailer, and blank-line stripping.
/// </summary>
/// <param name="Subject">The first non-comment line.</param>
/// <param name="BodyLines">Body lines after the subject (may be empty). Blank
/// lines between bullets are preserved so the blank-line-before-body rule and
/// the prose-line rule can both run.</param>
public readonly record struct PreprocessedMessage(string Subject, IReadOnlyList<string> BodyLines);

/// <summary>
/// Strips comments, the trailing trailer block, and trailing blanks from a raw
/// commit message — exactly as git and ai-sorcery's reference do — leaving the
/// subject plus body. Pure and IO-free.
/// </summary>
public static class CommitMessagePreprocessor
{
    /// <summary>
    /// Preprocess <paramref name="message"/>: (1) drop <c>#</c> comment lines,
    /// (2) strip trailing blank lines, (3) pop the trailing trailer block,
    /// (4) strip trailing blanks again. Returns subject + body lines.
    /// </summary>
    public static PreprocessedMessage Preprocess(string message)
    {
        var lines = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !line.StartsWith('#'))
            .ToList();

        StripTrailingBlanks(lines);
        StripTrailerBlock(lines);
        StripTrailingBlanks(lines);

        if (lines.Count == 0)
            return new PreprocessedMessage(string.Empty, []);

        var subject = lines[0];
        var body = lines.Skip(1).ToList();
        return new PreprocessedMessage(subject, body);
    }

    private static void StripTrailingBlanks(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
    }

    private static void StripTrailerBlock(List<string> lines)
    {
        // Repeatedly pop the last line while it matches the trailer pattern.
        // Never pop the subject (index 0): a subject that happens to look like
        // a trailer is still the subject.
        while (lines.Count > 1 && CommitRules.TrailerLine.IsMatch(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
    }
}
