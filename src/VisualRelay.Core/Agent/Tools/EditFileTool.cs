using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>edit_file</c>: exact-string replacement, and nothing cleverer.
///
/// The two loud failures are the point of the tool. If <c>old_string</c> is
/// absent, the model's picture of the file is stale and a fuzzy match would
/// silently edit the wrong thing. If it matches more than once, the model has
/// not said which one it means, and picking the first is how an edit lands in
/// the wrong method. Both return an error naming the count, so the model widens
/// its context or opts into <c>replace_all</c> deliberately.
/// </summary>
public sealed class EditFileTool : IAgentTool
{
    private const int ReportedMatches = 5;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "edit_file",
        "Replace an exact string in a file. 'old_string' must appear EXACTLY once (whitespace and "
        + "indentation included) unless replace_all is true — copy it verbatim from read_file "
        + "output, and include surrounding lines to make it unique. Fails without writing when the "
        + "string is missing or ambiguous.",
        ToolSchema.Object(
            new JsonObject
            {
                ["path"] = ToolSchema.Text("Path to the file, relative to the repository root."),
                ["old_string"] = ToolSchema.Text(
                    "The exact text to replace, including indentation and line breaks."),
                ["new_string"] = ToolSchema.Text("The text to put in its place (\"\" deletes it)."),
                ["replace_all"] = ToolSchema.Flag(
                    "Replace every occurrence instead of failing on an ambiguous match. "
                    + "Defaults to false."),
            },
            "path", "old_string", "new_string"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path"), out var absolute, out var error))
            return ToolResult.Error(error);

        var refusal = ToolPaths.RejectIfVersionControl(context, absolute);
        if (refusal is not null) return ToolResult.Error(refusal);

        var missing = ToolFiles.DescribeMissing(context, absolute, "edit_file");
        if (missing is not null) return ToolResult.Error(missing);

        var oldString = ToolArguments.String(arguments, "old_string");
        var newString = ToolArguments.String(arguments, "new_string");
        var shown = ToolPaths.Display(context, absolute);

        if (string.IsNullOrEmpty(oldString))
            return ToolResult.Error(
                "edit_file: 'old_string' is required and must not be empty — pass the exact text to "
                + $"replace, copied from read_file on \"{shown}\". To create or overwrite a whole "
                + "file, use write_file.");
        if (newString is null)
            return ToolResult.Error(
                "edit_file: 'new_string' is required — pass \"\" to delete the matched text.");
        if (string.Equals(oldString, newString, StringComparison.Ordinal))
            return ToolResult.Error(
                "edit_file: 'old_string' and 'new_string' are identical, so this edit would change "
                + "nothing. Pass the text you actually want in the file.");

        try
        {
            var size = new FileInfo(absolute).Length;
            if (size > ToolLimits.ReadFileBytes)
                return ToolResult.Error(
                    $"edit_file: \"{shown}\" is {ToolText.Bytes(size)}, too large to edit safely. "
                    + "Leave generated or bundled files alone and change their source instead.");

            var text = await File.ReadAllTextAsync(absolute, cancellationToken);
            var offsets = Offsets(text, oldString);
            var replaceAll = ToolArguments.Bool(arguments, "replace_all", false);

            if (offsets.Count == 0) return ToolResult.Error(NotFound(text, oldString, shown));
            if (offsets.Count > 1 && !replaceAll)
                return ToolResult.Error(Ambiguous(text, offsets, shown));

            var updated = replaceAll
                ? text.Replace(oldString, newString, StringComparison.Ordinal)
                : string.Concat(text.AsSpan(0, offsets[0]), newString, text.AsSpan(offsets[0] + oldString.Length));
            await File.WriteAllTextAsync(absolute, updated, new UTF8Encoding(false), cancellationToken);

            var where = string.Join(", ", offsets.Take(ReportedMatches).Select(o => LineOf(text, o)));
            return ToolResult.Ok(
                $"edit_file: replaced {ToolText.Count(offsets.Count)} occurrence(s) in \"{shown}\" "
                + $"(line {where}). File is now {ToolText.Bytes(new FileInfo(absolute).Length)}.\n");
        }
        catch (IOException ex)
        {
            return ToolResult.Error(
                $"edit_file: could not edit \"{shown}\": {ex.Message}. Re-read it and try again.");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error($"edit_file: \"{shown}\" is not writable (permission denied).");
        }
    }

    private static List<int> Offsets(string text, string needle)
    {
        var offsets = new List<int>();
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            offsets.Add(index);
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return offsets;
    }

    private static string NotFound(string text, string oldString, string shown)
    {
        var hint = Squash(text).Contains(Squash(oldString), StringComparison.Ordinal)
            ? " The file DOES contain that text apart from whitespace, so the indentation or line "
              + "breaks in 'old_string' are wrong — copy the span verbatim out of read_file output."
            : " Re-read the file (read_file, or grep for a distinctive fragment) and copy the "
              + "current text exactly; what you replaced it with may already be in place.";

        return $"edit_file: 'old_string' does not appear in \"{shown}\", so nothing was written.{hint}";
    }

    private static string Ambiguous(string text, List<int> offsets, string shown)
    {
        var lines = string.Join(", ", offsets.Take(ReportedMatches).Select(o => LineOf(text, o)));
        var more = offsets.Count > ReportedMatches ? ", …" : string.Empty;
        return $"edit_file: 'old_string' appears {ToolText.Count(offsets.Count)} times in \"{shown}\" "
            + $"(lines {lines}{more}), so the edit is ambiguous and nothing was written. Include more "
            + "surrounding lines so the match is unique, or pass replace_all=true if you really mean "
            + "every occurrence.";
    }

    private static int LineOf(string text, int offset) => text.AsSpan(0, offset).Count('\n') + 1;

    private static string Squash(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        return builder.ToString();
    }
}
