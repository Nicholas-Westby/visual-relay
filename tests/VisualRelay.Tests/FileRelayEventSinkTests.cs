using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class FileRelayEventSinkTests
{
    [Fact]
    public async Task PublishAsync_WritesOneLinePerEvent_InOrder_WithFullData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(dir, "run.log");
        var sink = new FileRelayEventSink(path);
        var longReason = new string('x', 500) + " full untruncated failure detail";

        await sink.PublishAsync(new RelayEvent(
            new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero),
            "info",
            "run_start",
            "run-1",
            "/root",
            "task-a",
            Data: new Dictionary<string, string> { ["base_url"] = "http://127.0.0.1:4000" }));
        await sink.PublishAsync(new RelayEvent(
            new DateTimeOffset(2026, 6, 4, 12, 0, 1, TimeSpan.Zero),
            "info",
            "stage_start",
            "run-1",
            "/root",
            "task-a",
            StageNumber: 3,
            Tier: "balanced",
            Data: new Dictionary<string, string> { ["name"] = "evidence" }));
        await sink.PublishAsync(new RelayEvent(
            new DateTimeOffset(2026, 6, 4, 12, 0, 2, TimeSpan.Zero),
            "error",
            "flagged",
            "run-1",
            "/root",
            "task-a",
            StageNumber: 9,
            Data: new Dictionary<string, string> { ["reason"] = longReason }));

        var lines = await File.ReadAllLinesAsync(path);

        Assert.Equal(3, lines.Length);
        Assert.Contains("2026-06-04T12:00:00.0000000+00:00", lines[0]);
        Assert.Contains("run=run-1", lines[0]);
        Assert.Contains("run_start", lines[0]);
        Assert.Contains("base_url=http://127.0.0.1:4000", lines[0]);
        // No stage scope -> placeholder dash.
        Assert.Contains(" - ", lines[0]);

        Assert.Contains("stage_start", lines[1]);
        Assert.Contains("s3/balanced", lines[1]);
        Assert.Contains("name=evidence", lines[1]);

        Assert.Contains("flagged", lines[2]);
        Assert.Contains("error", lines[2]);
        // Stage number with no tier still records a scope token.
        Assert.Contains("s9/", lines[2]);
        // The FULL reason must be present, untruncated.
        Assert.Contains(longReason, lines[2]);
    }

    [Fact]
    public async Task PublishAsync_IsAppendOnly_AcrossMultipleCalls()
    {
        var path = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"), "run.log");
        var sink = new FileRelayEventSink(path);

        await sink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "first", "run-1", "/root"));
        await sink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "second", "run-1", "/root"));

        var lines = await File.ReadAllLinesAsync(path);

        Assert.Equal(2, lines.Length);
        Assert.Contains("first", lines[0]);
        Assert.Contains("second", lines[1]);
    }

    [Fact]
    public async Task PublishAsync_CollapsesNewlinesInData_SoOneEventStaysOneLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"), "run.log");
        var sink = new FileRelayEventSink(path);

        // A trace's content (or a multi-line error reason) routinely spans lines;
        // it must not split one event across multiple physical log lines or `tail`
        // and line-oriented tooling break.
        await sink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow,
            "info",
            "trace",
            "run-1",
            "/root",
            "task-a",
            StageNumber: 6,
            Tier: "frontier",
            Data: new Dictionary<string, string> { ["content"] = "line one\nline two\r\nline three" }));

        var lines = await File.ReadAllLinesAsync(path);

        Assert.Single(lines);
        Assert.Contains("line one line two line three", lines[0]);
    }

    [Fact]
    public async Task PublishAsync_CreatesMissingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"), "a", "b", "run.log");
        var sink = new FileRelayEventSink(path);

        await sink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, "info", "only", "run-1", "/root"));

        Assert.True(File.Exists(path));
    }
}
