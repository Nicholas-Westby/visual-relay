using System.Globalization;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Per-stage measurement and completion-signaling tests for the Review (7)
/// / Visual-review (8) pair — verifies that each stage's stage_done event
/// fires with its own timing and status, not at the pair barrier.
/// </summary>
public sealed partial class RelayDriverReviewPairTests
{
    [Fact]
    public async Task RunTaskAsync_FastVisualSlowReview_VisualDoneFiresWithOwnTiming()
    {
        // Inject: stage 7 (Review) = 3000 ms delay, stage 8 (Visual-review) = 0 ms.
        // The visual-review finishes first (~0 s). Its stage_done must carry
        // timeSeconds close to its own 0 s wall-clock, NOT the sibling's 3 s.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("fast-visual", "# Fast visual, slow review\n");
        var sink = new InMemoryRelayEventSink();
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var runner = new DelayedSubagentRunner(inner,
            new Dictionary<int, int> { [7] = 3000, [8] = 0 });
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fast-visual");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stage8Done = sink.Events.FirstOrDefault(e =>
            e is { EventName: "stage_done", StageNumber: 8 });
        Assert.NotNull(stage8Done);
        Assert.True(stage8Done.Data is not null);
        var s8Seconds = double.Parse(stage8Done.Data!["timeSeconds"], CultureInfo.InvariantCulture);
        Assert.True(s8Seconds < 1.0,
            $"Stage 8 timeSeconds ({s8Seconds:F2}) should be < 1.0 (fast visual's own ~0s), not the slow sibling's ~3s");

        var stage7Done = sink.Events.FirstOrDefault(e =>
            e is { EventName: "stage_done", StageNumber: 7 });
        Assert.NotNull(stage7Done);
        Assert.True(stage7Done.Data is not null);
        var s7Seconds = double.Parse(stage7Done.Data!["timeSeconds"], CultureInfo.InvariantCulture);
        Assert.True(s7Seconds >= 2.5,
            $"Stage 7 timeSeconds ({s7Seconds:F2}) should be >= 2.5 (slow review with 3s delay)");
    }

    [Fact]
    public async Task RunTaskAsync_SlowVisualFastReview_ReviewDoneFiresWithOwnTiming()
    {
        // Symmetric case: stage 8 (Visual-review) = 3000 ms, stage 7 (Review) = 0 ms.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("slow-visual", "# Slow visual, fast review\n");
        var sink = new InMemoryRelayEventSink();
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var runner = new DelayedSubagentRunner(inner,
            new Dictionary<int, int> { [7] = 0, [8] = 3000 });
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "slow-visual");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stage7Done = sink.Events.FirstOrDefault(e =>
            e is { EventName: "stage_done", StageNumber: 7 });
        Assert.NotNull(stage7Done);
        Assert.True(stage7Done.Data is not null);
        var s7Seconds = double.Parse(stage7Done.Data!["timeSeconds"], CultureInfo.InvariantCulture);
        Assert.True(s7Seconds < 1.0,
            $"Stage 7 timeSeconds ({s7Seconds:F2}) should be < 1.0 (fast review's own ~0s), not the slow sibling's ~3s");

        var stage8Done = sink.Events.FirstOrDefault(e =>
            e is { EventName: "stage_done", StageNumber: 8 });
        Assert.NotNull(stage8Done);
        Assert.True(stage8Done.Data is not null);
        var s8Seconds = double.Parse(stage8Done.Data!["timeSeconds"], CultureInfo.InvariantCulture);
        Assert.True(s8Seconds >= 2.5,
            $"Stage 8 timeSeconds ({s8Seconds:F2}) should be >= 2.5 (slow visual with 3s delay)");
    }

    [Fact]
    public async Task RunTaskAsync_VisualDoneBeforeReview_EventOrderTolerance()
    {
        // Fast-visual case: stage-8 stage_done MUST appear in the event sink
        // before stage-7 stage_done.  Downstream consumers (drain summary,
        // status.json, UI tiles) must tolerate this ordering.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("ordering", "# Ordering test\n");
        var sink = new InMemoryRelayEventSink();
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var runner = new DelayedSubagentRunner(inner,
            new Dictionary<int, int> { [7] = 3000, [8] = 0 });
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "ordering");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var s7Idx = sink.Events.FindIndex(e =>
            e is { EventName: "stage_done", StageNumber: 7 });
        var s8Idx = sink.Events.FindIndex(e =>
            e is { EventName: "stage_done", StageNumber: 8 });
        Assert.True(s7Idx >= 0, "stage_done for stage 7 must be present");
        Assert.True(s8Idx >= 0, "stage_done for stage 8 must be present");
        Assert.True(s8Idx < s7Idx,
            $"stage-8 stage_done (index {s8Idx}) must fire before stage-7 stage_done (index {s7Idx}) when visual finishes first; consumers must tolerate 8-before-7 ordering");
    }

    [Fact]
    public async Task RunTaskAsync_BarrierRegression_Stage9WaitsForBoth()
    {
        // Inject equal delays for both stages 7 and 8 (2000 ms each).
        // The run must still complete (Committed), proving stage 9 waited for
        // both — the barrier is unchanged.  Ledger contains stages 7, 8, 9 in
        // sequential order (serialized writes).
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("barrier", "# Barrier regression\n");
        var sink = new InMemoryRelayEventSink();
        var inner = new ScriptedSubagentRunner();
        inner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var runner = new DelayedSubagentRunner(inner,
            new Dictionary<int, int> { [7] = 2000, [8] = 2000 });
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "barrier");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var ledger = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "barrier", "ledger.md"));
        Assert.Contains("## Stage 7 - Review", ledger, StringComparison.Ordinal);
        Assert.Contains("## Stage 8 - Visual-review", ledger, StringComparison.Ordinal);
        Assert.Contains("## Stage 9 - Fix", ledger, StringComparison.Ordinal);

        // Stage 9 must appear after both 7 and 8 in the serialized ledger.
        var s7Pos = ledger.IndexOf("## Stage 7 - Review", StringComparison.Ordinal);
        var s8Pos = ledger.IndexOf("## Stage 8 - Visual-review", StringComparison.Ordinal);
        var s9Pos = ledger.IndexOf("## Stage 9 - Fix", StringComparison.Ordinal);
        Assert.True(s9Pos > s7Pos, "Stage 9 must appear after stage 7 in the ledger");
        Assert.True(s9Pos > s8Pos, "Stage 9 must appear after stage 8 in the ledger");
    }
}
