using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Runs a command line through the shell, so pipes, redirection, globbing and
/// <c>&amp;&amp;</c> chains all work. It shares the one sandboxed path with
/// <see cref="RunCommandTool"/>: the in-process command guard inspects the shell
/// string segment by segment, so a hook bypass hidden mid-chain is still stripped.
/// </summary>
/// <param name="executor">The shared sandboxed-command path.</param>
public sealed class RunShellCommandTool(SandboxedCommandExecutor executor) : IAgentTool
{
    /// <summary>The wire name the model calls.</summary>
    private const string ToolName = "run_shell_command";

    /// <summary>The argument carrying the shell command line.</summary>
    private const string CommandArgument = "command";

    /// <inheritdoc />
    public ToolDefinition Definition => new(
        ToolName,
        "Runs a command line through /bin/sh, so pipes, redirection, globbing and && chains work. "
        + "It starts at the repository root inside the sandbox; cd within the command to work "
        + "elsewhere in the tree. Pass timeout_seconds to say how long it may take — the value is "
        + "honoured as given, and the only ceiling is the time left in this stage; if it ever has "
        + "to be reduced the result tells you the number that was applied and why. Use "
        + "run_command instead to run one program from an argv array with no shell. Both are "
        + "available in every stage that can run commands, under exactly these two names.",
        BuildSchema());

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (command, error) = CommandToolArguments.GetString(arguments, CommandArgument);
        return command is null
            ? Task.FromResult(ToolResult.Error(error!))
            : executor.RunShellAsync(ToolName, command, arguments, context, cancellationToken);
    }

    private static JsonNode BuildSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [CommandArgument] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The shell command line to run.",
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
