using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// write_file, edit_file and delete_file. The interesting cases are the refusals:
/// an edit that cannot be placed exactly must fail loudly and write nothing.
/// </summary>
public sealed class AgentMutationToolsTests
{
    /// <summary>Writing creates missing parent directories.</summary>
    [Fact]
    public async Task WriteFile_CreatesParentDirectories()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new WriteFileTool(), root.Path, """{"path":"src/deep/New.cs","content":"class New;\n"}""");

        Assert.False(result.IsError);
        Assert.Contains("created \"src/deep/New.cs\"", result.Content, StringComparison.Ordinal);
        Assert.Equal("class New;\n", await File.ReadAllTextAsync(Path.Combine(root.Path, "src", "deep", "New.cs")));
    }

    /// <summary>Replacing an existing file reports both sizes, so an accident is visible.</summary>
    [Fact]
    public async Task WriteFile_ReplacingExisting_ReportsTheOldSize()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "a.txt", "the original contents");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new WriteFileTool(), root.Path, """{"path":"a.txt","content":"new"}""");

        Assert.False(result.IsError);
        Assert.Contains("replaced \"a.txt\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("was 21 B", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A missing content argument is an error, not an accidental truncation.</summary>
    [Fact]
    public async Task WriteFile_WithoutContent_IsAnError()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "a.txt", "keep me");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new WriteFileTool(), root.Path, """{"path":"a.txt"}""");

        Assert.True(result.IsError);
        Assert.Contains("'content' is required", result.Content, StringComparison.Ordinal);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(root.Path, "a.txt")));
    }

    /// <summary>A unique old_string is replaced, and the report names the line.</summary>
    [Fact]
    public async Task EditFile_ReplacesAUniqueString()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "class A\n{\n    int x = 1;\n}\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/A.cs","old_string":"int x = 1;","new_string":"int x = 42;"}""");

        Assert.False(result.IsError);
        Assert.Contains("replaced 1 occurrence(s)", result.Content, StringComparison.Ordinal);
        Assert.Contains("line 3", result.Content, StringComparison.Ordinal);
        Assert.Contains("int x = 42;", await File.ReadAllTextAsync(Path.Combine(root.Path, "src", "A.cs")));
    }

    /// <summary>An absent old_string fails and leaves the file byte-identical.</summary>
    [Fact]
    public async Task EditFile_MissingOldString_FailsWithoutWriting()
    {
        using var root = new TempDirectory();
        const string original = "class A\n{\n}\n";
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", original);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/A.cs","old_string":"int y = 2;","new_string":"int y = 3;"}""");

        Assert.True(result.IsError);
        Assert.Contains("does not appear", result.Content, StringComparison.Ordinal);
        Assert.Contains("nothing was written", result.Content, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(root.Path, "src", "A.cs")));
    }

    /// <summary>
    /// When only whitespace differs the error says so, which is the difference
    /// between one more attempt and five.
    /// </summary>
    [Fact]
    public async Task EditFile_WhitespaceOnlyMismatch_SaysWhichWayItIsWrong()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "class A\n{\n    int x = 1;\n}\n");

        // The file is indented with four spaces; the model guessed eight.
        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/A.cs","old_string":"        int x = 1;","new_string":"        int x = 2;"}""");

        Assert.True(result.IsError);
        Assert.Contains("apart from whitespace", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An ambiguous match fails, names the lines, and writes nothing.</summary>
    [Fact]
    public async Task EditFile_AmbiguousMatch_FailsWithoutWriting()
    {
        using var root = new TempDirectory();
        const string original = "value = 1;\nother();\nvalue = 1;\n";
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", original);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/A.cs","old_string":"value = 1;","new_string":"value = 2;"}""");

        Assert.True(result.IsError);
        Assert.Contains("appears 2 times", result.Content, StringComparison.Ordinal);
        Assert.Contains("lines 1, 3", result.Content, StringComparison.Ordinal);
        Assert.Contains("replace_all=true", result.Content, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(root.Path, "src", "A.cs")));
    }

    /// <summary>replace_all is the deliberate opt-in for an ambiguous edit.</summary>
    [Fact]
    public async Task EditFile_ReplaceAll_ReplacesEveryOccurrence()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "value = 1;\nother();\nvalue = 1;\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/A.cs","old_string":"value = 1;","new_string":"value = 2;","replace_all":true}""");

        Assert.False(result.IsError);
        Assert.Contains("replaced 2 occurrence(s)", result.Content, StringComparison.Ordinal);
        Assert.Equal(
            "value = 2;\nother();\nvalue = 2;\n",
            await File.ReadAllTextAsync(Path.Combine(root.Path, "src", "A.cs")));
    }

    /// <summary>An edit that would change nothing is refused before it burns a turn.</summary>
    [Fact]
    public async Task EditFile_IdenticalStrings_IsAnError()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "same\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/A.cs","old_string":"same","new_string":"same"}""");

        Assert.True(result.IsError);
        Assert.Contains("identical", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Editing a file that is not there points at how to find the real one.</summary>
    [Fact]
    public async Task EditFile_MissingFile_IsAnError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":"src/Nope.cs","old_string":"a","new_string":"b"}""");

        Assert.True(result.IsError);
        Assert.Contains("no such file", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Deleting removes the file and reports what it removed.</summary>
    [Fact]
    public async Task DeleteFile_RemovesTheFile()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "stale.txt", "gone soon");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new DeleteFileTool(), root.Path, """{"path":"stale.txt"}""");

        Assert.False(result.IsError);
        Assert.Contains("deleted \"stale.txt\"", result.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root.Path, "stale.txt")));
    }

    /// <summary>A directory is never deleted, however the model asks.</summary>
    [Fact]
    public async Task DeleteFile_Directory_IsRefused()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "x");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new DeleteFileTool(), root.Path, """{"path":"src"}""");

        Assert.True(result.IsError);
        Assert.Contains("is a directory", result.Content, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "src")));
    }

    /// <summary>Deleting something already gone is an error, not a silent success.</summary>
    [Fact]
    public async Task DeleteFile_MissingFile_IsAnError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new DeleteFileTool(), root.Path, """{"path":"never.txt"}""");

        Assert.True(result.IsError);
        Assert.Contains("no such file", result.Content, StringComparison.Ordinal);
    }
}
