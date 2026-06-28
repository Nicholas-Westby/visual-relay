using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the optional <c>buildCmd</c> / <c>buildTimeoutMs</c> config that
/// splits the cold build from the timed test command so verify's wall-clock
/// budget covers test-only time.  The build runs first (untimed / generous cap,
/// no idle-reap watchdog), and the test command uses <c>--no-build</c> so
/// <c>testTimeoutMs</c> gates only the suite.
/// </summary>
public sealed class VerifyBuildWarmTests
{
    // ── Config: buildCmd round-trip ────────────────────────────────────

    [Fact]
    public async Task BuildCmd_AbsentFromJson_IsNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Null(config.BuildCommand);
    }

    [Fact]
    public async Task BuildCmd_PresentInJson_IsRead()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [], "buildCmd": "dotnet build --no-restore" }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Equal("dotnet build --no-restore", config.BuildCommand);
    }

    [Fact]
    public async Task BuildCmd_BlankInJson_IsNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [], "buildCmd": "   " }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Null(config.BuildCommand);
    }

    [Fact]
    public void BuildTimeoutMs_DefaultsTo30Minutes()
    {
        var defaults = RelayConfigLoader.Defaults();

        Assert.Equal(1_800_000, defaults.BuildTimeoutMilliseconds);
    }

    [Fact]
    public async Task BuildTimeoutMs_Override()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [], "buildTimeoutMs": 600000 }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(600_000, result.Config.BuildTimeoutMilliseconds);
    }

    // ── Integration: build-before-test pipeline ────────────────────────

    /// <summary>
    /// Without <c>buildCmd</c>, verify calls the test runner directly with
    /// <c>testCmd</c> — the existing behaviour must not regress.
    /// </summary>
    [Fact]
    public async Task NoBuildCmd_RunsTestDirectly_BackwardCompatible()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test --no-build", [], baselineVerify: false, maxVerifyLoops: 1);
        repo.WriteTask("no-build-cmd", "# No buildCmd\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var recordingTests = new RecordingTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 9 verify
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, recordingTests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-build-cmd");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The test command was passed to the runner at least once (stage 9).
        Assert.Contains(recordingTests.Calls, c => c.Command == "dotnet test --no-build");
    }

    /// <summary>
    /// With <c>buildCmd</c> that succeeds, the build runs first (untimed)
    /// and then the test command runs as normal.  The test runner receives
    /// <c>testCmd</c> after the build completes.
    /// </summary>
    [Fact]
    public async Task BuildCmd_BuildSuccess_TestRuns()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test --no-build",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1,
              "buildCmd": "true"
            }
            """);
        repo.WriteTask("build-ok", "# Build succeeds\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var recordingTests = new RecordingTestRunner(
            new TestRunResult(1, "red"),      // stage 5 author gate
            new TestRunResult(0, "build-ok"), // stage 9 build phase
            new TestRunResult(0, "green"));   // stage 9 verify
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, recordingTests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "build-ok");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The test command was still invoked after the build succeeded.
        Assert.Contains(recordingTests.Calls, c => c.Command == "dotnet test --no-build");
    }

    /// <summary>
    /// When <c>buildCmd</c> fails, the test is skipped entirely — the test
    /// runner is never called with <c>testCmd</c> at the verify stage, and a
    /// build-failure error event is published.
    /// </summary>
    [Fact]
    public async Task BuildCmd_BuildFailure_SkipsTest_EmitsErrorEvent()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test --no-build",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 0,
              "buildCmd": "false"
            }
            """);
        repo.WriteTask("build-fail", "# Build fails\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var recordingTests = new RecordingTestRunner(
            new TestRunResult(1, "red"),        // stage 5 author gate
            new TestRunResult(1, "build-fail"), // stage 9 build phase (fails)
            new TestRunResult(0, "green"));     // stage 9 verify (never reached)
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, recordingTests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "build-fail");

        // Build failure prevents verify from passing → task flags.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        // The test command was NEVER invoked for verify — build failed first.
        Assert.DoesNotContain(recordingTests.Calls, c => c.Command == "dotnet test --no-build");
        // A build-failure error event was published.
        Assert.Contains(sink.Events, e => e is { Level: "error", StageNumber: >= 9 });
    }
}
