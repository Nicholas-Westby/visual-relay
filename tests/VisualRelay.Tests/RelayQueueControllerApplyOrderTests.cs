using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayQueueControllerApplyOrderTests
{
    [Fact]
    public async Task DrainAsync_RunsInAppliedOrderWithUnknownIdSortingLast()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);

        await controller.RefreshAsync();

        // The app's visible order: gamma, beta, alpha — plus an id the controller
        // never heard of, which must sort last without displacing the known ids.
        controller.ApplyOrder(["gamma", "ghost", "beta", "alpha"]);
        var results = await controller.DrainAsync();

        Assert.Equal(["gamma", "beta", "alpha"], runner.TasksRun);
        Assert.Equal(["gamma", "beta", "alpha"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Completed, controller.State);
    }

    [Fact]
    public async Task ApplyOrder_KeepsUnrankedTasksInOriginalRelativeOrderAtEnd()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var controller = new RelayQueueController(repo.Root, new RecordingTaskRunner());

        await controller.RefreshAsync();

        // Only rank gamma; alpha and beta are unknown to the order and must keep
        // their original relative order (alpha before beta) at the end — stable.
        controller.ApplyOrder(["gamma"]);

        Assert.Equal(["gamma", "alpha", "beta"], controller.Tasks.Select(t => t.Id));
    }
}
