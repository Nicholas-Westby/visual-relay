using System.Text.RegularExpressions;

namespace VisualRelay.App.Services;

public sealed record PromptSection(string Title, string Body, bool CollapsedByDefault);

public static partial class AssembledPromptParser
{
    /// <summary>
    /// Top-level section headings produced by <c>BuildPrompt</c>.
    /// Any other <c>## </c> line (e.g. <c>## Stage 1 - Ideate</c> within
    /// the Prior stages body) is sub-content, not a section boundary.
    /// </summary>
    private static readonly HashSet<string> TopLevelHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "Task input",
        "Manifest",
        "Task context",
        "Log sources",
        "Prior stages",
        "Failing verify output",
        "Verify command"
    };

    /// <summary>
    /// Splits the assembled prompt markdown on top-level <c>## </c> headings.
    /// Text before the first heading becomes a <c>"Header"</c> section.
    /// The <c>"Prior stages"</c> section gets <c>CollapsedByDefault = true</c>
    /// and its trailing output-contract line (separated by a blank line from the
    /// ledger body) is extracted into an <c>"Output contract"</c> section.
    /// Tolerant: empty or garbage input → a single <c>"Prompt"</c> section.
    /// </summary>
    public static IReadOnlyList<PromptSection> Parse(string? assembledPrompt)
    {
        if (assembledPrompt is null)
            return [];

        // Find every candidate heading line and keep only the top-level ones.
        var allMatches = HeadingRegex().Matches(assembledPrompt);
        var topLevelMatches = new List<(int Index, int Length, string Title)>();
        foreach (Match m in allMatches)
        {
            var title = m.Groups[1].Value.Trim();
            if (TopLevelHeadings.Contains(title))
                topLevelMatches.Add((m.Index, m.Length, title));
        }

        // No top-level headings → single section.
        if (topLevelMatches.Count == 0)
        {
            if (string.IsNullOrEmpty(assembledPrompt))
                return [new PromptSection("Prompt", "", false)];

            // If there are ANY "## " headings, the body is the whole text (all
            // headings were sub-content, e.g. only "## Stage 1" lines).
            return [new PromptSection("Prompt", assembledPrompt, false)];
        }

        var sections = new List<PromptSection>();

        // Text before the first top-level heading
        var firstIndex = topLevelMatches[0].Index;
        if (firstIndex > 0)
        {
            var headerText = assembledPrompt[..firstIndex].Trim();
            if (headerText.Length > 0)
                sections.Add(new PromptSection("Header", headerText, false));
        }

        // Process each top-level heading+body pair
        for (var i = 0; i < topLevelMatches.Count; i++)
        {
            var (index, length, title) = topLevelMatches[i];
            var bodyStart = index + length;
            var bodyEnd = i + 1 < topLevelMatches.Count ? topLevelMatches[i + 1].Index : assembledPrompt.Length;
            var body = assembledPrompt[bodyStart..bodyEnd].Trim();

            if (string.Equals(title, "Prior stages", StringComparison.OrdinalIgnoreCase))
            {
                // Split off the output contract: last \n\n-separated block.
                var lastDoubleNewline = body.LastIndexOf("\n\n", StringComparison.Ordinal);
                if (lastDoubleNewline >= 0)
                {
                    var contractPart = body[(lastDoubleNewline + 2)..].Trim();
                    var ledgerPart = body[..lastDoubleNewline].Trim();

                    sections.Add(new PromptSection(title, ledgerPart, CollapsedByDefault: true));

                    if (contractPart.Length > 0)
                        sections.Add(new PromptSection("Output contract", contractPart, CollapsedByDefault: false));
                }
                else
                {
                    sections.Add(new PromptSection(title, body, CollapsedByDefault: true));
                }
            }
            else
            {
                sections.Add(new PromptSection(title, body, CollapsedByDefault: false));
            }
        }

        return sections;
    }

    [GeneratedRegex(@"^## (.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();
}
