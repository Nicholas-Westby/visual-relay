using System.Text.RegularExpressions;

namespace VisualRelay.Guards;

/// <summary>
/// Pure classifier: detects whether a tracked file is a shell script
/// by extension (.sh/.bash/.zsh) or hashbang (first line matching <c>^#!.*\bsh\b</c>).
/// </summary>
public static class ShellScriptClassifier
{
    private static readonly Regex HashbangRegex = new(@"^#!.*sh\b", RegexOptions.Compiled);

    private static readonly HashSet<string> ShellExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh",
        ".bash",
        ".zsh",
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="relativePath"/> has a shell extension
    /// or <paramref name="firstLine"/> matches a shell hashbang.
    /// </summary>
    public static bool IsShellScript(string relativePath, string? firstLine)
    {
        // Extension check (case-insensitive).
        var ext = Path.GetExtension(relativePath);
        if (ext.Length > 0 && ShellExtensions.Contains(ext))
            return true;

        // Hashbang check.
        if (firstLine is not null && HashbangRegex.IsMatch(firstLine))
            return true;

        return false;
    }
}
