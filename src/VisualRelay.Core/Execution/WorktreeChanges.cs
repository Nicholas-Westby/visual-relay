namespace VisualRelay.Core.Execution;

/// <summary>
/// The working tree's changed paths, in the same terms the commit stage stages them:
/// tracked edits (unstaged and staged) plus untracked non-ignored files, with Visual
/// Relay's own run artifacts and the tasks directory left out because nothing stages
/// those. Used to tell what a stage changed by comparing two listings.
/// </summary>
internal static class WorktreeChanges
{
    private static readonly string[] InternalArtifactPrefixes = [".relay/", ".relay-scratch/"];

    internal static async Task<IReadOnlySet<string>> ListAsync(
        string rootPath, string? tasksDir, IGitInvoker gitInvoker, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string[][] listings =
        [
            ["-c", "core.quotePath=false", "diff", "--name-only", "-z"],
            ["-c", "core.quotePath=false", "diff", "--cached", "--name-only", "-z"],
            ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard", "-z"],
            ["-c", "core.quotePath=false", "ls-files", "--deleted", "-z"],
        ];

        foreach (var arguments in listings)
        {
            var result = await gitInvoker.RunAsync(rootPath, arguments, cancellationToken);
            if (result.ExitCode != 0 || result.TimedOut)
                continue;

            // -z, so NUL records: a path holding a tab or newline stays literal and is
            // never C-quoted.
            foreach (var path in GitPathOutput.SplitNulRecords(result.Output))
            {
                var normalized = path.Replace('\\', '/');
                if (normalized.Length > 0 && !IsExcluded(normalized, tasksDir))
                    paths.Add(normalized);
            }
        }

        return paths;
    }

    private static bool IsExcluded(string relativePath, string? tasksDir)
    {
        foreach (var prefix in InternalArtifactPrefixes)
        {
            if (relativePath.StartsWith(prefix, StringComparison.Ordinal)
                || string.Equals(relativePath, prefix.TrimEnd('/'), StringComparison.Ordinal))
                return true;
        }

        return !string.IsNullOrEmpty(tasksDir)
            && (string.Equals(relativePath, tasksDir, StringComparison.Ordinal)
                || relativePath.StartsWith(tasksDir + "/", StringComparison.Ordinal));
    }
}
