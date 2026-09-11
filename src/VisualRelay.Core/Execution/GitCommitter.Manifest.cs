namespace VisualRelay.Core.Execution;

internal static partial class GitCommitter
{
    /// <summary>
    /// Narrows the stage-4 manifest to the paths the explicit <c>git add</c> may name:
    /// relay bookkeeping is dropped, and an entry that neither exists on disk nor is
    /// tracked is skipped (git would abort the whole add on an unmatched pathspec).
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveManifestFilesToStageAsync(
        IGitInvoker gitInvoker,
        string rootPath,
        IReadOnlyList<string> manifest,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        var files = new List<string>();
        foreach (var relative in manifest.Distinct(StringComparer.Ordinal))
        {
            // Relay bookkeeping never enters a task commit. Stage 5's manifest merge
            // already drops task-dir files and the ignored-path pre-check rejects a
            // gitignored .relay entry, so this only catches a stage-4 manifest that
            // named relay state in a repo that tracks it — the case the (now removed)
            // exclude pathspec used to absorb on this call.
            if (IsInternalArtifact(relative))
                continue;

            var fullPath = Path.Combine(rootPath, relative);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                files.Add(relative);
                continue;
            }

            var tracked = await GitAsync(gitInvoker, rootPath, ["ls-files", "--", relative], cancellationToken, timeProvider: timeProvider);
            if (!string.IsNullOrWhiteSpace(tracked.Output))
            {
                files.Add(relative);
            }
        }

        return files;
    }
}
