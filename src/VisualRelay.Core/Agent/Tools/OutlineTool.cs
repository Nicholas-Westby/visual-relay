using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>outline</c>: the shape of a source file — its types and members with the
/// line each starts on — for a fraction of the tokens the whole file costs.
///
/// The summary is produced by <see cref="OutlineScanner"/>, a line-based scan
/// rather than a language parser, and the header says so: a heuristic the model
/// believes is exact would be worse than no outline at all.
/// </summary>
public sealed class OutlineTool : IAgentTool
{
    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "outline",
        "Summarise a source file's structure — declaration lines (types, members, functions, or "
        + "Markdown headings) with their line numbers — without reading the whole file. Use it to "
        + "decide which lines to read_file, on any language. The scan is heuristic and line-based, "
        + "so treat it as a map, not as a parse.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text("Path to the file, relative to the repository root."),
            },
            "path"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path"), out var absolute, out var error))
            return ToolResult.Error(error);

        var missing = ToolFiles.DescribeMissing(context, absolute, "outline");
        if (missing is not null) return ToolResult.Error(missing);

        var shown = ToolPaths.Display(context, absolute);
        try
        {
            var size = new FileInfo(absolute).Length;
            if (size > ToolLimits.ReadFileBytes)
                return ToolResult.Error(
                    $"outline: \"{shown}\" is {ToolText.Bytes(size)}, too large to scan. Use grep to "
                    + "find the lines you need instead.");

            var text = await File.ReadAllTextAsync(absolute, cancellationToken);
            if (ToolFiles.LooksBinary(text.Length > 4096 ? text[..4096] : text))
                return ToolResult.Error(
                    $"outline: \"{shown}\" is a binary file and has no source structure to summarise. "
                    + "Use view_image if it is an image.");

            var extension = Path.GetExtension(absolute).ToLowerInvariant();
            var entries = OutlineScanner.Scan(text, extension, ToolLimits.OutlineEntries, out var totalLines);
            return ToolResult.Ok(Render(shown, size, totalLines, entries));
        }
        catch (IOException ex)
        {
            return ToolResult.Error(
                $"outline: could not read \"{shown}\": {ex.Message}. Check the path with list_files.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error($"outline: \"{shown}\" is not readable (permission denied).");
        }
    }

    private static string Render(
        string shown, long size, int totalLines, List<(int Line, int Depth, string Text)> entries)
    {
        if (entries.Count == 0)
            return $"{shown} — {ToolText.Count(totalLines)} lines, {ToolText.Bytes(size)}: no "
                + "declaration-shaped lines found. It may be data, prose, or a language this scan "
                + "does not recognise — use read_file or grep instead.\n";

        var width = entries[^1].Line.ToString(CultureInfo.InvariantCulture).Length;
        var body = new StringBuilder(
            $"{shown} — outline ({ToolText.Count(totalLines)} lines, "
            + $"{ToolText.Count(entries.Count)} declarations; heuristic line scan)\n");

        foreach (var entry in entries)
            body.Append(entry.Line.ToString(CultureInfo.InvariantCulture).PadLeft(width))
                .Append(": ")
                .Append(new string(' ', entry.Depth * 2))
                .Append(entry.Text)
                .Append('\n');

        return entries.Count < ToolLimits.OutlineEntries
            ? body.ToString()
            : ToolText.Marker(
                body.ToString(), "outline",
                $"stopped at the first {ToolLimits.OutlineEntries} declarations",
                "Read the region you care about with read_file, or search it with grep");
    }
}
