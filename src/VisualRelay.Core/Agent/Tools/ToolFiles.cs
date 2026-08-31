namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Failure messages the file tools share. Each one is written for a reader who
/// must decide what to call next, so it names the offending path as the model
/// should type it and points at the tool that resolves the ambiguity.
/// </summary>
internal static class ToolFiles
{
    private const int SiblingHints = 8;

    /// <summary>
    /// Explains a path that is not a readable file, listing what does exist
    /// beside it so the model can correct a typo without another round trip.
    /// </summary>
    /// <param name="context">The run, for rendering root-relative paths.</param>
    /// <param name="absolute">The resolved path that failed.</param>
    /// <param name="tool">The calling tool's wire name.</param>
    /// <returns>The message, or null when the path IS a readable file.</returns>
    internal static string? DescribeMissing(ToolContext context, string absolute, string tool)
    {
        if (File.Exists(absolute)) return null;

        var shown = ToolPaths.Display(context, absolute);
        if (Directory.Exists(absolute))
            return $"{tool}: \"{shown}\" is a directory, not a file — "
                + $"use list_files with path=\"{shown}\" to see what is inside it.";

        var parent = Path.GetDirectoryName(absolute);
        if (parent is null || !Directory.Exists(parent))
            return $"{tool}: no such file \"{shown}\" (its parent directory does not exist either) — "
                + "use list_files to see what the repository actually contains.";

        return $"{tool}: no such file \"{shown}\" — {Siblings(context, parent)}";
    }

    /// <summary>
    /// Detects content that must not be pasted into a prompt as text. Binary
    /// bytes survive a UTF-8 decode as NUL characters, which is the cheapest
    /// reliable signal available without sniffing every known file format.
    /// </summary>
    /// <param name="sample">Text decoded from the start of the file.</param>
    /// <returns>True when the content looks binary.</returns>
    internal static bool LooksBinary(string sample) => sample.Contains('\0', StringComparison.Ordinal);

    private static string Siblings(ToolContext context, string parent)
    {
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(parent)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Order(StringComparer.Ordinal)
                .Take(SiblingHints + 1)
                .ToList();

            if (entries.Count == 0)
                return $"the directory \"{ToolPaths.Display(context, parent)}\" is empty. "
                    + "Use list_files to see what the repository contains.";

            var listed = string.Join(", ", entries.Take(SiblingHints));
            var more = entries.Count > SiblingHints ? ", …" : string.Empty;
            return $"\"{ToolPaths.Display(context, parent)}\" contains: {listed}{more}. "
                + "Use list_files for the full listing.";
        }
        catch (IOException)
        {
            return "its parent directory could not be listed. Use list_files to see what exists.";
        }
        catch (UnauthorizedAccessException)
        {
            return "its parent directory is not readable. Use list_files to see what exists.";
        }
    }
}
