using System.Text.RegularExpressions;

namespace VisualRelay.Guards;

/// <summary>
/// Pure logic-line counter for shell scripts.
/// Excludes blank lines, full-line comments (and the hashbang), and here-doc bodies.
/// Inline comments after code count as a logic line.
/// </summary>
public static class ShellScriptLineCounter
{
    // Matches <<[-]? optionally followed by whitespace, an optional quote
    // (single or double), then the delimiter word, and the matching close quote.
    // Group 1: the optional dash (null if absent).
    // Group 2: the optional opening quote char (null if absent).
    // Group 3: the delimiter word.
    private static readonly Regex HereDocStart = new(
        @"<<(-)?\s*(['""]?)([A-Za-z_]\w*)\2",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns the number of logic lines in <paramref name="lines"/>.
    /// </summary>
    public static int CountLogicLines(IEnumerable<string> lines)
    {
        var count = 0;
        // Active here-docs: each entry is (delimiterWord, stripLeadingTabs).
        var hereDocs = new List<(string Word, bool StripTabs)>();

        foreach (var rawLine in lines)
        {
            // Check if we are inside any here-doc body.
            // We must process closing delimiters before the logic-line decision.
            if (hereDocs.Count > 0 && IsHereDocClosing(rawLine, hereDocs))
            {
                // This line is a closing delimiter — not a logic line.
                continue;
            }

            if (hereDocs.Count > 0)
            {
                // Inside a here-doc body — exclude entirely.
                continue;
            }

            // ── Not inside a here-doc body — classify the line ─────

            var trimmed = rawLine.TrimStart();
            if (trimmed.Length == 0)
                continue; // blank line

            if (trimmed[0] == '#')
                continue; // full-line comment (also drops hashbang)

            // Logic line.
            count++;

            // Scan for here-doc starts on this line (code + here-doc counts as 1).
            var m = HereDocStart.Match(rawLine);
            while (m.Success)
            {
                var stripTabs = m.Groups[1].Success; // the dash was present
                var word = m.Groups[3].Value;
                hereDocs.Add((word, stripTabs));
                m = m.NextMatch();
            }
        }

        return count;
    }

    /// <summary>
    /// Returns <c>true</c> and removes the matched here-doc from
    /// <paramref name="hereDocs"/> when <paramref name="line"/> is a closing
    /// delimiter for any active here-doc.
    /// </summary>
    private static bool IsHereDocClosing(
        string line,
        List<(string Word, bool StripTabs)> hereDocs)
    {
        // Trim the line. For strip-tabs here-docs also try stripping leading tabs.
        var trimmed = line.Trim();
        var tabStripped = line.TrimStart('\t');

        for (var i = hereDocs.Count - 1; i >= 0; i--)
        {
            var (word, stripTabs) = hereDocs[i];
            var candidate = stripTabs ? tabStripped : trimmed;
            if (candidate == word)
            {
                hereDocs.RemoveAt(i);
                return true;
            }
        }

        return false;
    }
}
