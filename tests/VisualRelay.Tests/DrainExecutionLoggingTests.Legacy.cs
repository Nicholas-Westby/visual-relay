using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Legacy single-phase regression, split out of DrainExecutionLoggingTests.cs to
// keep each file under the 300-line guard. The legacy constructor builds no
// planning driver, so it needs no temp-XDG isolation.
public sealed partial class DrainExecutionLoggingTests
{
    [Fact]
    public async Task DrainAsync_LegacyConstructor_SerialOnly_NoRegression()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("legacy", "# Legacy\n");

        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, results[0].Status);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.Equal(["legacy"], runner.TasksRun);
    }
}
