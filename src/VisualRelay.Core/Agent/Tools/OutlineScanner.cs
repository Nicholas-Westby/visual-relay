using System.Text.RegularExpressions;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The line-based declaration scanner behind <c>outline</c>.
///
/// It is deliberately NOT a parser. <c>VisualRelay.Core</c> carries no package
/// references at all — Roslyn lives only in <c>tools/VisualRelay.Guards</c> —
/// and pulling <c>Microsoft.CodeAnalysis.CSharp</c> into the shipped core to
/// summarise a file would be a large dependency bought for one tool, and would
/// still only understand C#. Matching declaration-shaped lines instead works on
/// every language a target repository might be written in, at the cost of the
/// occasional false positive, which an outline can afford.
/// </summary>
internal static partial class OutlineScanner
{
    private const int EntryChars = 160;
    private const int MaxDepth = 8;

    /// <summary>Scans a file's text for declaration-shaped lines.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="extension">The file extension, lower-cased, including the dot.</param>
    /// <param name="maxEntries">How many entries to collect before stopping.</param>
    /// <param name="totalLines">Receives the file's line count.</param>
    /// <returns>Line number, nesting depth and the declaration text, in file order.</returns>
    internal static List<(int Line, int Depth, string Text)> Scan(
        string text, string extension, int maxEntries, out int totalLines)
    {
        var entries = new List<(int Line, int Depth, string Text)>();
        var lines = text.Split('\n');
        totalLines = lines.Length;
        var markdown = extension is ".md" or ".markdown" or ".mdx";

        for (var index = 0; index < lines.Length && entries.Count < maxEntries; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            var entry = markdown ? Heading(line) : Declaration(line);
            if (entry is null) continue;

            entries.Add((index + 1, entry.Value.Depth, entry.Value.Text));
        }

        return entries;
    }

    private static (int Depth, string Text)? Heading(string line)
    {
        var match = HeadingPattern().Match(line);
        return match.Success
            ? (Math.Min(match.Groups["hashes"].Value.Length - 1, MaxDepth), Clean(line))
            : null;
    }

    private static (int Depth, string Text)? Declaration(string line)
    {
        if (!StructuralPattern().IsMatch(line)
            && !MemberPattern().IsMatch(line)
            && !FunctionPattern().IsMatch(line))
            return null;

        return (Indent(line), Clean(line));
    }

    private static int Indent(string line)
    {
        var columns = 0;
        foreach (var character in line)
        {
            if (character == ' ') columns++;
            else if (character == '\t') columns += 4;
            else break;
        }

        return Math.Min(columns / 4, MaxDepth);
    }

    private static string Clean(string line)
    {
        var trimmed = line.Trim().TrimEnd('{').TrimEnd();
        return trimmed.Length <= EntryChars ? trimmed : trimmed[..EntryChars] + " …";
    }

    /// <summary>Markdown headings: the outline of a document.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+\S")]
    private static partial Regex HeadingPattern();

    /// <summary>Type-shaped declarations across the C, JVM, Go, Rust and script families.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(
        @"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|final|export|"
        + @"default|declare|open|data|pub|const|file)\s+)*"
        + @"(?:namespace|class|struct|interface|enum|record|delegate|trait|impl|module|package|type|object)\b")]
    private static partial Regex StructuralPattern();

    /// <summary>Members declared with an explicit access modifier, as C# and Java write them.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|protected|internal)\s+\S")]
    private static partial Regex MemberPattern();

    /// <summary>Function declarations by keyword, plus the bare shell <c>name() {</c> form.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(
        @"^\s*(?:(?:async|export|pub|local|static|inline)\s+)*"
        + @"(?:def|defp|fn|func|function|sub)\s+\w+"
        + @"|^\s*(?:function\s+)?[A-Za-z_]\w*\s*\(\)\s*\{")]
    private static partial Regex FunctionPattern();
}
