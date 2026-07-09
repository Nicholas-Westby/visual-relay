using VisualRelay.Domain;

namespace VisualRelay.Tests;

internal static class RunLogGroupingTestHelpers
{
    public static RelayEvent Heartbeat(int stageNumber, string tier, string message, DateTimeOffset? at = null) =>
        new(
            at ?? DateTimeOffset.UtcNow,
            "debug",
            "watchdog_heartbeat",
            "run-1",
            "/root",
            "task-1",
            stageNumber,
            tier,
            Data: new Dictionary<string, string> { ["message"] = message });

    public static RelayEvent StageStart(int stageNumber, string tier, DateTimeOffset? at = null) =>
        new(
            at ?? DateTimeOffset.UtcNow,
            "info",
            "stage_start",
            "run-1",
            "/root",
            "task-1",
            stageNumber,
            tier);

    public static RelayEvent Trace(int stageNumber, string tier, string title, string content, DateTimeOffset? at = null) =>
        new(
            at ?? DateTimeOffset.UtcNow,
            "info",
            "trace",
            "run-1",
            "/root",
            "task-1",
            stageNumber,
            tier,
            Data: new Dictionary<string, string> { ["title"] = title, ["content"] = content });
}
