using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverVerifyFixOutputTests
{
    [Fact]
    public async Task RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage10AndStage11_WithOutputFilePointer()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("verify-event", "# Verify event test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 10 verify — retry also fails
            new TestRunResult(0, "green"),             // fix-verify attempt 1 gate — green
            new TestRunResult(0, "green"));            // pad (not reached)
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "verify-event");

        // (1) The first authoritative red (stage 10) is observable.
        var stage10 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 10 });
        Assert.NotNull(stage10);
        Assert.Equal("dotnet test", stage10!.Data?["command"]);
        Assert.Equal("1", stage10.Data?["exitCode"]);
        Assert.Equal("red", stage10.Data?["check"]);
        Assert.Contains("Failed TestX", stage10.Data?["reason"] ?? "", StringComparison.Ordinal);
        // (2) The stage-11 gate verdict is observable.
        var stage11 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 11 });
        Assert.NotNull(stage11);
        Assert.Equal("dotnet test", stage11!.Data?["command"]);
        Assert.Equal("0", stage11.Data?["exitCode"]);
        Assert.Equal("green", stage11.Data?["check"]);
        Assert.True(stage11.Data!.ContainsKey("treeHash"));
        Assert.True(stage11.Data.ContainsKey("outputFile"));
        Assert.True(File.Exists(stage11.Data["outputFile"]));
        // (3) verify_result carries a treeHash and outputFile POINTER; full output in file, never inlined.
        Assert.True(stage10.Data!.ContainsKey("treeHash"));
        var outputFile = stage10.Data["outputFile"];
        Assert.False(string.IsNullOrEmpty(outputFile));
        Assert.True(File.Exists(outputFile));
        var persisted = await File.ReadAllTextAsync(outputFile);
        Assert.Contains("Failed TestX", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(stage10.Data.Values, v => v.Contains("Failed TestX") && v.Length > 200);
    }

    [Fact]
    public async Task RunTaskAsync_VerifyGreen_Stage10AgentReceivesCapturedTestOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("stage9-green-output", "# Stage 9 receives green output\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),         // stage 5 author gate
            new TestRunResult(0, "All 42 tests passed!"));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "stage9-green-output");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.NotNull(inv.LastTestOutput);
        Assert.Contains("All 42 tests passed!", inv.LastTestOutput, StringComparison.Ordinal);
        Assert.Null(inv.TestCommand); // read-only Verify gets captured output, not an imperative command
    }

    [Fact]
    public async Task RunTaskAsync_VerifyRed_Stage10AgentReceivesFailingTestOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("stage9-fail-output", "# Stage 9 receives failing output\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(new TestRunResult(1, "red"),                    // stage 5 author gate
            new TestRunResult(1, "FAIL: TestDeepCheck"),
            new TestRunResult(1, "FAIL: TestDeepCheck"),
            new TestRunResult(1, "FAIL: TestDeepCheck"),
            new TestRunResult(0, "All green!"));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        var outcome = await driver.RunTaskAsync(repo.Root, "stage9-fail-output");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var inv = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.NotNull(inv.LastTestOutput);
        Assert.Contains("FAIL: TestDeepCheck", inv.LastTestOutput, StringComparison.Ordinal);
        Assert.Null(inv.TestCommand);
    }
}
