using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that a <see cref="RelayQueueController"/> built with an injected
/// environment accessor threads it all the way down its parallel planning phase
/// (<see cref="PlanPhaseRunner.RunPlanPhaseAsync"/> → the internally-built
/// <see cref="RelayDriver"/> → <see cref="NonoProfileEnsurer.EnsureAsync"/>), so the
/// vr-guard sandbox profile self-heals under a hermetic TEMP <c>XDG_CONFIG_HOME</c>
/// and NEVER the real <c>$HOME/.config/visual-relay/vr-guard.json</c>.
///
/// <para>This is the orchestrator-construction counterpart of
/// <see cref="RelayDriverProfileIsolationTests"/>: those drivers come from
/// <see cref="RelayDriverDependencies.ForTests"/> (already isolated), while these
/// come from the orchestrators' PRODUCTION constructors. Reverting the accessor
/// threading in <see cref="RelayQueueController"/> or
/// <see cref="PlanPhaseRunner.RunPlanPhaseAsync"/> makes the planning driver fall
/// back to the real process env: the profile never appears under the injected temp
/// dir (assertion b fails) and — when the real profile was absent or diverged — the
/// real <c>~/.config</c> copy is created/changed (assertion a fails). Under the
/// always-on vr-guard nono sandbox that real-<c>~/.config</c> write is denied, which
/// is exactly the stage-1 failure this seam removes.</para>
/// </summary>
public sealed class OrchestratorProfileIsolationTests
{
    [Fact]
    public async Task RelayQueueControllerTwoPhase_SelfHealsProfileUnderInjectedXdg_LeavingRealHomeConfigUntouched()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("orchestrator-isolation", "# Orchestrator isolation\n");
        PlanPhaseTestHelpers.InitGitRepo(repo.Root);

        // A dedicated, FRESH temp XDG dir for THIS run (not the suite-shared accessor),
        // so "a profile appeared here" is attributable to this planning phase's self-heal
        // and stays revert-sensitive. Cleaned up below regardless of outcome.
        var xdgDir = Path.Combine(Path.GetTempPath(), "vr-orch-iso", Guid.NewGuid().ToString("N"));
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = xdgDir };
        var isolatedProfile = NonoProfileEnsurer.ResolveProfilePath(env);

        // Snapshot the REAL ~/.config profile target before the run (it may or may not
        // pre-exist on the host); the planning phase must not create or alter it.
        var realProfile = NonoProfileEnsurer.ResolveProfilePath();
        var realExistedBefore = File.Exists(realProfile);
        var realBytesBefore = realExistedBefore ? await File.ReadAllTextAsync(realProfile) : null;

        try
        {
            var planRunner = new ScriptedSubagentRunner();
            planRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

            // Phase 2 is a recording double (builds no driver), so the ONLY profile
            // self-heal comes from the Phase-1 planning driver — exactly the path the
            // injected accessor must reach.
            var controller = new RelayQueueController(
                repo.Root,
                new RecordingTaskRunner(),
                planSubagentRunnerFactory: _ => planRunner,
                planTestRunner: new ScriptedTestRunner(),
                environmentAccessor: env);

            await controller.RefreshAsync();
            var results = await controller.DrainAsync();

            // The planning phase actually ran end-to-end (proving EnsureAsync executed).
            Assert.Single(results);
            Assert.Equal("orchestrator-isolation", results[0].TaskId);
            Assert.Equal(RelayTaskOutcomeStatus.Committed, results[0].Status);

            // (a) The profile self-healed under the INJECTED temp XDG dir, byte-for-byte.
            Assert.True(
                File.Exists(isolatedProfile),
                $"planning driver must self-heal the vr-guard profile under the injected XDG dir: {isolatedProfile}");
            Assert.StartsWith(Path.GetTempPath(), isolatedProfile, StringComparison.Ordinal);
            Assert.Equal(NonoProfileEnsurer.EmbeddedContent, await File.ReadAllTextAsync(isolatedProfile));

            // (b) The real ~/.config profile was neither created nor modified.
            Assert.Equal(realExistedBefore, File.Exists(realProfile));
            if (realExistedBefore)
                Assert.Equal(realBytesBefore, await File.ReadAllTextAsync(realProfile));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdgDir);
        }
    }
}
