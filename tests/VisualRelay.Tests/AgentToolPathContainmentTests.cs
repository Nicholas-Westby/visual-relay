using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// The containment boundary: every file tool resolves paths against the run's
/// target root and refuses anything that lands outside it, however the escape is
/// spelled — <c>..</c>, an absolute path, or a symlink pointing out of the tree.
/// These are the tests that make the boundary real rather than assumed.
/// </summary>
public sealed class AgentToolPathContainmentTests
{
    /// <summary>A relative path climbing above the root is refused, not read.</summary>
    [Fact]
    public async Task ReadFile_RefusesParentEscape()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var secret = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(secret, "credentials");

        var escape = Path.GetRelativePath(root.Path, secret).Replace('\\', '/');
        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, $$"""{"path":"{{escape}}"}""");

        Assert.True(result.IsError);
        Assert.Contains("escapes the repository root", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An absolute path outside the root is refused.</summary>
    [Fact]
    public async Task ReadFile_RefusesAbsolutePathOutsideRoot()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var secret = Path.Combine(outside.Path, "secret.txt").Replace('\\', '/');
        await File.WriteAllTextAsync(secret, "credentials");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, $$"""{"path":"{{secret}}"}""");

        Assert.True(result.IsError);
        Assert.Contains("escapes the repository root", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An absolute path that stays inside the root is allowed through.</summary>
    [Fact]
    public async Task ReadFile_AcceptsAbsolutePathInsideRoot()
    {
        using var root = new TempDirectory();
        var file = AgentToolTestHelpers.Write(root.Path, "src/Program.cs", "class Program;\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, $$"""{"path":"{{file.Replace('\\', '/')}}"}""");

        Assert.False(result.IsError);
        Assert.Contains("class Program;", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A symlink inside the tree is not a door out of it.</summary>
    [Fact]
    public async Task ReadFile_RefusesSymlinkPointingOutOfTree()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var secret = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(secret, "credentials");
        File.CreateSymbolicLink(Path.Combine(root.Path, "leak.txt"), secret);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"leak.txt"}""");

        Assert.True(result.IsError);
        Assert.Contains("escapes the repository root", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A path reached through a symlinked directory is refused too.</summary>
    [Fact]
    public async Task ReadFile_RefusesPathThroughSymlinkedDirectory()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "credentials");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "elsewhere"), outside.Path);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ReadFileTool(), root.Path, """{"path":"elsewhere/secret.txt"}""");

        Assert.True(result.IsError);
        Assert.Contains("escapes the repository root", result.Content, StringComparison.Ordinal);
    }

    /// <summary>write_file refuses an escape and leaves the outside file untouched.</summary>
    [Fact]
    public async Task WriteFile_RefusesEscape_AndWritesNothing()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var target = Path.Combine(outside.Path, "victim.txt");
        await File.WriteAllTextAsync(target, "original");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new WriteFileTool(), root.Path,
            $$"""{"path":"{{target.Replace('\\', '/')}}","content":"clobbered"}""");

        Assert.True(result.IsError);
        Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    /// <summary>delete_file refuses an escape and leaves the outside file in place.</summary>
    [Fact]
    public async Task DeleteFile_RefusesEscape_AndDeletesNothing()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        var target = Path.Combine(outside.Path, "victim.txt");
        await File.WriteAllTextAsync(target, "original");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new DeleteFileTool(), root.Path, $$"""{"path":"{{target.Replace('\\', '/')}}"}""");

        Assert.True(result.IsError);
        Assert.True(File.Exists(target));
    }

    /// <summary>Version-control internals are refused by every mutating tool.</summary>
    [Fact]
    public async Task MutatingTools_RefuseGitInternals()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, ".git/index", "binary-ish");

        var write = await AgentToolTestHelpers.InvokeAsync(
            new WriteFileTool(), root.Path, """{"path":".git/index","content":"x"}""");
        var delete = await AgentToolTestHelpers.InvokeAsync(
            new DeleteFileTool(), root.Path, """{"path":".git/index"}""");
        var edit = await AgentToolTestHelpers.InvokeAsync(
            new EditFileTool(), root.Path,
            """{"path":".git/index","old_string":"binary","new_string":"y"}""");

        Assert.True(write.IsError);
        Assert.True(delete.IsError);
        Assert.True(edit.IsError);
        Assert.Contains("version-control internals", write.Content, StringComparison.Ordinal);
        Assert.Equal("binary-ish", await File.ReadAllTextAsync(Path.Combine(root.Path, ".git", "index")));
    }

    /// <summary>list_files names a symlinked directory but never walks through it.</summary>
    [Fact]
    public async Task ListFiles_DoesNotFollowDirectorySymlinkOutOfTree()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "credentials");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "elsewhere"), outside.Path);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ListFilesTool(), root.Path, """{"path":".","depth":4}""");

        Assert.False(result.IsError);
        Assert.Contains("symlink (not followed)", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", result.Content, StringComparison.Ordinal);
    }

    /// <summary>grep does not search through a symlinked directory either.</summary>
    [Fact]
    public async Task Grep_DoesNotSearchThroughDirectorySymlink()
    {
        using var root = new TempDirectory();
        using var outside = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(outside.Path, "secret.txt"), "needle out here");
        AgentToolTestHelpers.Write(root.Path, "inside.txt", "needle in here");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "elsewhere"), outside.Path);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new GrepTool(), root.Path, """{"pattern":"needle"}""");

        Assert.False(result.IsError);
        Assert.Contains("inside.txt:1:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An empty path is a usable error, not an exception.</summary>
    [Fact]
    public async Task EmptyPath_IsAnActionableError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(new ReadFileTool(), root.Path, """{"path":""}""");

        Assert.True(result.IsError);
        Assert.Contains("path is required", result.Content, StringComparison.Ordinal);
        Assert.Contains("list_files", result.Content, StringComparison.Ordinal);
    }
}
