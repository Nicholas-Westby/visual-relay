using System.Text;

namespace VisualRelay.Core.Execution;

// What the one audit call is shown, assembled from git and from disk.
public static partial class AuthorTestDiffAuditor
{
    /// <summary>
    /// What the model is shown: the tracked test files' diff against HEAD plus the
    /// whole content of every declared test file git does not know about yet,
    /// which is where a first-time test file's implementation would hide.
    /// </summary>
    /// <param name="rootPath">The workspace root.</param>
    /// <param name="testFiles">What the stage declared as its test files.</param>
    /// <param name="git">The git invoker the diff is read with.</param>
    /// <param name="cancellationToken">Cancels the git calls.</param>
    /// <returns>The diff, cut at the cap with a marker when it is longer.</returns>
    internal static async Task<string> BuildDiffAsync(
        string rootPath,
        IReadOnlyList<string> testFiles,
        IGitInvoker git,
        CancellationToken cancellationToken)
    {
        // The list is the model's own, so an entry can name a path outside the
        // workspace. Those are dropped before git or the reader ever sees them.
        var declared = testFiles
            .Select(file => (Relative: file, Full: InsideWorkspace(rootPath, file)))
            .Where(entry => entry.Full is not null)
            .ToList();

        var tracked = await TrackedAsync(rootPath, [.. declared.Select(entry => entry.Relative)], git, cancellationToken);
        var known = new HashSet<string>(tracked, PathComparer);
        var builder = new StringBuilder();
        if (tracked.Count > 0)
        {
            var diff = await git.RunAsync(rootPath, ["diff", "HEAD", "--", .. tracked], cancellationToken);
            // A repository without a commit yet has no HEAD to diff against; the
            // untracked pass below still shows everything the stage wrote.
            if (diff.ExitCode == 0)
                builder.Append(diff.Output);
        }

        foreach (var (relative, full) in declared.Where(entry => !known.Contains(entry.Relative)))
        {
            if (!File.Exists(full))
                continue;
            builder.Append("--- new file: ").Append(relative).AppendLine(" ---");
            builder.AppendLine(await File.ReadAllTextAsync(full!, cancellationToken));
        }

        var text = builder.ToString();
        return text.Length <= DiffCharacterCap
            ? text
            : text[..DiffCharacterCap] + TruncationMarker;
    }

    /// <summary>Paths compare the way the host's filesystem compares them.</summary>
    private static StringComparer PathComparer =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// The declared files git already tracks, spelled as the index spells them so
    /// the diff pathspec matches, and in git's own order so the prompt is stable.
    /// </summary>
    private static async Task<List<string>> TrackedAsync(
        string rootPath, IReadOnlyList<string> testFiles, IGitInvoker git, CancellationToken cancellationToken)
    {
        if (testFiles.Count == 0)
            return [];

        var listed = await git.RunAsync(rootPath, ["ls-files", "-z", "--", .. testFiles], cancellationToken);
        return listed.ExitCode == 0
            ? [.. GitPathOutput.SplitNulRecords(listed.Output)]
            : [];
    }

    /// <summary>
    /// The absolute path of a declared file, or null when it is rooted or resolves
    /// outside the workspace.
    /// </summary>
    private static string? InsideWorkspace(string rootPath, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file))
            return null;
        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Combine(root, file));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}
