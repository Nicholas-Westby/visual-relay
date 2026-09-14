using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// A snapshot of the checkout imports its own copy of an editable Python project.
public sealed partial class RelayDriver
{
    /// <summary>
    /// The search paths a command run in the snapshot at <paramref name="worktreePath"/> needs so each
    /// editable Python install of the checkout resolves inside the snapshot (see
    /// <see cref="PythonEditableImports"/>). Published as <c>snapshot_python_imports</c> when there is
    /// anything to redirect; empty when the sandbox cannot name either tree.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> SnapshotSearchPathsAsync(
        string sourcePath, string worktreePath, IReadOnlyList<(string Name, bool IsDirectory)> ignoredEntries,
        string runId, string taskId, int? stageNumber, CancellationToken cancellationToken)
    {
        var host = _dependencies.SandboxHost ?? await SandboxHost.CurrentAsync(cancellationToken);
        if (host.MapGrant(sourcePath) is not { } checkoutSeen || host.MapGrant(worktreePath) is not { } snapshotSeen)
            return PythonEditableImports.SearchPaths([]);

        var searchPaths = PythonEditableImports.SearchPaths(PythonEditableImports.SnapshotRoots(
            sourcePath, ignoredEntries.Where(entry => entry.IsDirectory).Select(entry => entry.Name),
            checkoutSeen, snapshotSeen));
        if (searchPaths.TryGetValue("PYTHONPATH", out var pythonPath))
        {
            await _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "info", "snapshot_python_imports", runId, sourcePath, taskId, stageNumber,
                Data: new Dictionary<string, string>
                {
                    ["pythonPath"] = pythonPath,
                    ["worktree"] = worktreePath,
                }), cancellationToken);
        }

        return searchPaths;
    }

    // The test runner call a snapshot makes: with search paths only when there are any.
    private Task<TestRunResult> RunInSnapshotAsync(
        string worktreePath, string command, IReadOnlyDictionary<string, string> searchPaths,
        CancellationToken cancellationToken) =>
        searchPaths.Count == 0
            ? _dependencies.TestRunner.RunAsync(worktreePath, command, cancellationToken)
            : _dependencies.TestRunner.RunAsync(worktreePath, command, searchPaths, cancellationToken);
}
