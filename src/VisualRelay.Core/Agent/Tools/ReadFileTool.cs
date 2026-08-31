using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>read_file</c>: returns the exact text of one file, or a line window of it.
///
/// The text is returned verbatim — no line numbers — so a span the model reads
/// here can be pasted straight into <c>edit_file</c> as <c>old_string</c> and
/// still match. Line numbers live in the header instead, and in
/// <c>grep</c>/<c>outline</c> output, which is where navigation belongs.
/// </summary>
public sealed class ReadFileTool : IAgentTool
{
    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "read_file",
        "Read a text file from the repository. Returns the file's exact text (no line numbers), "
        + $"capped at {ToolLimits.ReadFileLines} lines or {ToolLimits.ReadFileChars / 1024} KiB per call — "
        + "page through a longer file with 'offset'. Use view_image for images, outline for a "
        + "structure-only summary, and grep to find the right file first.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text("Path to the file, relative to the repository root."),
                ["offset"] = ToolSchema.Integer(
                    "1-based line to start at. Defaults to 1; use the value the truncation "
                    + "marker suggests to continue reading."),
                ["limit"] = ToolSchema.Integer(
                    $"Maximum lines to return. Defaults to (and is capped at) {ToolLimits.ReadFileLines}."),
            },
            "path"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path"), out var absolute, out var error))
            return ToolResult.Error(error);

        var missing = ToolFiles.DescribeMissing(context, absolute, "read_file");
        if (missing is not null) return ToolResult.Error(missing);

        var offset = Math.Max(1, ToolArguments.Int(arguments, "offset", 1));
        var limit = ToolArguments.Int(arguments, "limit", ToolLimits.ReadFileLines);
        limit = limit <= 0 ? ToolLimits.ReadFileLines : Math.Min(limit, ToolLimits.ReadFileLines);

        try
        {
            return await ReadAsync(context, absolute, offset, limit, cancellationToken);
        }
        catch (IOException ex)
        {
            return ToolResult.Error(
                $"read_file: could not read \"{ToolPaths.Display(context, absolute)}\": {ex.Message}. "
                + "Check the path with list_files, or read a different file.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(
                $"read_file: \"{ToolPaths.Display(context, absolute)}\" is not readable "
                + "(permission denied). Read a different file.");
        }
    }

    private static async Task<ToolResult> ReadAsync(
        ToolContext context, string absolute, int offset, int limit, CancellationToken cancellationToken)
    {
        var shown = ToolPaths.Display(context, absolute);
        var size = new FileInfo(absolute).Length;
        if (size == 0) return ToolResult.Ok($"{shown} — empty file (0 bytes).\n");
        if (size > ToolLimits.ReadFileBytes)
            return ToolResult.Error(
                $"read_file: \"{shown}\" is {ToolText.Bytes(size)}, over the "
                + $"{ToolLimits.ReadFileBytes / (1024 * 1024)} MiB ceiling for a text read — it is a "
                + "bundle, a log or generated output. Use grep to pull out the lines you need.");

        var body = new StringBuilder();
        var total = 0;
        var emitted = 0;
        var lastEmitted = 0;
        var clipped = false;

        using var reader = new StreamReader(absolute, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            total++;
            if (total <= 8 && ToolFiles.LooksBinary(line))
                return ToolResult.Error(
                    $"read_file: \"{shown}\" is a binary file ({ToolText.Bytes(size)}) and cannot be "
                    + "read as text. Use view_image if it is an image; otherwise leave it alone.");

            if (total < offset || emitted >= limit || clipped) continue;

            if (body.Length + line.Length + 1 > ToolLimits.ReadFileChars)
            {
                clipped = true;
                continue;
            }

            body.Append(line).Append('\n');
            emitted++;
            lastEmitted = total;
        }

        return ToolResult.Ok(Render(shown, size, total, offset, emitted, lastEmitted, clipped, body));
    }

    private static string Render(
        string shown, long size, int total, int offset, int emitted, int lastEmitted, bool clipped, StringBuilder body)
    {
        if (offset > total)
            return $"{shown} — {ToolText.Count(total)} lines, {ToolText.Bytes(size)}. "
                + $"offset {ToolText.Count(offset)} is past the end of the file; "
                + "call read_file again with a smaller offset.\n";

        var window = emitted == total && offset == 1
            ? "whole file"
            : $"lines {ToolText.Count(offset)}-{ToolText.Count(lastEmitted)}";
        var header = $"{shown} — {ToolText.Count(total)} lines, {ToolText.Bytes(size)} ({window})\n";
        var text = header + body;

        if (lastEmitted >= total && !clipped) return text;

        var reason = clipped
            ? $"hit the {ToolLimits.ReadFileChars / 1024} KiB output cap at line {ToolText.Count(lastEmitted)} "
              + $"of {ToolText.Count(total)}"
            : $"showed {ToolText.Count(emitted)} of {ToolText.Count(total)} lines";
        return ToolText.Marker(
            text, "read_file", reason,
            $"Continue with offset={lastEmitted + 1}, or narrow with grep/outline instead of reading it all");
    }
}
