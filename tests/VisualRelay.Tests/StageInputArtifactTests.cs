using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class StageInputArtifactTests
{
    [Fact]
    public void PathFor_SwapsReportExtensionToInput()
    {
        var result = StageInputArtifact.PathFor(
            "/tmp/.relay/task/stage3-attempt2.report.json");
        Assert.Equal("/tmp/.relay/task/stage3-attempt2.input.json", result);
    }

    [Fact]
    public void PathFor_HandlesWindowsBackslash()
    {
        // Path.GetDirectoryName on Unix returns '/' even with backslashes as
        // part of a filename, so this is mostly a no-op for portability; the
        // test documents the expected behaviour on the current platform.
        var result = StageInputArtifact.PathFor(
            @"C:\relay\task\stage1-attempt1.report.json");
        // On Unix the backslashes are treated as literal characters in the filename.
        Assert.NotNull(result);
        Assert.Contains("stage1-attempt1.input.json", result);
    }

    [Fact]
    public void PathFor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            StageInputArtifact.PathFor(null!));
    }

    [Fact]
    public void WriteThenTryRead_RoundTrips()
    {
        using var dir = new TempDirectory();
        var reportPath = Path.Combine(dir.Path, "stage2-attempt1.report.json");
        var input = new StageInputArtifact(
            Version: 1,
            Stage: 2,
            Attempt: 1,
            Name: "Research",
            SystemPrompt: "Investigate the codebase.",
            InputPrompt: "# Relay stage 2: Research\nTask: find bugs",
            Timestamp: "2026-06-20T17:59:00Z");

        StageInputArtifact.Write(reportPath, input);

        var inputPath = StageInputArtifact.PathFor(reportPath);
        Assert.True(File.Exists(inputPath));

        Assert.True(StageInputArtifact.TryRead(inputPath, out var read));
        Assert.NotNull(read);
        Assert.Equal(1, read.Version);
        Assert.Equal(2, read.Stage);
        Assert.Equal(1, read.Attempt);
        Assert.Equal("Research", read.Name);
        Assert.Equal("Investigate the codebase.", read.SystemPrompt);
        Assert.Equal("# Relay stage 2: Research\nTask: find bugs", read.InputPrompt);
        Assert.Equal("2026-06-20T17:59:00Z", read.Timestamp);
    }

    [Fact]
    public void TryRead_ReturnsFalseOnMissingFile()
    {
        Assert.False(StageInputArtifact.TryRead(
            "/nonexistent/path/stage1-attempt99.input.json", out var data));
        Assert.Null(data);
    }

    [Fact]
    public void TryRead_ReturnsFalseOnCorruptJson()
    {
        using var dir = new TempDirectory();
        var badPath = Path.Combine(dir.Path, "corrupt.input.json");
        File.WriteAllText(badPath, "this is not json");

        Assert.False(StageInputArtifact.TryRead(badPath, out var data));
        Assert.Null(data);
    }

    [Fact]
    public void TryRead_ReturnsFalseOnEmptyFile()
    {
        using var dir = new TempDirectory();
        var emptyPath = Path.Combine(dir.Path, "empty.input.json");
        File.WriteAllText(emptyPath, "");

        Assert.False(StageInputArtifact.TryRead(emptyPath, out var data));
        Assert.Null(data);
    }

    [Fact]
    public void LatestPath_ReturnsNullOnMissingDirectory()
    {
        Assert.Null(StageInputArtifact.LatestPath("/nonexistent", 1));
    }

    [Fact]
    public void LatestPath_PicksMaxAttemptIgnoringMtime()
    {
        using var dir = new TempDirectory();

        // Write attempt 1 first
        var path1 = Path.Combine(dir.Path, "stage5-attempt1.input.json");
        File.WriteAllText(path1, """{"version":1,"stage":5,"attempt":1,"name":"Author-tests","systemPrompt":"Write tests","inputPrompt":"test","timestamp":"2026-06-20T01:00:00Z"}""");
        var mtime1 = File.GetLastWriteTimeUtc(path1);

        // Write attempt 3 with an OLDER mtime (simulating file-system timestamp skew)
        var path3 = Path.Combine(dir.Path, "stage5-attempt3.input.json");
        File.WriteAllText(path3, """{"version":1,"stage":5,"attempt":3,"name":"Author-tests","systemPrompt":"Write tests","inputPrompt":"test","timestamp":"2026-06-20T03:00:00Z"}""");
        File.SetLastWriteTimeUtc(path3, mtime1.AddDays(-1)); // force older mtime

        // Write attempt 2 with a NEWER mtime
        Thread.Sleep(10); // ensure distinct mtime
        var path2 = Path.Combine(dir.Path, "stage5-attempt2.input.json");
        File.WriteAllText(path2, """{"version":1,"stage":5,"attempt":2,"name":"Author-tests","systemPrompt":"Write tests","inputPrompt":"test","timestamp":"2026-06-20T02:00:00Z"}""");

        var latest = StageInputArtifact.LatestPath(dir.Path, 5);
        Assert.Equal(path3, latest); // attempt 3, even though its mtime is oldest
    }

    [Fact]
    public void LatestPath_FiltersByStage()
    {
        using var dir = new TempDirectory();

        File.WriteAllText(Path.Combine(dir.Path, "stage5-attempt1.input.json"),
            """{"version":1,"stage":5,"attempt":1,"name":"X","systemPrompt":"","inputPrompt":"","timestamp":"Z"}""");
        File.WriteAllText(Path.Combine(dir.Path, "stage6-attempt1.input.json"),
            """{"version":1,"stage":6,"attempt":1,"name":"Y","systemPrompt":"","inputPrompt":"","timestamp":"Z"}""");

        var latest = StageInputArtifact.LatestPath(dir.Path, 5);
        Assert.NotNull(latest);
        Assert.Contains("stage5-attempt1", latest);
    }

    /// <summary>
    /// Creates a temporary directory that is deleted on Dispose, for use in
    /// unit tests that need file-system isolation without the full
    /// TestRepository fixture.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vr-artifact-tests",
                Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
