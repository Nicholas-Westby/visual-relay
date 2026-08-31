using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>read_multiple_files</c>: one round trip for a set of files that are only
/// useful together — an interface and its implementations, a view and its
/// view-model. Per-file failures are reported inline rather than failing the
/// whole call, because one bad path in a batch of ten should not cost the model
/// the other nine.
/// </summary>
public sealed class ReadMultipleFilesTool : IAgentTool
{
    private const string Separator = "───────────────────────────────────────────";

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "read_multiple_files",
        $"Read up to {ToolLimits.MultiFileCount} text files in one call. Each file is capped at "
        + $"{ToolLimits.MultiFilePerFileChars / 1024} KiB and the batch at "
        + $"{ToolLimits.MultiFileTotalChars / 1024} KiB, so use it for a set of files you need "
        + "together and read_file for one file you need in full. A path that fails is reported "
        + "beside the files that succeeded.",
        ToolSchema.Object(
            new JsonObject
            {
                ["paths"] = ToolSchema.TextArray(
                    "Paths relative to the repository root, most important first — later paths "
                    + "are the ones dropped if the batch hits its cap."),
            },
            "paths"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var paths = ToolArguments.StringArray(arguments, "paths");
        if (paths.Count == 0)
            return ToolResult.Error(
                "read_multiple_files: 'paths' was empty — pass an array of file paths relative to "
                + "the repository root, or call read_file for a single file.");

        var accepted = paths.Take(ToolLimits.MultiFileCount).ToList();
        var body = new StringBuilder();
        var failures = 0;
        var dropped = paths.Count - accepted.Count;

        for (var index = 0; index < accepted.Count; index++)
        {
            if (body.Length >= ToolLimits.MultiFileTotalChars)
            {
                dropped += accepted.Count - index;
                break;
            }

            var section = await SectionAsync(context, accepted[index], cancellationToken);
            if (section.Failed) failures++;
            body.Append(section.Text);
        }

        if (failures == accepted.Count)
            return ToolResult.Error(
                $"read_multiple_files: none of the {ToolText.Count(accepted.Count)} paths could be "
                + $"read.\n{body}Use list_files or grep to find the real paths.");

        var text = body.ToString();
        return ToolResult.Ok(dropped <= 0
            ? text
            : ToolText.Marker(
                text, "read_multiple_files", $"{ToolText.Count(dropped)} path(s) not read",
                $"The batch caps at {ToolLimits.MultiFileCount} files and "
                + $"{ToolLimits.MultiFileTotalChars / 1024} KiB; request the rest in another call"));
    }

    private static async Task<(string Text, bool Failed)> SectionAsync(
        ToolContext context, string path, CancellationToken cancellationToken)
    {
        if (!ToolPaths.TryResolve(context, path, out var absolute, out var error))
            return ($"{Separator}\n{path}\nERROR: {error}\n\n", true);

        var missing = ToolFiles.DescribeMissing(context, absolute, "read_multiple_files");
        var shown = ToolPaths.Display(context, absolute);
        if (missing is not null) return ($"{Separator}\n{shown}\nERROR: {missing}\n\n", true);

        try
        {
            // Bounded read: never materialise more than the per-file cap, whatever
            // the file's size — the batch is a survey, not a bulk load.
            var text = await HeadAsync(absolute, ToolLimits.MultiFilePerFileChars + 1, cancellationToken);
            if (ToolFiles.LooksBinary(text))
                return ($"{Separator}\n{shown}\nERROR: binary file — use view_image if it is an image.\n\n", true);

            var size = new FileInfo(absolute).Length;
            var header = $"{Separator}\n{shown} — {ToolText.Bytes(size)}\n";
            if (text.Length <= ToolLimits.MultiFilePerFileChars) return ($"{header}{text}\n", false);

            var kept = text[..ToolLimits.MultiFilePerFileChars];
            var marker = ToolText.Marker(
                $"{header}{kept}", "read_multiple_files",
                $"{shown} clipped at {ToolLimits.MultiFilePerFileChars / 1024} KiB of {ToolText.Bytes(size)}",
                $"Call read_file with path=\"{shown}\" to page through the rest");
            return (marker + "\n", false);
        }
        catch (IOException ex)
        {
            return ($"{Separator}\n{shown}\nERROR: could not be read: {ex.Message}\n\n", true);
        }
        catch (UnauthorizedAccessException)
        {
            return ($"{Separator}\n{shown}\nERROR: permission denied.\n\n", true);
        }
    }

    private static async Task<string> HeadAsync(string absolute, int maxChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(absolute, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(maxChars, 8192)];
        var text = new StringBuilder();

        while (text.Length < maxChars)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            text.Append(buffer, 0, Math.Min(read, maxChars - text.Length));
        }

        return text.ToString();
    }
}
