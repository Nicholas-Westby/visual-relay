using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// A git worktree is created and removed in pairs, and both halves must run on a
/// token a cancel cannot trip. Handing a cancelled token to <c>git worktree add</c>
/// leaves an admin entry pointing at a directory that was never checked out — and a
/// caller holding no path to remove; handing one to <c>git worktree remove</c> skips
/// the removal, so the local directory delete tears the tree away and leaves the
/// admin entry behind. Parses source text to hold the invariant at every call site —
/// no runtime integration.
/// </summary>
public sealed class WorktreeTokenGuardTests
{
    [Theory]
    [InlineData("PlanningWorktree.CreateAsync")]
    [InlineData("PlanningWorktree.RemoveAsync")]
    public void WorktreeLifecycleCalls_PassCancellationTokenNone(string call)
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
            foreach (var arguments in CallArgumentListsFor(File.ReadAllText(file), call))
                if (!arguments.Contains("CancellationToken.None", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {call}({Collapse(arguments)})");

        Assert.True(offenders.Count == 0,
            $"every {call} call under src/ must pass CancellationToken.None; found: "
            + string.Join(" | ", offenders));
    }

    /// <summary>
    /// The teardown helper takes no cancellation token at all, so no caller — not even
    /// a test seam — can hand it one. The invariant above is structural here.
    /// </summary>
    [Fact]
    public void CleanupVerifyWorktree_DeclaresNoCancellationToken()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.Resolve().Root, "src",
            "VisualRelay.Core", "Execution", "RelayDriver.VerifyWorktreeCleanup.cs"));

        var parameters = Assert.Single(ArgumentListsFor(source, "Task CleanupVerifyWorktreeAsync("));
        Assert.DoesNotContain("CancellationToken", parameters, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Resolve().Root, "src"), "*.cs",
                SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>Argument lists of every INVOCATION of <paramref name="name"/> (never its declaration).</summary>
    private static IEnumerable<string> CallArgumentListsFor(string text, string name) =>
        ArgumentListsFor(text, name + "(", declarations: false);

    /// <summary>
    /// The text between the parentheses following each occurrence of
    /// <paramref name="needle"/>, matching parentheses so a nested call or a
    /// collection expression inside the list is kept whole.
    /// </summary>
    private static IEnumerable<string> ArgumentListsFor(
        string text, string needle, bool declarations = true)
    {
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            // A longer identifier ending in the same name is a different member.
            if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '_' or '.'))
                continue;
            if (!declarations && PrecedingWord(text, at) is "Task" or "void" or "ValueTask")
                continue;

            var open = at + needle.Length;
            var depth = 1;
            var i = open;
            while (i < text.Length && depth > 0)
            {
                depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
                i++;
            }
            if (depth == 0)
                yield return text[open..(i - 1)];
        }
    }

    /// <summary>The whitespace-delimited word before <paramref name="at"/> — a method's return type at a declaration.</summary>
    private static string PrecedingWord(string text, int at)
    {
        var end = at;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var word = text[start..end];
        return word.StartsWith("Task<", StringComparison.Ordinal) ? "Task" : word;
    }

    private static string Collapse(string arguments) =>
        string.Join(' ', arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
