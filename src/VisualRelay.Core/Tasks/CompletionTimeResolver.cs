using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

/// <summary>
/// Resolves a completion timestamp for an archived task through a four-tier
/// fallback chain. The first tier that yields a value wins.
/// </summary>
public static class CompletionTimeResolver
{
    /// <summary>
    /// Resolve <paramref name="task"/>'s completion time. When
    /// <see cref="RelayTaskItem.CompletedAt"/> is already populated (tier 1
    /// was set by <c>AttachRunMetrics</c>), it is returned immediately.
    /// Otherwise tiers 2–4 are tried in order.
    /// </summary>
    public static async Task<DateTimeOffset?> ResolveAsync(
        RelayTaskItem task,
        string rootPath,
        IGitInvoker? gitInvoker,
        CancellationToken cancellationToken)
    {
        // Tier 1: already populated by AttachRunMetrics.
        if (task.CompletedAt is { } existing)
            return existing;

        // Tier 2: newest file mtime under .relay/<id>/ (recursive).
        var relayDir = Path.Combine(rootPath, ".relay", task.Id);
        if (Directory.Exists(relayDir))
        {
            var tier2 = NewestRelayFileMtime(relayDir);
            if (tier2 is not null)
                return tier2;
        }

        // Tier 3: git committer date via IGitInvoker.
        if (gitInvoker is not null)
        {
            var tier3 = await GitCommitTimeAsync(task.MarkdownPath, rootPath, gitInvoker, cancellationToken);
            if (tier3 is not null)
                return tier3;
        }

        // Tier 4: markdown last-write time (last resort).
        return MarkdownMtime(task.MarkdownPath);
    }

    private static DateTimeOffset? NewestRelayFileMtime(string relayDir)
    {
        try
        {
            var files = Directory.EnumerateFiles(relayDir, "*", SearchOption.AllDirectories);
            DateTime? newest = null;
            foreach (var file in files)
            {
                try
                {
                    var mtime = File.GetLastWriteTimeUtc(file);
                    if (newest is null || mtime > newest.Value)
                        newest = mtime;
                }
                catch
                {
                    // skip unreadable files
                }
            }

            return newest is { } dt ? new DateTimeOffset(dt, TimeSpan.Zero) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DateTimeOffset?> GitCommitTimeAsync(
        string markdownPath,
        string rootPath,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, output, timedOut) = await gitInvoker.RunAsync(
                rootPath,
                ["log", "--follow", "-1", "--format=%cI", "--", markdownPath],
                cancellationToken,
                timeout: TimeSpan.FromSeconds(5));

            if (timedOut || exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            var line = output.Trim();
            if (DateTimeOffset.TryParse(line, out var parsed))
                return parsed;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? MarkdownMtime(string markdownPath)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(markdownPath);
            return new DateTimeOffset(mtime, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }
}
