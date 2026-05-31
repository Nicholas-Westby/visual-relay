using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerTests
{
    [Fact]
    public async Task DrainAsync_UsesManualOrderAndPausesAtTaskBoundary()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        runner.AfterRun = () => controller.RequestPause();

        await controller.RefreshAsync();
        controller.MoveDown("alpha");
        var results = await controller.DrainAsync();

        Assert.Equal(["beta"], runner.TasksRun);
        Assert.Equal(["beta"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Paused, controller.State);
        Assert.Equal(["alpha", "gamma"], controller.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task DrainAsync_HaltsAfterRepeatedCommitGateRejections()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var controller = new RelayQueueController(repo.Root, new CommitRejectingTaskRunner());

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Equal(["alpha", "beta"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Failed, controller.State);
        Assert.Equal(["gamma"], controller.Tasks.Select(t => t.Id));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "DRAIN-HALTED")));
    }
}

internal sealed class CommitRejectingTaskRunner : IRelayTaskRunner
{
    public Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, "commit rejected: empty commit"));
}
