using System.Globalization;
using System.Text.Json;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Contract tests for the observability fields an orchestrator polls on
/// <c>GET /state</c>: the server clock, the last relay event and when it
/// arrived (the stuck detector), the live running-task roster, the session
/// cost, the drain-halt marker, and the per-task metrics every task entry
/// carries. Each drives the real view model on the headless dispatcher — no
/// relay run is started.
/// </summary>
public sealed partial class ControlApiTests
{
    [AvaloniaFact]
    public async Task BuildStateJson_WhenIdle_ReportsClockAndNoActivity()
    {
        var api = NewApi(out _);
        var before = DateTimeOffset.UtcNow;

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var root = doc.RootElement;

        var nowUtc = root.GetProperty("nowUtc").GetDateTimeOffset();
        Assert.InRange(nowUtc, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastActivityUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastEvent").ValueKind);
        Assert.Equal(0, root.GetProperty("runningTasks").GetArrayLength());
        Assert.Equal(0d, root.GetProperty("sessionCostUsd").GetDouble());
        Assert.False(root.GetProperty("drainHalted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("haltReason").ValueKind);
    }

    [AvaloniaFact]
    public async Task BuildStateJson_AfterRelayEvent_ReportsLastActivityAndEvent()
    {
        var api = NewApi(out var vm);
        var at = DateTimeOffset.UtcNow.AddMinutes(-2);
        RelayEventTestDispatch.Dispatch(vm, RelayEventTestDispatch.StageStart("alpha", 6, at));

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var root = doc.RootElement;

        Assert.Equal(at, root.GetProperty("lastActivityUtc").GetDateTimeOffset());
        var last = root.GetProperty("lastEvent");
        Assert.Equal(at, last.GetProperty("utc").GetDateTimeOffset());
        Assert.Equal("info", last.GetProperty("level").GetString());
        Assert.Equal("stage_start", last.GetProperty("name").GetString());
        Assert.Equal("alpha", last.GetProperty("taskId").GetString());
        Assert.Equal(6, last.GetProperty("stage").GetInt32());
        Assert.Equal("balanced", last.GetProperty("tier").GetString());
        Assert.Contains("Stage 6", last.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task BuildStateJson_LastEvent_ClipsLongTraceTextTo240Characters()
    {
        var api = NewApi(out var vm);
        var trace = new RelayEvent(
            DateTimeOffset.UtcNow, "info", "trace", "test-run", "/root", "alpha", 6, "balanced",
            Data: new Dictionary<string, string>
            {
                ["kind"] = "AssistantText",
                ["title"] = "assistant",
                ["content"] = new('x', 5_000)
            });
        RelayEventTestDispatch.Dispatch(vm, trace);

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var message = doc.RootElement.GetProperty("lastEvent").GetProperty("message").GetString();

        Assert.NotNull(message);
        Assert.Equal(240, message.Length);
    }

    [AvaloniaFact]
    public async Task BuildStateJson_RunningTasks_ListTheLiveStageOfEachRunningTask()
    {
        var api = NewApi(out var vm);
        vm.RestoreRunningTaskState("alpha", 6, "Implement");

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var running = doc.RootElement.GetProperty("runningTasks");

        Assert.Equal(1, running.GetArrayLength());
        Assert.Equal("alpha", running[0].GetProperty("taskId").GetString());
        Assert.Equal(6, running[0].GetProperty("stageNumber").GetInt32());
        Assert.Equal("Implement", running[0].GetProperty("stageName").GetString());
        Assert.Equal("balanced", running[0].GetProperty("tier").GetString());
    }

    [AvaloniaFact]
    public async Task BuildStateJson_RunningTasks_ReportNullStageWhenNoneIsOpen()
    {
        var api = NewApi(out var vm);
        vm.RestoreRunningTaskState("gamma", stageNumber: null, stageName: null);

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var running = doc.RootElement.GetProperty("runningTasks")[0];

        Assert.Equal("gamma", running.GetProperty("taskId").GetString());
        Assert.Equal(JsonValueKind.Null, running.GetProperty("stageNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("stageName").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("tier").ValueKind);
    }

    [AvaloniaFact]
    public async Task BuildStateJson_RunningTasks_FollowTheStageEventsMoveTo()
    {
        var api = NewApi(out var vm);
        vm.RestoreRunningTaskState("alpha", 6, "Implement");
        RelayEventTestDispatch.Dispatch(
            vm, RelayEventTestDispatch.StageStart("alpha", 7, DateTimeOffset.UtcNow));

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var running = doc.RootElement.GetProperty("runningTasks")[0];

        Assert.Equal(7, running.GetProperty("stageNumber").GetInt32());
        Assert.Equal("Stage 7", running.GetProperty("stageName").GetString());
        Assert.Equal("frontier", running.GetProperty("tier").GetString());
    }

    [AvaloniaFact]
    public async Task BuildStateJson_RunningTasks_DropTasksThatAreNoLongerRunning()
    {
        var api = NewApi(out var vm);
        vm.RestoreRunningTaskState("alpha", 6, "Implement");
        RelayEventTestDispatch.Dispatch(
            vm, RelayEventTestDispatch.StageStart("alpha", 6, DateTimeOffset.UtcNow));
        // Restoring another task replaces the running set — alpha is no longer running.
        vm.RestoreRunningTaskState("beta", 9, "Fix");

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var running = doc.RootElement.GetProperty("runningTasks");

        Assert.Equal(1, running.GetArrayLength());
        Assert.Equal("beta", running[0].GetProperty("taskId").GetString());
    }

    [AvaloniaFact]
    public async Task BuildStateJson_SessionCostUsd_SumsPricedStageDoneEvents()
    {
        var api = NewApi(out var vm);
        RelayEventTestDispatch.Dispatch(vm, PricedStageDone("alpha", 6, 0.25));
        RelayEventTestDispatch.Dispatch(vm, PricedStageDone("beta", 9, 0.5));

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());

        Assert.Equal(0.75, doc.RootElement.GetProperty("sessionCostUsd").GetDouble(), 6);
    }

    [AvaloniaFact]
    public async Task BuildStateJson_DrainHalted_ReportsTheMarkerReason()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(
            Path.Combine(repo.Root, ".relay", "DRAIN-HALTED"),
            "commit gate rejected consecutive tasks\nlast task alpha\n");
        var api = NewApi(out var vm);
        vm.RootPath = repo.Root;

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());

        Assert.True(doc.RootElement.GetProperty("drainHalted").GetBoolean());
        Assert.Equal(
            "commit gate rejected consecutive tasks\nlast task alpha",
            doc.RootElement.GetProperty("haltReason").GetString());
    }

    [AvaloniaFact]
    public async Task BuildStateJson_TaskEntries_CarryCostDurationAndStageCounts()
    {
        var api = NewApi(out var vm);
        var item = new RelayTaskItem(
            "alpha", "/tmp/alpha.md", "/tmp", false, [],
            ReviewReason: "tests failed", CostUsd: 1.5, DurationSeconds: 42,
            CompletedStageCount: 7, SettledStageCount: 8, PipelineStageCount: 12);
        vm.Tasks.Add(new TaskRowViewModel(item));
        vm.SelectedTask = vm.Tasks[0];

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        JsonElement[] entries =
        [
            doc.RootElement.GetProperty("tasks")[0],
            doc.RootElement.GetProperty("selectedTask")
        ];

        foreach (var entry in entries)
        {
            Assert.Equal(1.5, entry.GetProperty("costUsd").GetDouble(), 6);
            Assert.Equal(42d, entry.GetProperty("durationSeconds").GetDouble(), 6);
            Assert.Equal(7, entry.GetProperty("completedStageCount").GetInt32());
            Assert.Equal(8, entry.GetProperty("settledStageCount").GetInt32());
            Assert.Equal(12, entry.GetProperty("pipelineStageCount").GetInt32());
            Assert.Equal("tests failed", entry.GetProperty("reviewReason").GetString());
        }
    }

    private static RelayEvent PricedStageDone(string taskId, int stage, double costUsd) =>
        new(DateTimeOffset.UtcNow, "info", "stage_done", "test-run", "/root", taskId, stage, "balanced",
            Data: new Dictionary<string, string>
            {
                ["name"] = $"Stage {stage}",
                ["costUsd"] = costUsd.ToString(CultureInfo.InvariantCulture)
            });
}
