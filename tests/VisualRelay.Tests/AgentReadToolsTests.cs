using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// read_file and read_multiple_files: exact text out, bounded size, and failures
/// phrased as the next thing to try.
/// </summary>
public sealed class AgentReadToolsTests
{
    /// <summary>A small file comes back verbatim under a header naming its size.</summary>
    [Fact]
    public async Task ReadFile_ReturnsExactText_UnderAHeader()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "class A\n{\n    // body\n}\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"src/Program.cs"}""");

        Assert.False(result.IsError);
        Assert.Contains("src/Program.cs — 4 lines", result.Content, StringComparison.Ordinal);
        Assert.Contains("class A\n{\n    // body\n}\n", result.Content, StringComparison.Ordinal);
        Assert.Null(result.Images);
    }

    /// <summary>An offset reads a window rather than the whole file.</summary>
    [Fact]
    public async Task ReadFile_Offset_ReadsAWindow()
    {
        using var root = new TempDirectory();
        var lines = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}"));
        AgentToolTestHelpers.Write(root.Path, "notes.txt", lines);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"notes.txt","offset":10,"limit":3}""");

        Assert.False(result.IsError);
        Assert.Contains("lines 10-12", result.Content, StringComparison.Ordinal);
        Assert.Contains("line 10\nline 11\nline 12\n", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("line 13", result.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Past the line cap the output stops, says how much it showed, and names the
    /// offset that continues it — a truncated read must still be actionable.
    /// </summary>
    [Fact]
    public async Task ReadFile_LongFile_TruncatesWithAContinuationOffset()
    {
        using var root = new TempDirectory();
        var lines = string.Join('\n', Enumerable.Range(1, 2_600).Select(i => $"line {i}"));
        AgentToolTestHelpers.Write(root.Path, "big.txt", lines);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"big.txt"}""");

        Assert.False(result.IsError);
        Assert.Contains("line 2000\n", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("line 2001\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("read_file: truncated", result.Content, StringComparison.Ordinal);
        Assert.Contains("offset=2001", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An offset past the end explains itself instead of returning nothing.</summary>
    [Fact]
    public async Task ReadFile_OffsetPastEnd_SaysSo()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "short.txt", "one\ntwo\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"short.txt","offset":99}""");

        Assert.False(result.IsError);
        Assert.Contains("past the end of the file", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A missing file's error lists what IS in the directory.</summary>
    [Fact]
    public async Task ReadFile_MissingFile_ListsSiblings()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "x");
        AgentToolTestHelpers.Write(root.Path, "src/Helper.cs", "y");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"src/Programs.cs"}""");

        Assert.True(result.IsError);
        Assert.Contains("no such file \"src/Programs.cs\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("Program.cs", result.Content, StringComparison.Ordinal);
        Assert.Contains("Helper.cs", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A directory is redirected to list_files rather than read as text.</summary>
    [Fact]
    public async Task ReadFile_Directory_PointsAtListFiles()
    {
        using var root = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"src"}""");

        Assert.True(result.IsError);
        Assert.Contains("is a directory", result.Content, StringComparison.Ordinal);
        Assert.Contains("list_files", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Binary content is refused with a pointer at view_image.</summary>
    [Fact]
    public async Task ReadFile_BinaryFile_IsRefused()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "blob.bin"), [0x00, 0x01, 0x02, 0x00, 0x03]);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"blob.bin"}""");

        Assert.True(result.IsError);
        Assert.Contains("binary file", result.Content, StringComparison.Ordinal);
        Assert.Contains("view_image", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A batch returns every file, each under its own path header.</summary>
    [Fact]
    public async Task ReadMultipleFiles_ReturnsEachFile()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "a.txt", "alpha");
        AgentToolTestHelpers.Write(root.Path, "b.txt", "beta");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadMultipleFilesTool(), root.Path, """{"paths":["a.txt","b.txt"]}""");

        Assert.False(result.IsError);
        Assert.Contains("a.txt", result.Content, StringComparison.Ordinal);
        Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
        Assert.Contains("beta", result.Content, StringComparison.Ordinal);
    }

    /// <summary>One bad path in a batch does not cost the model the good ones.</summary>
    [Fact]
    public async Task ReadMultipleFiles_ReportsPerFileFailuresInline()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "a.txt", "alpha");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadMultipleFilesTool(), root.Path, """{"paths":["a.txt","missing.txt","../escape.txt"]}""");

        Assert.False(result.IsError);
        Assert.Contains("alpha", result.Content, StringComparison.Ordinal);
        Assert.Contains("no such file \"missing.txt\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("escapes the repository root", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A batch where nothing could be read is an error the loop counts.</summary>
    [Fact]
    public async Task ReadMultipleFiles_AllFailing_IsAnError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadMultipleFilesTool(), root.Path, """{"paths":["one.txt","two.txt"]}""");

        Assert.True(result.IsError);
        Assert.Contains("none of the 2 paths", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Beyond the batch's file count the extras are named as dropped.</summary>
    [Fact]
    public async Task ReadMultipleFiles_BeyondTheFileCap_SaysWhatItDropped()
    {
        using var root = new TempDirectory();
        var paths = new List<string>();
        for (var index = 0; index < 25; index++)
        {
            AgentToolTestHelpers.Write(root.Path, $"f{index}.txt", "x");
            paths.Add($"\"f{index}.txt\"");
        }

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadMultipleFilesTool(), root.Path, $$"""{"paths":[{{string.Join(',', paths)}}]}""");

        Assert.False(result.IsError);
        Assert.Contains("read_multiple_files: truncated — 5 path(s) not read", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A file over the per-file cap is clipped and points at read_file.</summary>
    [Fact]
    public async Task ReadMultipleFiles_ClipsAFileOverTheCap()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "big.txt", new string('x', 40_000));

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadMultipleFilesTool(), root.Path, """{"paths":["big.txt"]}""");

        Assert.False(result.IsError);
        Assert.Contains("clipped at 16 KiB", result.Content, StringComparison.Ordinal);
        Assert.Contains("read_file", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < 40_000);
    }

    /// <summary>A file at the size ceiling is refused rather than streamed into the prompt.</summary>
    [Fact]
    public async Task ReadFile_OverTheSizeCeiling_IsRefused()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "huge.log");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(33L * 1024 * 1024);
        }

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"huge.log"}""");

        Assert.True(result.IsError);
        Assert.Contains("over the 32 MiB ceiling", result.Content, StringComparison.Ordinal);
        Assert.Contains("grep", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An empty file says it is empty instead of returning a bare header.</summary>
    [Fact]
    public async Task ReadFile_EmptyFile_SaysSo()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "empty.txt", string.Empty);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"empty.txt"}""");

        Assert.False(result.IsError);
        Assert.Contains("empty file (0 bytes)", result.Content, StringComparison.Ordinal);
    }
}
