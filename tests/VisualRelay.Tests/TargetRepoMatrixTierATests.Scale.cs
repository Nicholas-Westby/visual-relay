using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using VisualRelay.GitSim;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Tier A rows about SCALE and hostility rather than toolchain: a very large
/// repository, a repository whose tests take six minutes, and one carrying a
/// pre-commit hook that rejects the commit.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    private static StageInvocation WithManifest(IReadOnlyList<string> manifest) =>
        new(
            Stage: RelayStages.All[0],
            Tier: "cheap",
            RunId: "run-1",
            TargetRoot: "/tmp/big",
            TaskName: "a-task",
            TaskInput: "do the thing",
            LedgerSoFar: "(none)",
            Manifest: manifest,
            LogSources: [],
            TraceDirectory: "/tmp/big",
            ReportFile: string.Empty,
            MaxTurns: 4);

    /// <summary>
    /// A very large repo's manifest is capped in the prompt and the true count
    /// is stated. Pasting thousands of paths spends the context window on a
    /// file listing before the model reads a line of code — and on the smallest
    /// window in the catalog that is most of the budget.
    /// </summary>
    [Fact]
    public void Row_VeryLargeRepo_ManifestIsCappedAndCounted()
    {
        var manifest = Enumerable.Range(0, 2_500).Select(i => $"src/File{i}.cs").ToList();

        var prompt = SandboxedStage.BuildPrompt(WithManifest(manifest));

        Assert.Contains("src/File0.cs", prompt, StringComparison.Ordinal);
        Assert.Contains(
            $"src/File{SandboxedStage.MaxManifestEntriesInPrompt - 1}.cs", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"src/File{SandboxedStage.MaxManifestEntriesInPrompt}.cs", prompt, StringComparison.Ordinal);
        Assert.Contains("2500 entries", prompt, StringComparison.Ordinal);
        Assert.Contains(
            $"and {2_500 - SandboxedStage.MaxManifestEntriesInPrompt} more", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest at or below the cap is passed through whole, so an ordinary
    /// repo loses nothing to a guard meant for a pathological one.
    /// </summary>
    [Fact]
    public void Row_AnOrdinaryManifest_IsNotTruncated()
    {
        var manifest = Enumerable.Range(0, SandboxedStage.MaxManifestEntriesInPrompt)
            .Select(i => $"src/File{i}.cs").ToList();

        var prompt = SandboxedStage.BuildPrompt(WithManifest(manifest));

        Assert.DoesNotContain("and 0 more", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("entries;", prompt, StringComparison.Ordinal);
        Assert.Contains(
            $"src/File{SandboxedStage.MaxManifestEntriesInPrompt - 1}.cs", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repo whose tests take six minutes still runs them to completion. This
    /// is the headline row for the tool-timeout fix: the agent's per-command
    /// budget used to be clamped below the suite's own runtime, so a healthy
    /// six-minute suite was killed and reported as a failure.
    /// </summary>
    [Fact]
    public void Row_ASixMinuteTestSuite_FitsInsideTheCommandBudget()
    {
        var config = RelayConfigLoader.Defaults();
        var sixMinutes = TimeSpan.FromMinutes(6);

        Assert.True(
            TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds) > sixMinutes,
            $"the configured test timeout {config.TestTimeoutMilliseconds}ms does not "
            + "accommodate a six-minute suite");
    }

    /// <summary>
    /// The agent honours a six-minute request verbatim. Swival clamped every
    /// command to a hidden 240-second ceiling with no flag or env var, so a
    /// healthy six-minute suite was killed regardless of what the config said —
    /// and the model was told a number it never asked for, so it could not
    /// learn to adapt.
    /// </summary>
    [Fact]
    public void Row_ASixMinuteCommand_IsHonouredVerbatim()
    {
        var arguments = System.Text.Json.JsonDocument
            .Parse("""{"command": "npm test", "timeout_seconds": 360}""").RootElement;

        var (budget, error) = CommandTimeoutBudget.Resolve(arguments, TimeSpan.FromMinutes(40));

        Assert.Null(error);
        Assert.NotNull(budget);
        Assert.Equal(TimeSpan.FromMinutes(6), budget!.Applied);
        Assert.False(budget.Reduced, "a six-minute suite was silently cut short");
    }

    /// <summary>
    /// The only ceiling is what remains of the stage. A reduction says so,
    /// naming what was asked and what was applied, because a silent one is the
    /// bug being fixed.
    /// </summary>
    [Fact]
    public void Row_ACommandLongerThanTheStage_IsReducedAndSaysSo()
    {
        var arguments = System.Text.Json.JsonDocument
            .Parse("""{"command": "npm test", "timeout_seconds": 3600}""").RootElement;

        var (budget, error) = CommandTimeoutBudget.Resolve(arguments, TimeSpan.FromMinutes(6));

        Assert.Null(error);
        Assert.True(budget!.Reduced);
        Assert.Equal(TimeSpan.FromHours(1), budget.Requested);
        Assert.Equal(TimeSpan.FromMinutes(6), budget.Applied);
    }

    /// <summary>
    /// A repo whose pre-commit hook rejects everything still detects and
    /// bootstraps. The hook is the target's business, not Visual Relay's: the
    /// rejection must surface when a commit is attempted, not turn setup into a
    /// silent no-op or a hang.
    /// </summary>
    [Fact]
    public async Task Row_HostilePreCommitHook_StillDetectsAndBootstraps()
    {
        var root = NewRepo("hostile-hook");
        try
        {
            Write(root, "go.mod", "module example.com/m\n");
            Write(root, "main.go", "package main\n");

            // A real repo with a hostile hook already has history, so bootstrap
            // needs no initial commit and the hook never fires.
            var sim = new GitSimEngine();
            sim.InitRepo(root);
            await sim.RunAsync(root, ["add", "-A"], CancellationToken.None);
            await sim.RunAsync(root, ["commit", "-m", "base"], CancellationToken.None);

            // The hook goes on only after the repo has history, which is the
            // shape a real hostile-hook repo arrives in.
            sim.PreCommitHook = _ => GitSimHookVerdict.Reject("hook: no commits here");

            Assert.Equal(["go test ./..."], TestCommandDetector.DetectCandidates(root));

            var result = await ProjectBootstrapper.BootstrapAsync(
                root, gitInvoker: sim,
                validationRunner: new ScriptedTestRunner(new TestRunResult(0, "ok")));

            // Detection and config writing do not depend on being able to commit.
            Assert.Equal("go test ./...", result.TestCommand);
            Assert.False(result.UsedPlaceholderTestCommand);
            Assert.True(File.Exists(Path.Combine(root, ".relay", "config.json")));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// A hostile hook on a repo with NO history fails bootstrap loudly, naming
    /// the rejection. Bootstrap must create an initial commit there, and a
    /// silent no-op would leave the user with a half-configured repo and no
    /// idea why.
    /// </summary>
    [Fact]
    public async Task Row_HostileHookAndNoHistory_FailsLoudlyNamingTheRejection()
    {
        var root = NewRepo("hostile-hook-no-history");
        try
        {
            Write(root, "go.mod", "module example.com/m\n");
            var sim = new GitSimEngine
            {
                PreCommitHook = _ => GitSimHookVerdict.Reject("hook: no commits here"),
            };

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectBootstrapper.BootstrapAsync(
                    root, gitInvoker: sim,
                    validationRunner: new ScriptedTestRunner(new TestRunResult(0, "ok"))));

            Assert.Contains("hook: no commits here", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
