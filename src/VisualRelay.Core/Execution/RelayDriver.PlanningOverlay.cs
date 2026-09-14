namespace VisualRelay.Core.Execution;

// A planning worktree gets the checkout's git-ignored runtime content, as the verify snapshot does.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Mirrors <paramref name="checkoutPath"/>'s git-ignored runtime entries into the planning
    /// worktree at <paramref name="worktreePath"/> under the verify snapshot's rules: build
    /// output is skipped, small entries are cloned or copied, large ones linked. The planning
    /// agents run the tests to reproduce a task, and a checkout omits what git ignores.
    /// Measured with ruby-grape/grape: without vendor/bundle and .bundle/config the research
    /// agent found the sandbox denying a gem install to the home cache, then reinstalled 90 gems.
    /// </summary>
    internal Task OverlayCheckoutDependenciesAsync(
        string checkoutPath, string worktreePath, string worktreeId, string runId,
        CancellationToken cancellationToken) =>
        OverlayIgnoredEntriesAsync(
            checkoutPath, worktreePath, worktreeId, runId, IgnoredOverlayCopyMaxBytes, cloneOverlay: true,
            cancellationToken);
}
