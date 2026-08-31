using VisualRelay.Core.Agent.Tools;
using static VisualRelay.Tests.CommandToolTestHarness;

namespace VisualRelay.Tests;

/// <summary>
/// The two tools in the command family that reach no process: <c>think</c>, a
/// scratchpad that keeps reasoning in the transcript, and <c>todo</c>, the stage's
/// task list. <c>todo</c> takes the WHOLE list every time, so a dropped or reordered
/// call cannot leave it half-updated.
/// </summary>
public sealed class ThinkAndTodoToolTests
{
    /// <summary>think records the thought and says plainly that nothing ran.</summary>
    [Fact]
    public async Task Think_RecordsTheThoughtAndRunsNothing()
    {
        IAgentTool tool = new ThinkTool();

        var result = await tool.InvokeAsync(
            Arguments("""{"thought":"the failing assert is in the shell branch"}"""),
            Context(600), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("Thought recorded", result.Content, StringComparison.Ordinal);
        Assert.Contains("nothing changed", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A think call with no thought is told which argument is missing.</summary>
    [Fact]
    public async Task Think_WithoutAThought_NamesTheMissingArgument()
    {
        IAgentTool tool = new ThinkTool();

        var result = await tool.InvokeAsync(Arguments("{}"), Context(600), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("\"thought\" is required", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A todo call replaces the whole list and renders it back with a per-status tally.</summary>
    [Fact]
    public async Task Todo_ReplacesTheWholeListAndRendersIt()
    {
        IAgentTool tool = new TodoTool();

        var result = await tool.InvokeAsync(
            Arguments("""
                {"todos":[{"task":"read the spec","status":"done"},
                          {"task":"write the failing test","status":"in_progress"},
                          {"task":"wire the guard","status":"pending"}]}
                """),
            Context(600), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("[x] read the spec", result.Content, StringComparison.Ordinal);
        Assert.Contains("[~] write the failing test", result.Content, StringComparison.Ordinal);
        Assert.Contains("[ ] wire the guard", result.Content, StringComparison.Ordinal);
        Assert.Contains("(1 pending, 1 in_progress, 1 done)", result.Content, StringComparison.Ordinal);
    }

    /// <summary>A later call replaces the list rather than appending to it, and a call with no arguments just reads it back.</summary>
    [Fact]
    public async Task Todo_WithNoArguments_ReadsBackTheStoredList()
    {
        IAgentTool tool = new TodoTool();
        var empty = await tool.InvokeAsync(Arguments("{}"), Context(600), CancellationToken.None);
        Assert.Equal("The todo list is empty.", empty.Content);

        await tool.InvokeAsync(
            Arguments("""{"todos":[{"task":"first","status":"pending"},{"task":"second","status":"pending"}]}"""),
            Context(600), CancellationToken.None);
        await tool.InvokeAsync(
            Arguments("""{"todos":[{"task":"only this one","status":"done"}]}"""),
            Context(600), CancellationToken.None);

        var readBack = await tool.InvokeAsync(Arguments("{}"), Context(600), CancellationToken.None);

        Assert.Contains("[x] only this one", readBack.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("first", readBack.Content, StringComparison.Ordinal);
    }

    /// <summary>An unknown status is rejected with the set of statuses that are accepted.</summary>
    [Fact]
    public async Task Todo_RejectsAnUnknownStatus()
    {
        IAgentTool tool = new TodoTool();

        var result = await tool.InvokeAsync(
            Arguments("""{"todos":[{"task":"x","status":"blocked"}]}"""), Context(600), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("pending, in_progress, done", result.Content, StringComparison.Ordinal);
        Assert.Contains("got \"blocked\"", result.Content, StringComparison.Ordinal);
    }

    /// <summary>Both tools advertise the wire name and the schema the model is given.</summary>
    [Fact]
    public void ThinkAndTodo_AdvertiseTheirWireNamesAndSchemas()
    {
        var think = new ThinkTool().Definition;
        Assert.Equal("think", think.Name);
        Assert.Contains("runs nothing", think.Description, StringComparison.Ordinal);
        Assert.Equal("string", think.ParametersSchema["properties"]!["thought"]!["type"]!.GetValue<string>());

        var todo = new TodoTool().Definition;
        Assert.Equal("todo", todo.Name);
        Assert.Contains("WHOLE list", todo.Description, StringComparison.Ordinal);
        Assert.Contains("in_progress", todo.ParametersSchema["properties"]!.ToJsonString(), StringComparison.Ordinal);
    }
}
