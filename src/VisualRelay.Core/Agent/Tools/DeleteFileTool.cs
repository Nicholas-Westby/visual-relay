using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>delete_file</c>: removes one file. It never removes a directory — a
/// recursive delete driven by a model is a whole class of accident this tool set
/// does not need — and never touches <c>.git</c>.
/// </summary>
public sealed class DeleteFileTool : IAgentTool
{
    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "delete_file",
        "Delete a single file from the repository. Directories are not deleted; neither is "
        + "anything under .git. Deleting a file you did not create is rarely part of a task — "
        + "check with grep that nothing still references it first.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text("Path to the file to delete, relative to the repository root."),
            },
            "path"));

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Delete(arguments, context));
    }

    private static ToolResult Delete(JsonElement arguments, ToolContext context)
    {
        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path"), out var absolute, out var error))
            return ToolResult.Error(error);

        var refusal = ToolPaths.RejectIfVersionControl(context, absolute);
        if (refusal is not null) return ToolResult.Error(refusal);

        var shown = ToolPaths.Display(context, absolute);
        if (Directory.Exists(absolute))
            return ToolResult.Error(
                $"delete_file: \"{shown}\" is a directory. This tool deletes one file at a time; "
                + "list it with list_files and delete the files you actually mean to remove.");

        var missing = ToolFiles.DescribeMissing(context, absolute, "delete_file");
        if (missing is not null) return ToolResult.Error(missing);

        try
        {
            var size = new FileInfo(absolute).Length;
            File.Delete(absolute);
            return ToolResult.Ok($"delete_file: deleted \"{shown}\" ({ToolText.Bytes(size)}).\n");
        }
        catch (IOException ex)
        {
            return ToolResult.Error(
                $"delete_file: could not delete \"{shown}\": {ex.Message}. It may be open or "
                + "read-only; leave it in place and continue.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(
                $"delete_file: \"{shown}\" is not deletable (permission denied). Leave it in place.");
        }
    }
}
