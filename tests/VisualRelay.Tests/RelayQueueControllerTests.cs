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
}

