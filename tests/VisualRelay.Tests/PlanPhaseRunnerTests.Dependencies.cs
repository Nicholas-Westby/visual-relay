using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A planning worktree carries the checkout's git-ignored dependencies, as the verify snapshot
/// does. Measured with ruby-grape/grape: the research agent's worktree had no vendor/bundle or
/// .bundle/config, the sandbox denied bundler's install into the home gem cache, and the agent
/// spent four minutes on that before reinstalling 90 gems inside the worktree.
/// </summary>
public sealed partial class PlanPhaseRunnerTests
{
    /// <summary>Records what the research agent finds in its worktree.</summary>
    private sealed class WorktreeProbe(ISubagentRunner inner) : ISubagentRunner
    {
        public bool SawDependency { get; private set; }
        public bool SawBuildOutput { get; private set; }

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            if (invocation.Stage.Number == 2)
            {
                SawDependency = File.Exists(Path.Combine(invocation.TargetRoot, "vendor", "bundle", "rack.rb"));
                SawBuildOutput = Directory.Exists(Path.Combine(invocation.TargetRoot, "target"));
            }

            return inner.RunAsync(invocation, cancellationToken);
        }
    }

    [Fact]
    public async Task RunPlanPhase_GivesTheAgentsTheCheckoutsIgnoredDependencies_ButNotItsBuildOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("deps", "# Deps\n");
        var sim = PlanPhaseTestHelpers.InitGitSim(repo.Root);
        sim.Seed(repo.Root, ".gitignore", "vendor/\ntarget/\n");
        sim.Commit(repo.Root, "ignore dependencies and build output");
        Directory.CreateDirectory(Path.Combine(repo.Root, "vendor", "bundle"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "vendor", "bundle", "rack.rb"), "# a gem");
        Directory.CreateDirectory(Path.Combine(repo.Root, "target"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "target", "app.o"), "object code");
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var probe = new WorktreeProbe(inner);

        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("deps", Use(probe))],
            config: PlanPhaseTestHelpers.MakeConfig(1),
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg,
            gitInvoker: sim,
            sandboxHost: SandboxHost.Local);

        Assert.Equal(RelayTaskOutcomeStatus.Planned, Assert.Single(results).Outcome.Status);
        Assert.True(probe.SawDependency, "the research agent's worktree must hold the checkout's ignored vendor/bundle");
        Assert.False(probe.SawBuildOutput, "build output stays out: it is regenerated at the worktree's own path");
    }
}
