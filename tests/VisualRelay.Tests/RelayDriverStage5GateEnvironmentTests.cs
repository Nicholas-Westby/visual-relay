using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A red gate whose run never reached the tests proves nothing about them. Measured on the
/// Windows arm with litedb-org/LiteDB: the sandbox denied NuGet its user config, restore failed
/// before anything compiled, and the gate accepted that exit 1 as the new test's red, so
/// Author-tests passed in 25 s with a test that had never built. A compile error the new test
/// itself causes is still a red: that is how a test for a missing method fails. The same holds for
/// a build tool that never started: on the Windows arm, Unciv's gradle could not open its own file
/// hash lock and stopped in 556 ms, and the gate took that for the new test's red.
/// </summary>
public sealed class RelayDriverStage5GateEnvironmentTests
{
    private const string NugetDenied =
        "  Determining projects to restore...\n"
        + "/home/enjay/.dotnet/sdk/11.0.100-rc.1.26425.128/NuGet.targets(784,5): error : Failed to read NuGet.Config due to unauthorized access. Path: '/home/enjay/.nuget/NuGet/NuGet.Config'. [/home/enjay/vr-eval/LiteDB/LiteDB/LiteDB.csproj]\n"
        + "/home/enjay/.dotnet/sdk/11.0.100-rc.1.26425.128/NuGet.targets(784,5): error :   Access to the path '/home/enjay/.nuget/NuGet/NuGet.Config' is denied. [/home/enjay/vr-eval/LiteDB/LiteDB/LiteDB.csproj]\n"
        + "/home/enjay/.dotnet/sdk/11.0.100-rc.1.26425.128/NuGet.targets(784,5): error :   Permission denied [/home/enjay/vr-eval/LiteDB/LiteDB/LiteDB.csproj]\n";

    [Theory]
    [InlineData(NugetDenied)]
    [InlineData("  Retrying 'FindPackagesByIdAsync'...\nerror NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.\n")]
    [InlineData("[ERROR] Failed to execute goal on project commons-lang3: Could not resolve dependencies for project org.apache.commons:commons-lang3:jar:3.18.0\n")]
    [InlineData("ERROR: Could not find a version that satisfies the requirement httpx2 (from versions: none)\nERROR: No matching distribution found for httpx2\n")]
    public async Task ARunThatCouldNotFetchItsDependencies_IsAnUnusableGate(string output)
    {
        using var repo = TestRepository.Create();
        var (outcome, events) = await RunWithRedGateOutputAsync(repo, "restore-denied", output);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Contains(events.Events, e => e is { EventName: "author_test_gate_unusable", Level: "warn" });
        Assert.Equal("unproven", RelayDriverStage5GateTests.Stage5Status(repo, "restore-denied").Check);
    }

    // Unciv's red gate output on the Windows arm, as saved (nono's trailing JSON block left out).
    private const string GradleCouldNotStart =
        "\n\nFAILURE: Build failed with an exception.\n\n"
        + "* What went wrong:\n"
        + "Gradle could not start your build.\n"
        + "> Could not create service of type BuildLifecycleController using BuildScopeServices.createBuildLifecycleController().\n"
        + "   > Could not create service of type BuildModelController using VintageBuildControllerProvider.createBuildModelController().\n"
        + "      > Could not create service of type FileHasher using BuildSessionServices.createFileHasher().\n"
        + "         > java.io.FileNotFoundException: /home/enjay/vr-eval/Unciv/.gradle/9.4.1/fileHashes/fileHashes.lock (Permission denied)\n\n"
        + "* Try:\n"
        + "> Run with --stacktrace option to get the stack trace.\n"
        + "> Run with --info or --debug option to get more log output.\n"
        + "> Run with --scan to get full insights from a Build Scan (powered by Develocity).\n"
        + "> Get more help at https://help.gradle.org.\n\n"
        + "BUILD FAILED in 556ms\n";

    [Fact]
    public async Task ABuildToolThatCouldNotStart_IsAnUnusableGate()
    {
        using var repo = TestRepository.Create();
        var (outcome, events) = await RunWithRedGateOutputAsync(repo, "gradle-start", GradleCouldNotStart);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Contains(events.Events, e => e is { EventName: "author_test_gate_unusable", Level: "warn" });
        Assert.Equal("unproven", RelayDriverStage5GateTests.Stage5Status(repo, "gradle-start").Check);
    }

    [Fact]
    public async Task ACompileErrorFromTheNewTest_IsStillARed()
    {
        using var repo = TestRepository.Create();
        var (_, events) = await RunWithRedGateOutputAsync(repo, "compile-red",
            "LiteDB.Tests/Query/LikeTests.cs(41,25): error CS0117: 'Query' does not contain a definition for 'LikeTrailing'\n"
            + "Build FAILED.\n");

        Assert.DoesNotContain(events.Events, e => e.EventName == "author_test_gate_unusable");
        Assert.Equal("red", RelayDriverStage5GateTests.Stage5Status(repo, "compile-red").Check);
    }

    private static async Task<(RelayTaskOutcome Outcome, InMemoryRelayEventSink Events)> RunWithRedGateOutputAsync(
        TestRepository repo, string taskId, string redGateOutput)
    {
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask(taskId, "# A task\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "seed");
        var events = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, redGateOutput), new TestRunResult(0, "green")), events, sim),
            RelayDriverOptions.NoGitCommit);

        return (await driver.RunTaskAsync(repo.Root, taskId), events);
    }
}
