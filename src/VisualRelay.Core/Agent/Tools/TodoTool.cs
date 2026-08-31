using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The model's task list for one stage. A call carrying <c>todos</c> replaces the
/// whole list — the model always sends the full picture, so a lost or reordered call
/// cannot leave a half-updated list — and a call with no arguments just reads it back.
/// State lives on the instance, so one instance belongs to one run.
/// </summary>
public sealed class TodoTool : IAgentTool
{
    /// <summary>The wire name the model calls.</summary>
    private const string ToolName = "todo";

    /// <summary>The argument carrying the replacement list.</summary>
    private const string TodosArgument = "todos";

    private static readonly string[] Statuses = ["pending", "in_progress", "done"];

    private readonly List<(string Task, string Status)> _items = [];
    private readonly object _gate = new();

    /// <inheritdoc />
    public ToolDefinition Definition => new(
        ToolName,
        "Keeps your task list for this stage. Send the WHOLE list every time — it replaces what "
        + "was there — with each entry's status set to pending, in_progress or done. Call it with "
        + "no arguments to read the list back.",
        BuildSchema());

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(TodosArgument, out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return Task.FromResult(ToolResult.Ok(Render()));
        }

        var (replacement, error) = Parse(element);
        if (replacement is null)
            return Task.FromResult(ToolResult.Error(error!));

        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(replacement);
        }

        return Task.FromResult(ToolResult.Ok(Render()));
    }

    private static (List<(string Task, string Status)>? Value, string? Error) Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return (null, $"\"{TodosArgument}\" must be an array of objects.");

        var parsed = new List<(string Task, string Status)>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return (null, $"every entry of \"{TodosArgument}\" must be an object.");

            var (task, taskError) = CommandToolArguments.GetString(item, "task");
            if (task is null)
                return (null, $"in \"{TodosArgument}\": {taskError}");

            var (status, statusError) = CommandToolArguments.GetString(item, "status");
            if (status is null)
                return (null, $"in \"{TodosArgument}\": {statusError}");

            if (!Statuses.Contains(status))
                return (null, $"\"status\" must be one of {string.Join(", ", Statuses)}; got \"{status}\".");

            parsed.Add((task, status));
        }

        return (parsed, null);
    }

    private string Render()
    {
        (string Task, string Status)[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _items];
        }

        if (snapshot.Length == 0)
            return "The todo list is empty.";

        var text = new StringBuilder();
        foreach (var (task, status) in snapshot)
        {
            var marker = status switch
            {
                "done" => "x",
                "in_progress" => "~",
                _ => " ",
            };
            text.AppendLine($"[{marker}] {task}");
        }

        var counts = Statuses.Select(s => $"{snapshot.Count(i => i.Status == s)} {s}");
        text.Append($"({string.Join(", ", counts)})");
        return text.ToString();
    }

    private static JsonNode BuildSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [TodosArgument] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "The complete list, replacing whatever is stored.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["task"] = new JsonObject { ["type"] = "string" },
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(Statuses[0], Statuses[1], Statuses[2]),
                        },
                    },
                    ["required"] = new JsonArray("task", "status"),
                },
            },
        },
    };
}
