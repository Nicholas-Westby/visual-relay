using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class PlanPhaseRunnerTests
{
    [Fact]
    public async Task RunPlanPhase_FlaggedTasksAreReturnedButExcludedFromExecutePhase()
    {
        // PlanPhaseRunner hardcodes a real GitInvoker for worktree creation (no
        // injection seam) — this fact is irreducibly bound to the real git binary.
        SlowIntegration.SkipIfNotOptedIn();
        // Tasks that flag during planning must be returned with Flagged status
        // and must NOT proceed to the execute phase. The plan runner must
        // still copy back the NEEDS-REVIEW marker and partial artifacts.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("flag-in-plan", "# Flag in plan\n");
        // Git repo required for worktree creation.
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);
        var flagAt3 = new FlagAtStageSubagentRunner(flagAtStage: 3);

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("flag-in-plan", flagAt3)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Single(results);
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, results[0].Outcome.Status);

        // NEEDS-REVIEW marker must be copied back to the main repo.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "flag-in-plan", "NEEDS-REVIEW")));

        // Partial status must exist (stages 1–2 Done, 3 Flagged).
        var status = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "flag-in-plan"));
        Assert.Equal("Done", status[0].Status);
        Assert.Equal("Done", status[1].Status);
        Assert.Equal("Flagged", status[2].Status);
    }

    private static ISubagentRunner MakeRunner(string codeFile, string testFile)
    {
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath(codeFile, testFile);
        return runner;
    }
}
