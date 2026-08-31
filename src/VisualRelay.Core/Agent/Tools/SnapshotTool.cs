using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Reports what the working tree looks like right now: the branch, the porcelain
/// status, and a diffstat of the staged and unstaged changes. It is how the model
/// checks what its own edits have actually amounted to before it verifies or reports.
/// <para>It is a command tool like any other — the same sandbox, the same in-process
/// command guard, the same honoured <c>timeout_seconds</c> — so no tool reaches a
/// process by a path the guard does not see.</para>
/// </summary>
/// <param name="executor">The shared sandboxed-command path.</param>
public sealed class SnapshotTool(SandboxedCommandExecutor executor) : IAgentTool
{
    /// <summary>The wire name the model calls.</summary>
    private const string ToolName = "snapshot";

    /// <summary>
    /// The fixed command line. Separated by <c>;</c> rather than <c>&amp;&amp;</c> so a
    /// repository with no commits yet still reports its status instead of stopping at
    /// the first failing git call.
    /// </summary>
    internal const string SnapshotCommand =
        "git status --porcelain=v1 --branch"
        + "; echo '--- staged ---'; git diff --cached --stat"
        + "; echo '--- unstaged ---'; git diff --stat";

    /// <inheritdoc />
    public ToolDefinition Definition => new(
        ToolName,
        "Shows the state of the working tree right now: current branch, the porcelain status, and "
        + "a diffstat of staged and unstaged changes. Takes no arguments. Use it to check what your "
        + "edits actually changed before you verify or report.",
        BuildSchema());

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        executor.RunShellAsync(ToolName, SnapshotCommand, arguments, context, cancellationToken);

    private static JsonNode BuildSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [CommandTimeoutBudget.ArgumentName] = new JsonObject
            {
                ["type"] = "number",
                ["description"] =
                    "Seconds to allow. Honoured exactly as given; the only ceiling is the time left "
                    + $"in this stage. Omit it to get the {CommandTimeoutBudget.DefaultSeconds}s default.",
            },
        },
    };
}
