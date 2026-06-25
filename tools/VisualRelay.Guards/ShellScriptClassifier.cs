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

    // PowerShell and batch launchers are NOT POSIX shell, so they are explicitly out
    // of scope for this bash-oriented guard — even when their shebang mentions
    // `pwsh` (which ends in "sh" and would otherwise match the hashbang regex). The
    // Windows launchers carry their own size discipline; see the launcher tests.
    private static readonly HashSet<string> NonPosixExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1",
        ".psm1",
        ".cmd",
        ".bat",
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="relativePath"/> has a shell extension
    /// or <paramref name="firstLine"/> matches a shell hashbang. PowerShell/batch
    /// extensions are always excluded.
    /// </summary>
    public static bool IsShellScript(string relativePath, string? firstLine)
    {
        var ext = Path.GetExtension(relativePath);

        // Non-POSIX launchers (.ps1/.cmd/…) are out of scope regardless of shebang.
        if (ext.Length > 0 && NonPosixExtensions.Contains(ext))
            return false;

        // Extension check (case-insensitive).
        if (ext.Length > 0 && ShellExtensions.Contains(ext))
            return true;

        // Hashbang check.
        if (firstLine is not null && HashbangRegex.IsMatch(firstLine))
            return true;

        return false;
    }
}
