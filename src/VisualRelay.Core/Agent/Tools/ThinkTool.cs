using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// A scratchpad. It runs nothing, reads nothing and changes nothing: it exists so the
/// model can commit a plan or a piece of reasoning to the transcript before acting,
/// which is what keeps that reasoning available on later turns.
/// </summary>
public sealed class ThinkTool : IAgentTool
{
    /// <summary>The wire name the model calls.</summary>
    private const string ToolName = "think";

    /// <summary>The argument carrying the thought.</summary>
    private const string ThoughtArgument = "thought";

    /// <inheritdoc />
    public ToolDefinition Definition => new(
        ToolName,
        "Records a thought in the transcript. It runs nothing, reads nothing and changes nothing; "
        + "use it to work out a plan, weigh what you have found, or note what to check next.",
        BuildSchema());

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (thought, error) = CommandToolArguments.GetString(arguments, ThoughtArgument);
        return Task.FromResult(thought is null
            ? ToolResult.Error(error!)
            : ToolResult.Ok("Thought recorded. Nothing was run and nothing changed."));
    }

    private static JsonNode BuildSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [ThoughtArgument] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What you are thinking.",
            },
        },
        ["required"] = new JsonArray(ThoughtArgument),
    };
}
