using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Runs one program with its arguments, argv-form: no shell, so no globbing, pipes,
/// redirection or word splitting. Everything goes through
/// <see cref="SandboxedCommandExecutor"/>, so the sandbox and the in-process command
/// guard both apply with no opt-out.
/// </summary>
/// <param name="executor">The shared sandboxed-command path.</param>
public sealed class RunCommandTool(SandboxedCommandExecutor executor) : IAgentTool
{
    /// <summary>The wire name the model calls.</summary>
    private const string ToolName = "run_command";

    /// <summary>The argument carrying the program and its arguments.</summary>
    private const string CommandArgument = "command";

    /// <inheritdoc />
    public ToolDefinition Definition => new(
        ToolName,
        "Runs one program with its arguments, with no shell involved: no globbing, pipes, "
        + "redirection or word splitting. The command runs at the repository root inside the "
        + "sandbox. Pass timeout_seconds to say how long it may take — the value is honoured as "
        + "given, and the only ceiling is the time left in this stage; if it ever has to be "
        + "reduced the result tells you the number that was applied and why. Use "
        + "run_shell_command when you genuinely need shell syntax. Both are available in "
        + "every stage that can run commands, under exactly these two names.",
        BuildSchema());

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (argv, error) = CommandToolArguments.GetStringArray(arguments, CommandArgument);
        return argv is null
            ? Task.FromResult(ToolResult.Error(error!))
            : executor.RunArgvAsync(ToolName, argv, arguments, context, cancellationToken);
    }

    private static JsonNode BuildSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [CommandArgument] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "The program followed by each of its arguments, one entry each.",
            },
            [CommandTimeoutBudget.ArgumentName] = new JsonObject
            {
                ["type"] = "number",
                ["description"] =
                    "Seconds to allow. Honoured exactly as given; the only ceiling is the time left "
                    + $"in this stage. Omit it to get the {CommandTimeoutBudget.DefaultSeconds}s default.",
            },
        },
        ["required"] = new JsonArray(CommandArgument),
    };
}
