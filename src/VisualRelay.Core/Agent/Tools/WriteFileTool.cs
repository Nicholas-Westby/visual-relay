using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>write_file</c>: creates a file or replaces one outright. Missing parent
/// directories are created, because a model that has to mkdir first will simply
/// forget. Paths under <c>.git</c> are refused: a corrupted index costs the whole
/// run and no task is served by editing one.
/// </summary>
public sealed class WriteFileTool : IAgentTool
{
    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "write_file",
        "Create a file, or replace an existing file's entire contents. Parent directories are "
        + "created as needed. Prefer edit_file when changing part of an existing file — write_file "
        + "discards everything that was there before.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text("Path to the file, relative to the repository root."),
                ["content"] = ToolSchema.Text("The file's complete new contents."),
            },
            "path", "content"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path"), out var absolute, out var error))
            return ToolResult.Error(error);

        var refusal = ToolPaths.RejectIfVersionControl(context, absolute);
        if (refusal is not null) return ToolResult.Error(refusal);

        var content = ToolArguments.String(arguments, "content");
        if (content is null)
            return ToolResult.Error(
                "write_file: 'content' is required and must be a string — pass the file's complete "
                + "new text (pass \"\" to write an empty file).");

        var shown = ToolPaths.Display(context, absolute);
        if (Directory.Exists(absolute))
            return ToolResult.Error(
                $"write_file: \"{shown}\" is an existing directory — pick a file path inside it.");

        try
        {
            var existed = File.Exists(absolute);
            var previous = existed ? new FileInfo(absolute).Length : 0;

            var parent = Path.GetDirectoryName(absolute);
            if (parent is { Length: > 0 }) Directory.CreateDirectory(parent);

            await File.WriteAllTextAsync(absolute, content, new UTF8Encoding(false), cancellationToken);

            var lines = content.Length == 0 ? 0 : content.AsSpan().Count('\n') + (content.EndsWith('\n') ? 0 : 1);
            var written = new FileInfo(absolute).Length;
            return ToolResult.Ok(existed
                ? $"write_file: replaced \"{shown}\" — {ToolText.Count(lines)} lines, "
                  + $"{ToolText.Bytes(written)} (was {ToolText.Bytes(previous)}).\n"
                : $"write_file: created \"{shown}\" — {ToolText.Count(lines)} lines, "
                  + $"{ToolText.Bytes(written)}.\n");
        }
        catch (IOException ex)
        {
            return ToolResult.Error(
                $"write_file: could not write \"{shown}\": {ex.Message}. Check the path with "
                + "list_files, or write to a different file.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(
                $"write_file: \"{shown}\" is not writable (permission denied). Write elsewhere.");
        }
    }
}
