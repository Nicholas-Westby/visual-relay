using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Agent.Tools;

namespace VisualRelay.Tests;

/// <summary>
/// The catalog the loop advertises: which tools are in it, the wire shape of
/// their definitions, and the fact that another toolset can register into the
/// same catalog without either half knowing about the other.
/// </summary>
public sealed class AgentToolCatalogTests
{
    private static readonly string[] ExpectedNames =
    [
        "read_file", "read_multiple_files", "list_files", "grep", "outline",
        "view_image", "write_file", "edit_file", "delete_file",
    ];

    /// <summary>The file and inspection half advertises exactly its nine tools.</summary>
    [Fact]
    public void FileToolset_AdvertisesTheNineFileAndInspectionTools()
    {
        var catalog = FileToolset.RegisterInto(new ToolCatalog());

        Assert.Equal(ExpectedNames, catalog.Tools.Select(tool => tool.Definition.Name).ToArray());
    }

    /// <summary>
    /// view_image and list_files are named as literal strings by the stage
    /// prompts, so renaming either silently breaks a prompt. This asserts the
    /// coupling from both ends: the prompt text still says the name, and the
    /// catalog still answers to it.
    /// </summary>
    [Fact]
    public void PromptNamedTools_KeepTheNamesThePromptsUse()
    {
        var catalog = FileToolset.RegisterInto(new ToolCatalog());
        var prompts = string.Concat(
            File.ReadAllText(Path.Combine(RepoSetup.Root, "src/VisualRelay.Core/Execution/RelayStages.cs")),
            File.ReadAllText(Path.Combine(
                RepoSetup.Root, "src/VisualRelay.Core/Execution/RelayDriver.ReviewPairTriage.cs")),
            File.ReadAllText(Path.Combine(
                RepoSetup.Root, "src/VisualRelay.Core/Execution/ProcessRunners.ManifestValidation.cs")));

        Assert.Contains("view_image", prompts, StringComparison.Ordinal);
        Assert.Contains("list_files", prompts, StringComparison.Ordinal);
        Assert.NotNull(catalog.Find("view_image"));
        Assert.NotNull(catalog.Find("list_files"));
    }

    /// <summary>Every definition carries a description and an object schema.</summary>
    [Fact]
    public void EveryDefinition_DescribesItselfAndItsArguments()
    {
        foreach (var tool in FileToolset.Create())
        {
            var definition = tool.Definition;
            Assert.False(string.IsNullOrWhiteSpace(definition.Description), definition.Name);
            var schema = Assert.IsType<JsonObject>(definition.ParametersSchema);
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.IsType<JsonObject>(schema["properties"]);
        }
    }

    /// <summary>Definitions render in the OpenAI-compatible tools shape.</summary>
    [Fact]
    public void Definitions_RenderInTheWireShape()
    {
        var catalog = FileToolset.RegisterInto(new ToolCatalog());

        var definitions = catalog.Definitions();

        Assert.Equal(9, definitions.Count);
        var first = definitions[0].AsObject();
        Assert.Equal("function", first["type"]!.GetValue<string>());
        Assert.Equal("read_file", first["function"]!["name"]!.GetValue<string>());
        Assert.NotNull(first["function"]!["parameters"]);
    }

    /// <summary>Rendering a definition does not hand out the tool's own schema node.</summary>
    [Fact]
    public void Definitions_AreClonedPerCall()
    {
        var catalog = FileToolset.RegisterInto(new ToolCatalog());

        var parameters = catalog.Definitions()[0]["function"]!["parameters"]!;

        Assert.NotSame(catalog.Tools[0].Definition.ParametersSchema, parameters);
    }

    /// <summary>An unknown name resolves to null so the loop can recover from it.</summary>
    [Fact]
    public void Find_ReturnsNullForAnUnknownName()
    {
        var catalog = FileToolset.RegisterInto(new ToolCatalog());

        Assert.Null(catalog.Find("read_fil"));
        Assert.Same(catalog.Tools[0], catalog.Find("read_file"));
    }

    /// <summary>
    /// The catalog is additive: a second toolset registers into the same instance,
    /// which is how the command tools join without either half editing the other.
    /// </summary>
    [Fact]
    public void Catalog_TakesAdditionalToolsetsWithoutDisturbingTheFirst()
    {
        var catalog = FileToolset.RegisterInto(new ToolCatalog());

        catalog.AddRange([new StubTool("stub_one"), new StubTool("stub_two")]);

        Assert.Equal(11, catalog.Tools.Count);
        Assert.NotNull(catalog.Find("read_file"));
        Assert.NotNull(catalog.Find("stub_two"));
        Assert.Equal("read_file", catalog.Tools[0].Definition.Name);
    }

    /// <summary>Two tools answering to one name is a programming error, not a silent win.</summary>
    [Fact]
    public void Catalog_RejectsADuplicateName()
    {
        var catalog = new ToolCatalog().Add(new StubTool("grep"));

        var error = Assert.Throws<ArgumentException>(() => FileToolset.RegisterInto(catalog));

        Assert.Contains("'grep' is already registered", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A stub standing in for the command half of the tool set.</summary>
    private sealed class StubTool(string name) : IAgentTool
    {
        /// <inheritdoc />
        public ToolDefinition Definition { get; } =
            new(name, "stub", new JsonObject { ["type"] = "object" });

        /// <inheritdoc />
        public Task<ToolResult> InvokeAsync(
            JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok("stub"));
    }
}
