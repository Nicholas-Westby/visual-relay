using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// list_files and grep: the two tools that can produce unbounded output, so the
/// caps and the "how to narrow it" markers are as much the subject here as the
/// results themselves.
/// </summary>
public sealed class AgentSearchToolsTests
{
    /// <summary>Directories are marked, files are listed as the model should type them.</summary>
    [Fact]
    public async Task ListFiles_ListsTheTree()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "x");
        AgentToolTestHelpers.Write(root.Path, "README.md", "y");

        var result = await AgentToolTestHelpers.InvokeAsync(new ListFilesTool(), root.Path, """{}""");

        Assert.False(result.IsError);
        Assert.Contains("README.md", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/Program.cs", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Build output, VCS internals and dot-files stay out of the listing.</summary>
    [Fact]
    public async Task ListFiles_SkipsBuildOutputAndHiddenEntries()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "x");
        AgentToolTestHelpers.Write(root.Path, "src/obj/Generated.cs", "x");
        AgentToolTestHelpers.Write(root.Path, ".git/config", "x");
        AgentToolTestHelpers.Write(root.Path, "node_modules/pkg/index.js", "x");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"path":".","depth":5}""");

        Assert.Contains("src/Program.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A glob filters file names without hiding the directories they live in.</summary>
    [Fact]
    public async Task ListFiles_Pattern_FiltersFileNames()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "x");
        AgentToolTestHelpers.Write(root.Path, "src/notes.txt", "x");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"path":"src","pattern":"*.cs"}""");

        Assert.Contains("src/Program.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Depth 1 stops at the immediate children.</summary>
    [Fact]
    public async Task ListFiles_Depth_LimitsDescent()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/deep/Deep.cs", "x");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"depth":1}""");

        Assert.Contains("src/", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Deep.cs", result.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Over the entry cap the listing truncates, reports the true total, and says
    /// which arguments narrow the next call.
    /// </summary>
    [Fact]
    public async Task ListFiles_OverTheCap_TruncatesAndSaysHowToNarrow()
    {
        using var root = new TempDirectory();
        for (var index = 0; index < 620; index++)
            AgentToolTestHelpers.Write(root.Path, $"many/f{index:D4}.txt", "x");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"path":"many"}""");

        Assert.False(result.IsError);
        Assert.Contains("620 entries", result.Content, StringComparison.Ordinal);
        Assert.Contains("list_files: truncated — showed 500 of 620 entries", result.Content, StringComparison.Ordinal);
        Assert.Contains("pattern", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Pointed at a file, list_files confirms the file exists — the prompt's use.</summary>
    [Fact]
    public async Task ListFiles_OnAFile_ConfirmsItExists()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "hello");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"path":"src/Program.cs"}""");

        Assert.False(result.IsError);
        Assert.Contains("exists and is a file", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A directory that is not there is an error naming the fallback.</summary>
    [Fact]
    public async Task ListFiles_MissingDirectory_IsAnError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"path":"nope"}""");

        Assert.True(result.IsError);
        Assert.Contains("no such directory", result.Content, StringComparison.Ordinal);
    }

    /// <summary>grep returns path, line number and the matching text.</summary>
    [Fact]
    public async Task Grep_ReturnsPathLineAndText()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "class A\n{\n    void Run() { }\n}\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"void \\w+\\(\\)"}""");

        Assert.False(result.IsError);
        Assert.Contains("1 match(es) in 1 file(s)", result.Content, StringComparison.Ordinal);
        // Leading indentation is preserved: it is what edit_file's old_string must match.
        Assert.Contains("src/A.cs:3:     void Run() { }", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A glob keeps the search off files the model did not mean.</summary>
    [Fact]
    public async Task Grep_Glob_RestrictsTheSearch()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "needle\n");
        AgentToolTestHelpers.Write(root.Path, "src/notes.txt", "needle\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle","glob":"*.cs"}""");

        Assert.Contains("src/A.cs:1:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", result.Content, StringComparison.Ordinal);
    }

    /// <summary>No matches is a normal answer with advice, not an error.</summary>
    [Fact]
    public async Task Grep_NoMatches_IsNotAnError()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "class A\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"nowhere"}""");

        Assert.False(result.IsError);
        Assert.Contains("no matches", result.Content, StringComparison.Ordinal);
        Assert.Contains("case-insensitive", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A malformed regex is reported as one, with the escaping advice.</summary>
    [Fact]
    public async Task Grep_InvalidRegex_IsAnActionableError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"foo("}""");

        Assert.True(result.IsError);
        Assert.Contains("not a valid regular expression", result.Content, StringComparison.Ordinal);
        Assert.Contains("Escape", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Case-insensitive matching is available without rewriting the pattern.</summary>
    [Fact]
    public async Task Grep_IgnoreCase_Matches()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "NeedLe\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle","ignore_case":true}""");

        Assert.False(result.IsError);
        Assert.Contains("src/A.cs:1: NeedLe", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Past the match cap grep stops and says how to search more narrowly.</summary>
    [Fact]
    public async Task Grep_OverTheMatchCap_TruncatesAndSaysHowToNarrow()
    {
        using var root = new TempDirectory();
        var lines = string.Join('\n', Enumerable.Repeat("needle here", 260));
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", lines);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle"}""");

        Assert.False(result.IsError);
        Assert.Contains("200 match(es)", result.Content, StringComparison.Ordinal);
        Assert.Contains("grep: truncated", result.Content, StringComparison.Ordinal);
        Assert.Contains("narrower 'path'", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A very long matching line is clipped rather than pasted whole.</summary>
    [Fact]
    public async Task Grep_ClipsAVeryLongLine()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "needle" + new string('x', 5_000) + "\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle"}""");

        Assert.False(result.IsError);
        Assert.Contains("chars]", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < 1_000);
    }

    /// <summary>Pointed at one file, grep searches only it and still names it in full.</summary>
    [Fact]
    public async Task Grep_OnASingleFile_SearchesOnlyThatFile()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "needle\n");
        AgentToolTestHelpers.Write(root.Path, "src/B.cs", "needle\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle","path":"src/A.cs"}""");

        Assert.False(result.IsError);
        Assert.Contains("1 match(es) in 1 file(s)", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/A.cs:1: needle", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("B.cs", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Binary files are skipped instead of being spilled into the prompt.</summary>
    [Fact]
    public async Task Grep_SkipsBinaryFiles()
    {
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(
            Path.Combine(root.Path, "blob.bin"), [.. "needle"u8.ToArray(), 0x00, 0x01]);
        AgentToolTestHelpers.Write(root.Path, "src/A.cs", "needle\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle"}""");

        Assert.Contains("src/A.cs:1:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("blob.bin", result.Content, StringComparison.Ordinal);
    }
}
