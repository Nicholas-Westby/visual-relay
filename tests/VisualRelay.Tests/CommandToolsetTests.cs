using VisualRelay.Core.Agent.Tools;
using static VisualRelay.Tests.CommandToolTestHarness;

namespace VisualRelay.Tests;

/// <summary>
/// The command half registers itself into the shared <see cref="ToolCatalog"/>
/// additively, so it composes with the file and inspection half without either
/// side owning a hard-coded list of the whole tool set.
/// </summary>
public sealed class CommandToolsetTests
{
    /// <summary>The five command-family tools are created in the order the model reads them.</summary>
    [Fact]
    public void Create_BuildsTheFiveCommandFamilyTools()
    {
        var tools = CommandToolset.Create(Executor(new RecordingCommandLauncher()));

        Assert.Equal(
            new[] { "run_command", "run_shell_command", "snapshot", "think", "todo" },
            tools.Select(tool => tool.Definition.Name));
    }

    /// <summary>Registration is additive: the toolset adds to a catalog it does not own, claims exactly its own five names, and assumes nothing about the rest.</summary>
    [Fact]
    public void RegisterInto_AddsToACatalogItDoesNotOwn()
    {
        var catalog = new ToolCatalog();

        var returned = CommandToolset.RegisterInto(catalog, Executor(new RecordingCommandLauncher()));

        Assert.Same(catalog, returned);
        Assert.Equal(5, catalog.Tools.Count);
        Assert.NotNull(catalog.Find("run_shell_command"));
        Assert.NotNull(catalog.Find("snapshot"));
        Assert.Null(catalog.Find("read_file"));
        Assert.Throws<ArgumentException>(() => catalog.Add(new ThinkTool()));
    }

    /// <summary>Every registered command tool advertises a schema the request builder can hand to the model.</summary>
    [Fact]
    public void RegisteredTools_RenderDefinitionsForTheRequest()
    {
        var catalog = CommandToolset.RegisterInto(new ToolCatalog(), Executor(new RecordingCommandLauncher()));

        var definitions = catalog.Definitions();

        Assert.Equal(5, definitions.Count);
        Assert.Equal("run_command", definitions[0]["function"]!["name"]!.GetValue<string>());
        Assert.Equal("object", definitions[0]["function"]!["parameters"]!["type"]!.GetValue<string>());
    }
}
