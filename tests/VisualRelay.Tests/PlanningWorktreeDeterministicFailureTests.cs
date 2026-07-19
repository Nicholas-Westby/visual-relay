using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Prove that a deterministic git failure (unregistered GitSimEngine →
/// "not a git repository") fails FAST — the returned task is already
/// faulted before any virtual time advances, because the retry loop
/// recognizes the failure as deterministic and throws immediately.
/// </summary>
public sealed class PlanningWorktreeDeterministicFailureTests
{
    [Fact]
    public void CreateAsync_UnregisteredRepo_AlreadyFaulted()
    {
        // An unregistered GitSimEngine returns (128, "fatal: not a git
        // repository…") for EVERY call — exactly like NullGitInvoker, but
        // GitSim completes synchronously so the task is immediately ready.
        var sim = new GitSimEngine();
        var time = new ManualTimeProvider();
        var task = PlanningWorktree.CreateAsync(
            Path.Combine(Path.GetTempPath(), "vr-deterministic-test"),
            "task-id", "run-id", sim, CancellationToken.None, timeProvider: time);

        // The task must be FAULTED already (deterministic failure → throw,
        // no delay). If it's still pending, the retry loop slept on a
        // deterministic failure — a regression.
        Assert.True(task.IsFaulted,
            "Expected task to be already faulted (deterministic failure should throw immediately, not sleep).");
    }
}
