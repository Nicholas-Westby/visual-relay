namespace VisualRelay.Core.Execution;

/// <summary>
/// Locates the well-known contributor/agent instruction files and directories a
/// repository may ship, so Research can be pointed at them before it investigates
/// anything else.
/// <para>
/// This fixed filename list is the one deliberate exception to Visual Relay's
/// general-purpose rule: nothing else in a prompt or in code may name a specific
/// file or tool. Keeping the list here, rather than in a static system prompt,
/// is what keeps that rule intact.
/// </para>
/// </summary>
internal static class RepositoryInstructionFiles
{
    /// <summary>Checked, in order, before <see cref="CursorRulesDirectory"/>.</summary>
    private static readonly string[] LeadingCandidateFiles =
    [
        "AGENTS.md",
        "CLAUDE.md",
        "GEMINI.md",
        "CONTRIBUTING.md",
        "docs/CONTRIBUTING.md",
        ".github/CONTRIBUTING.md",
        ".github/copilot-instructions.md",
        ".cursorrules",
    ];

    private const string CursorRulesDirectory = ".cursor/rules";

    /// <summary>Checked, in order, after <see cref="CursorRulesDirectory"/>.</summary>
    private static readonly string[] TrailingCandidateFiles =
    [
        ".clinerules",
        ".windsurfrules",
    ];

    /// <summary>
    /// Returns, in a fixed priority order, the candidates from
    /// <see cref="LeadingCandidateFiles"/>, <see cref="CursorRulesDirectory"/>, and
    /// <see cref="TrailingCandidateFiles"/> that exist under <paramref name="rootPath"/>.
    /// The directory candidate counts only when it contains at least one file
    /// (anywhere in its tree). A missing root returns an empty list.
    /// </summary>
    public static IReadOnlyList<string> Find(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return [];

        var found = new List<string>();
        found.AddRange(LeadingCandidateFiles.Where(candidate => File.Exists(Path.Combine(rootPath, candidate))));

        var cursorRulesFull = Path.Combine(rootPath, ".cursor", "rules");
        if (Directory.Exists(cursorRulesFull)
            && Directory.EnumerateFiles(cursorRulesFull, "*", SearchOption.AllDirectories).Any())
        {
            found.Add(CursorRulesDirectory);
        }

        found.AddRange(TrailingCandidateFiles.Where(candidate => File.Exists(Path.Combine(rootPath, candidate))));
        return found;
    }
}
