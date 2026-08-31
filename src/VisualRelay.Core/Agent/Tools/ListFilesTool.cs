using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>list_files</c>: what is actually on disk under a path.
///
/// The name is load-bearing — stage prompts tell the model to "verify the exact
/// path with list_files" — so the tool answers that question directly: pointing
/// it at a file reports that the file exists rather than erroring, and every
/// entry is printed the way the model should type it back.
/// </summary>
public sealed class ListFilesTool : IAgentTool
{
    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "list_files",
        "List files and directories under a path, to confirm what exists and how a path is really "
        + $"spelled. Descends 2 levels by default, skips {string.Join("/", ToolPaths.SkippedDirectories.Take(4))} "
        + $"and other build output, and returns at most {ToolLimits.ListEntries} entries — narrow with "
        + "'path', 'pattern' or 'depth' if that is not enough.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text(
                    "Directory (or file) to list, relative to the repository root. Defaults to the root."),
                ["depth"] = ToolSchema.Integer(
                    "How many directory levels to descend. 1 lists only the immediate children. Defaults to 2."),
                ["pattern"] = ToolSchema.Text(
                    "Optional glob matched against file names only, e.g. \"*.cs\" or \"Relay*\"."),
                ["include_hidden"] = ToolSchema.Flag(
                    "Include dot-files and dot-directories. Defaults to false."),
            }));

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(List(arguments, context));
    }

    private static ToolResult List(JsonElement arguments, ToolContext context)
    {
        var requested = ToolArguments.String(arguments, "path") ?? ".";
        if (!ToolPaths.TryResolve(context, requested, out var absolute, out var error))
            return ToolResult.Error(error);

        var shown = ToolPaths.Display(context, absolute);
        if (File.Exists(absolute))
            return ToolResult.Ok(
                $"list_files: \"{shown}\" exists and is a file ({ToolText.Bytes(new FileInfo(absolute).Length)}).\n");

        if (!Directory.Exists(absolute))
            return ToolResult.Error(
                $"list_files: no such directory \"{shown}\". List its parent to see what is really "
                + "there, or start from the repository root with path=\".\".");

        var depth = ToolArguments.Int(arguments, "depth", 2);
        depth = depth <= 0 ? 1 : Math.Min(depth, ToolLimits.ListMaxDepth);
        var pattern = ToolArguments.String(arguments, "pattern");
        var includeHidden = ToolArguments.Bool(arguments, "include_hidden", false);

        var entries = new List<string>();
        var total = 0;
        var visited = 0;
        Walk(new ToolPathScope(shown, absolute), absolute, depth, pattern, includeHidden, entries,
            ref total, ref visited);
        entries.Sort(StringComparer.Ordinal);

        return ToolResult.Ok(Render(shown, depth, pattern, entries, total, visited));
    }

    private static void Walk(
        ToolPathScope listing, string directory, int remainingDepth, string? pattern, bool includeHidden,
        List<string> entries, ref int total, ref int visited)
    {
        if (remainingDepth <= 0 || visited >= ToolLimits.ListWalkEntries) return;

        List<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        children.Sort(StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (visited++ >= ToolLimits.ListWalkEntries) return;

            var name = Path.GetFileName(child);
            if (!includeHidden && name.StartsWith('.')) continue;
            if (ToolPaths.SkippedDirectories.Contains(name, StringComparer.Ordinal)) continue;

            if (Directory.Exists(child))
            {
                // A symlinked directory is named but never entered: following it
                // would list files outside the root under an inside-looking path.
                var link = new DirectoryInfo(child).LinkTarget;
                Add(entries, listing.Display(child) + (link is null ? "/" : "/ → symlink (not followed)"), ref total);
                if (link is null)
                    Walk(listing, child, remainingDepth - 1, pattern, includeHidden, entries, ref total, ref visited);
                continue;
            }

            if (pattern is { Length: > 0 }
                && !FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) continue;

            Add(entries, listing.Display(child), ref total);
        }
    }

    private static void Add(List<string> entries, string entry, ref int total)
    {
        total++;
        if (entries.Count < ToolLimits.ListEntries) entries.Add(entry);
    }

    private static string Render(
        string shown, int depth, string? pattern, List<string> entries, int total, int visited)
    {
        var filter = pattern is { Length: > 0 } ? $", pattern \"{pattern}\"" : string.Empty;
        var body = new StringBuilder($"{shown} — {ToolText.Count(total)} entries (depth {depth}{filter})\n");
        foreach (var entry in entries) body.Append(entry).Append('\n');

        if (entries.Count == 0)
            body.Append(pattern is { Length: > 0 }
                ? "(nothing matched — try a wider pattern, or drop it to see every file)\n"
                : "(empty at this depth — try a larger depth)\n");

        if (total <= ToolLimits.ListEntries && visited < ToolLimits.ListWalkEntries) return body.ToString();

        var what = visited >= ToolLimits.ListWalkEntries
            ? $"stopped after visiting {ToolText.Count(visited)} entries"
            : $"showed {ToolText.Count(entries.Count)} of {ToolText.Count(total)} entries";
        return ToolText.Marker(
            body.ToString(), "list_files", what,
            "Narrow with a deeper 'path', a 'pattern' such as \"*.cs\", or depth=1");
    }
}
