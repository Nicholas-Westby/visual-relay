using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// outline and view_image: the two tools that summarise a file rather than
/// return it. view_image is also where an image reaches the conversation, so its
/// result carries the encoded picture in <see cref="ToolResult.Images"/>.
/// </summary>
public sealed class AgentInspectionToolsTests
{
    /// <summary>A C# file's types and members come back with their line numbers.</summary>
    [Fact]
    public async Task Outline_ListsTypesAndMembers()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "src/A.cs",
            "namespace Sample;\n\npublic sealed class Widget\n{\n"
            + "    public int Size { get; }\n\n    public void Render()\n    {\n    }\n}\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new OutlineTool(), root.Path, """{"path":"src/A.cs"}""");

        Assert.False(result.IsError);
        Assert.Contains("heuristic line scan", result.Content, StringComparison.Ordinal);
        Assert.Contains("1: namespace Sample;", result.Content, StringComparison.Ordinal);
        Assert.Contains("3: public sealed class Widget", result.Content, StringComparison.Ordinal);
        Assert.Contains("public int Size { get; }", result.Content, StringComparison.Ordinal);
        Assert.Contains("public void Render()", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Python declarations are recognised as well — the scan is not C#-only.</summary>
    [Fact]
    public async Task Outline_HandlesAnotherLanguage()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "tool.py",
            "import os\n\nclass Runner:\n    def run(self):\n        return 1\n\ndef main():\n    pass\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new OutlineTool(), root.Path, """{"path":"tool.py"}""");

        Assert.False(result.IsError);
        Assert.Contains("class Runner:", result.Content, StringComparison.Ordinal);
        Assert.Contains("def run(self):", result.Content, StringComparison.Ordinal);
        Assert.Contains("def main():", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("import os", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A Markdown file outlines to its headings.</summary>
    [Fact]
    public async Task Outline_Markdown_ListsHeadings()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "docs/notes.md",
            "# Title\n\nSome prose.\n\n## Section one\n\nMore prose.\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new OutlineTool(), root.Path, """{"path":"docs/notes.md"}""");

        Assert.False(result.IsError);
        Assert.Contains("# Title", result.Content, StringComparison.Ordinal);
        Assert.Contains("## Section one", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Some prose", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A file with no declarations says so and names the alternatives.</summary>
    [Fact]
    public async Task Outline_WithNothingToShow_SaysSo()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "data.csv", "a,b,c\n1,2,3\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new OutlineTool(), root.Path, """{"path":"data.csv"}""");

        Assert.False(result.IsError);
        Assert.Contains("no declaration-shaped lines found", result.Content, StringComparison.Ordinal);
        Assert.Contains("read_file", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Beyond the declaration cap the outline truncates and says so.</summary>
    [Fact]
    public async Task Outline_OverTheCap_Truncates()
    {
        using var root = new TempDirectory();
        var members = string.Join('\n', Enumerable.Range(0, 450).Select(i => $"    public int P{i} => {i};"));
        AgentToolTestHelpers.Write(root.Path, "src/Big.cs", $"public class Big\n{{\n{members}\n}}\n");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new OutlineTool(), root.Path, """{"path":"src/Big.cs"}""");

        Assert.False(result.IsError);
        Assert.Contains("outline: truncated", result.Content, StringComparison.Ordinal);
        Assert.Contains("first 400 declarations", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Outlining a file that is not there is an actionable error.</summary>
    [Fact]
    public async Task Outline_MissingFile_IsAnError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new OutlineTool(), root.Path, """{"path":"src/Gone.cs"}""");

        Assert.True(result.IsError);
        Assert.Contains("no such file", result.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// view_image returns the picture as a data URI in Images, and describes it in
    /// the text. The URI is what the loop turns into a ContentPart.FromImage part.
    /// </summary>
    [Fact]
    public async Task ViewImage_ReturnsADataUriAndDescribesTheImage()
    {
        using var root = new TempDirectory();
        var png = AgentToolTestHelpers.Png(1440, 900);
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "shot.png"), png);

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ViewImageTool(), root.Path, """{"path":"shot.png"}""");

        Assert.False(result.IsError);
        var image = Assert.Single(result.Images!);
        Assert.StartsWith("data:image/png;base64,", image, StringComparison.Ordinal);
        Assert.Equal(Convert.ToBase64String(png), image["data:image/png;base64,".Length..]);
        Assert.Contains("1440×900", result.Content, StringComparison.Ordinal);
        Assert.Contains("shot.png", result.Content, StringComparison.Ordinal);
    }

    /// <summary>The render stage hands over root-relative paths, and those work.</summary>
    [Fact]
    public async Task ViewImage_AcceptsThePathShapeTheVisualReviewPromptLists()
    {
        using var root = new TempDirectory();
        var directory = Path.Combine(root.Path, "llm-tasks", "demo", "visual-review");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "main.png"), AgentToolTestHelpers.Png(800, 600));

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ViewImageTool(), root.Path, """{"path":"llm-tasks/demo/visual-review/main.png"}""");

        Assert.False(result.IsError);
        Assert.NotNull(result.Images);
        Assert.Contains("800×600", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A text file is refused with a pointer at read_file.</summary>
    [Fact]
    public async Task ViewImage_NonImage_IsAnError()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "notes.txt", "not a picture");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ViewImageTool(), root.Path, """{"path":"notes.txt"}""");

        Assert.True(result.IsError);
        Assert.Null(result.Images);
        Assert.Contains("not a supported image", result.Content, StringComparison.Ordinal);
        Assert.Contains("read_file", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A .png that is not a PNG is caught before it reaches the provider.</summary>
    [Fact]
    public async Task ViewImage_WrongMagicBytes_IsAnError()
    {
        using var root = new TempDirectory();
        AgentToolTestHelpers.Write(root.Path, "broken.png", "this is not a png");

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ViewImageTool(), root.Path, """{"path":"broken.png"}""");

        Assert.True(result.IsError);
        Assert.Contains("contents are not a image/png image", result.Content, StringComparison.Ordinal);
    }

    /// <summary>An image over the attach ceiling is refused rather than base64-ed.</summary>
    [Fact]
    public async Task ViewImage_OverTheSizeCap_IsAnError()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "huge.png");
        await File.WriteAllBytesAsync(path, AgentToolTestHelpers.Png(10, 10));
        await using (var stream = File.Open(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(6L * 1024 * 1024);
        }

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ViewImageTool(), root.Path, """{"path":"huge.png"}""");

        Assert.True(result.IsError);
        Assert.Contains("over the 5 MiB limit", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A missing image points at list_files rather than failing bare.</summary>
    [Fact]
    public async Task ViewImage_MissingFile_IsAnError()
    {
        using var root = new TempDirectory();

        var result = await AgentToolTestHelpers.InvokeAsync(
            new ViewImageTool(), root.Path, """{"path":"gone.png"}""");

        Assert.True(result.IsError);
        Assert.Contains("no such file", result.Content, StringComparison.Ordinal);
    }
}
