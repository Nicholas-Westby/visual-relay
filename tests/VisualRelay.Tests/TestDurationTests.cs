using System.Text.Json;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TestDurationTests
{
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void TestRunResult_Elapsed_DefaultsToZero()
    {
        var result = new TestRunResult(0, "ok");
        Assert.Equal(TimeSpan.Zero, result.Elapsed);
    }

    [Fact]
    public void TestRunResult_Elapsed_CanBeSet()
    {
        var elapsed = TimeSpan.FromSeconds(1.5);
        var result = new TestRunResult(0, "ok", false, elapsed);
        Assert.Equal(elapsed, result.Elapsed);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Output);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ShellTestRunner_CapturesElapsed()
    {
        var runner = new ShellTestRunner(TimeSpan.FromSeconds(5));
        var result = await runner.RunAsync(Directory.GetCurrentDirectory(), "echo hi");
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Output}");
        Assert.True(result.Elapsed > TimeSpan.Zero, $"Elapsed was {result.Elapsed}");
    }

    [Fact]
    public void StageStatusEntry_JsonRoundTrip_IncludesTestDuration()
    {
        var entry = new StageStatusEntry(9, "Verify", "Done", Check: "green",
            DurationSeconds: 12.0, CostUsd: 0.05, Turns: 3, Model: "claude",
            TestDurationSeconds: 4.5);
        var json = JsonSerializer.Serialize(entry, CamelCase);
        Assert.Contains("\"testDurationSeconds\":4.5", json);
        var deserialized = JsonSerializer.Deserialize<StageStatusEntry>(json, CamelCase);
        Assert.NotNull(deserialized);
        Assert.Equal(4.5, deserialized!.TestDurationSeconds);
    }

    [Fact]
    public void StageStatusEntry_JsonRoundTrip_NullTestDuration()
    {
        var entry = new StageStatusEntry(1, "Ideate", "Done",
            DurationSeconds: 5.0, CostUsd: 0.01, TestDurationSeconds: null);
        var json = JsonSerializer.Serialize(entry, CamelCase);
        var deserialized = JsonSerializer.Deserialize<StageStatusEntry>(json, CamelCase);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.TestDurationSeconds);
    }

    [Fact]
    public void StageRowViewModel_MetricLabel_IncludesTestDuration()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.DurationLabel = "12s";
        stage.CostLabel = "$0.05";
        stage.TestDurationLabel = "5s";
        Assert.Contains("test 5s", stage.MetricLabel);
    }

    [Fact]
    public void StageRowViewModel_MetricLabel_OmitsWhenTestDurationEmpty()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.DurationLabel = "12s";
        stage.CostLabel = "$0.05";
        stage.TestDurationLabel = string.Empty;
        Assert.DoesNotContain("test", stage.MetricLabel);
    }

    [Fact]
    public void StageRowViewModel_ClearMetric_ResetsTestDuration()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.TestDurationLabel = "5s";
        stage.ClearMetric();
        Assert.Equal(string.Empty, stage.TestDurationLabel);
    }

    [Fact]
    public void StageRowViewModel_SetTestDurationSeconds_FormatsCorrectly()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.SetTestDurationSeconds(12);
        Assert.Equal("12s", stage.TestDurationLabel);
        stage.SetTestDurationSeconds(90);
        Assert.Equal("1m 30s", stage.TestDurationLabel);
        stage.SetTestDurationSeconds(0);
        Assert.Equal("0s", stage.TestDurationLabel);
        stage.SetTestDurationSeconds(null);
        Assert.Equal(string.Empty, stage.TestDurationLabel);
    }

    [Fact]
    public async Task FullRun_StatusJson_HasTestDurationSeconds()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("test-dur-status", "# Test duration status\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new ElapsedTestRunner(
            new TestRunResult(1, "red", Elapsed: TimeSpan.FromSeconds(0.5)),
            new TestRunResult(0, "green", Elapsed: TimeSpan.FromSeconds(1.2)));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "test-dur-status");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "test-dur-status"));
        Assert.NotEmpty(entries);

        var stage5 = entries.Single(e => e.Stage == 5);
        Assert.NotNull(stage5.TestDurationSeconds);
        Assert.True(stage5.TestDurationSeconds > 0);

        var stage9 = entries.Single(e => e.Stage == 9);
        Assert.NotNull(stage9.TestDurationSeconds);
        Assert.True(stage9.TestDurationSeconds > 0);

        Assert.Null(entries.Single(e => e.Stage == 1).TestDurationSeconds);
        Assert.Null(entries.Single(e => e.Stage == 4).TestDurationSeconds);
    }

    [Fact]
    public async Task FullRun_StatusJson_NoImplChange_NoTestDurationForStage5()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("no-impl", "# No impl\n");
        var runner = new OnlyTaskDirManifestSubagentRunner();
        var testRunner = new ElapsedTestRunner(
            new TestRunResult(0, "green", Elapsed: TimeSpan.FromSeconds(1.0)));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-impl");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "no-impl"));
        Assert.Null(entries.Single(e => e.Stage == 5).TestDurationSeconds);
    }

    [Fact]
    public async Task StageDone_Event_HasTestTime()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("test-time-event", "# Test time event\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new ElapsedTestRunner(
            new TestRunResult(1, "red", Elapsed: TimeSpan.FromSeconds(0.3)),
            new TestRunResult(0, "green", Elapsed: TimeSpan.FromSeconds(1.8)));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "test-time-event");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stage9Done = sink.Events.FirstOrDefault(
            e => e.EventName == "stage_done" && e.StageNumber == 9);
        Assert.NotNull(stage9Done);
        Assert.NotNull(stage9Done!.Data);
        Assert.True(stage9Done.Data!.ContainsKey("testTime"));
        Assert.NotEmpty(stage9Done.Data["testTime"]);
    }

    [Fact]
    public async Task StageDone_Event_NoTestTimeForNonTestStage()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("no-test-time", "# No test time\n");
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new ElapsedTestRunner(
            new TestRunResult(1, "red", Elapsed: TimeSpan.FromSeconds(0.5)),
            new TestRunResult(0, "green", Elapsed: TimeSpan.FromSeconds(1.0)));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-test-time");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stage1Done = sink.Events.FirstOrDefault(
            e => e.EventName == "stage_done" && e.StageNumber == 1);
        Assert.NotNull(stage1Done);
        if (stage1Done!.Data is not null)
            Assert.False(stage1Done.Data.ContainsKey("testTime"));
    }

    [Fact]
    public void StageRowViewModel_ApplyMetric_DoesNotSetTestDurationLabel()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.TestDurationLabel = "should-survive";
        var metric = new StageRunMetric(
            StageNumber: 9, StageName: "Verify", Tier: "cheap", Model: "claude",
            Timestamp: DateTimeOffset.UtcNow, DurationSeconds: 30.0, CostUsd: 0.10,
            Priced: true, PromptTokens: 1000, CachedTokens: 0, OutputTokens: 500,
            CacheWriteTokens: 0, ReportPath: "/tmp/report.json", TraceDirectory: null, Turns: 3);
        stage.ApplyMetric(metric);
        Assert.Equal("should-survive", stage.TestDurationLabel);
    }

    [Fact]
    public void SetTestDurationSeconds_Null_ClearsLabel()
    {
        var stage = new StageRowViewModel(RelayStages.All[8]);
        stage.TestDurationLabel = "5s";
        stage.SetTestDurationSeconds(null);
        Assert.Equal(string.Empty, stage.TestDurationLabel);
    }
}
