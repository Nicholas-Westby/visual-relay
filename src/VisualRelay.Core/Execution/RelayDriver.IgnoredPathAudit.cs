using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Paths git ignores that appear in the checkout during a run: invisible to the diff, the review
// and the commit, so the driver names them.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Dependencies and caches a build or test run leaves behind, beyond the build output the
    /// overlay already skips; an agent installing dependencies is not changing the environment.
    /// </summary>
    private static readonly IReadOnlySet<string> RunByproductNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "node_modules", ".hypothesis", ".tox", ".nox", ".coverage", "htmlcov", "coverage", ".nyc_output",
        ".eslintcache", ".cache", ".DS_Store",
    };

    /// <summary>The git-ignored entries of the checkout at <paramref name="rootPath"/> that a run could have made on purpose.</summary>
    private async Task<HashSet<string>> CaptureIgnoredEntriesAsync(string rootPath, CancellationToken cancellationToken)
    {
        var entries = await EnumerateOverlayIgnoredEntriesAsync(rootPath, cancellationToken);
        return entries
            .Select(entry => entry.IsDirectory ? entry.Name + "/" : entry.Name)
            .Where(name => !name.Split('/').Any(RunByproductNames.Contains))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Names the git-ignored paths that appeared in the checkout since the last look, in a
    /// <c>ignored_paths_created</c> warn event and in the ledger the later stages and the reviewer
    /// read, then adds them to <paramref name="known"/>. Measured on apache/commons-lang: an agent
    /// wrote .mvn/maven.config with -Drat.skip=true and both commits were verified with RAT off.
    /// </summary>
    private async Task AuditIgnoredEntriesAsync(
        string rootPath, string runId, string taskId, int stageNumber, HashSet<string> known,
        StringBuilder ledger, CancellationToken cancellationToken)
    {
        var created = (await CaptureIgnoredEntriesAsync(rootPath, cancellationToken))
            .Where(name => !known.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (created.Count == 0)
            return;

        known.UnionWith(created);
        var paths = string.Join(", ", created);
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "ignored_paths_created", runId, rootPath, taskId, stageNumber,
            Data: new Dictionary<string, string> { ["paths"] = paths }), cancellationToken);
        ledger.AppendLine(
            $"> **Outside version control (before stage {stageNumber})**: the checkout gained paths git ignores: {paths}. "
            + "The diff, the review and the commit do not show them. Changing how the project builds or tests "
            + "through ignored files is not a fix; a check that only passes that way has to be reported instead.");
        ledger.AppendLine();
    }
}
