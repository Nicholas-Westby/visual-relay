using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A task that flags while planning leaves the main checkout alone. Planning runs in its own
/// worktree and copies back only the task's .relay folder, yet the drain reset the main
/// checkout after the flag anyway. Measured on the Windows arm with crawl: the drain log read
/// "reset-refused (pre-run-untracked.txt is missing ...)", and by then the reset had already run
/// <c>git reset -q HEAD</c> and <c>git checkout -- .</c>, which discard any uncommitted edit.
/// </summary>
public sealed class RelayQueueControllerPlanningFlagResetTests
{
    [Fact]
    public async Task DrainAsync_PlanningFlag_KeepsAnUncommittedEditInTheMainCheckout()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha, which flags while planning\n");
        var sim = PlanPhaseTestHelpers.InitGitRepo(repo.Root);
        sim.Seed(repo.Root, "src/app.cs", "original");
        sim.Commit(repo.Root, "add app");
        var edited = Path.Combine(repo.Root, "src", "app.cs");
        await File.WriteAllTextAsync(edited, "work the user has not committed");

        var controller = new RelayQueueController(
            repo.Root,
            new RecordingTaskRunner(),
            planSubagentRunnerFactory: (_, _) => new FlagAtStageSubagentRunner(flagAtStage: 3),
            planTestRunner: new ScriptedTestRunner(),
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim,
            sandboxHost: SandboxHost.Local);

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        Assert.Contains(results, r => r is { TaskId: "alpha", Status: RelayTaskOutcomeStatus.Flagged });
        Assert.Equal("work the user has not committed", await File.ReadAllTextAsync(edited));
    }
}
