namespace VisualRelay.Core.Execution;

/// <summary>
/// Checks whether the stage-4 Plan's narrative and manifest appear to cover
/// the task's declared deliverables / "Done when" checklist.  Returns a
/// non-null corrective error message when coverage is insufficient; null when
/// the plan looks complete or no checklist is detectable.
/// </summary>
internal static class PlanCompletenessGate
{
    internal static string? CheckCoverage(
        string planNarrative,
        IReadOnlyList<string> manifest,
        string taskMarkdown)
    {
        var checklist = ExtractChecklist(taskMarkdown);
        if (checklist.Count == 0) return null;

        var corpus = (planNarrative + "\n" + string.Join("\n", manifest)).ToUpperInvariant();

        var uncovered = checklist
            .Where(item =>
            {
                var tokens = KeyTokensOf(item).ToList();
                return tokens.Count > 0
                    && !tokens.Any(tok => corpus.Contains(tok.ToUpperInvariant(), StringComparison.Ordinal));
            })
            .ToList();

        if (uncovered.Count == 0) return null;

        var bullets = string.Join("\n", uncovered.Select(u => $"- {u}"));
        return
            $"Plan completeness check: the following deliverables from the task's checklist " +
            $"do not appear to be covered by the plan narrative or manifest:\n{bullets}\n\n" +
            "Please revise the plan to address all stated deliverables, then re-emit the " +
            "complete JSON contract.";
    }

    internal static IReadOnlyList<string> ExtractChecklist(string markdown)
    {
        var items = new List<string>();
        var inSection = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.OrdinalIgnoreCase))
            {
                var heading = line[3..].Trim().ToUpperInvariant();
                inSection = heading.StartsWith("DONE WHEN", StringComparison.Ordinal)
                         || heading.StartsWith("DELIVERABLE", StringComparison.Ordinal);
                continue;
            }
            if (inSection && (line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                           || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)))
            {
                var text = line.TrimStart()[2..].Trim();
                if (text.Length > 0) items.Add(text);
            }
            else if (inSection && line.StartsWith("#", StringComparison.Ordinal))
            {
                inSection = false;
            }
        }
        return items;
    }

    internal static IEnumerable<string> KeyTokensOf(string item) =>
        item.Split([' ', '\t', '(', ')', ',', '.', ':', ';', '"', '\''],
                    StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 5);
}
