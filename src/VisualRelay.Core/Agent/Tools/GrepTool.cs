using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// <c>grep</c>: regex search across the tree, returning <c>path:line: text</c>.
///
/// Two ceilings matter here. The match timeout stops a catastrophically
/// backtracking pattern from eating the stage's wall clock, and the result caps
/// stop a broad pattern from pouring the whole repository into a context window
/// that is never reclaimed. Both report themselves in the output.
/// </summary>
public sealed class GrepTool : IAgentTool
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        "grep",
        "Search file contents with a .NET regular expression and get back matching lines as "
        + $"path:line: text. Returns at most {ToolLimits.GrepMatches} matches; skips build output, "
        + ".git, binary files and anything over "
        + $"{ToolLimits.GrepFileBytes / (1024 * 1024)} MiB. Narrow with 'path' and 'glob' rather "
        + "than widening the pattern.",
        ToolSchema.Object(
            new JsonObject
            {
                ["pattern"] = ToolSchema.Text("The regular expression to search for."),
                ["path"] = ToolSchema.Text(
                    "Directory or file to search, relative to the repository root. Defaults to the root."),
                ["glob"] = ToolSchema.Text("Optional file-name glob to restrict the search, e.g. \"*.cs\"."),
                ["ignore_case"] = ToolSchema.Flag("Match case-insensitively. Defaults to false."),
            },
            "pattern"));

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var pattern = ToolArguments.String(arguments, "pattern");
        if (string.IsNullOrEmpty(pattern))
            return ToolResult.Error(
                "grep: 'pattern' is required — pass a regular expression, e.g. \"class RelayDriver\".");

        if (!ToolPaths.TryResolve(context, ToolArguments.String(arguments, "path") ?? ".", out var root, out var error))
            return ToolResult.Error(error);

        Regex regex;
        try
        {
            var options = ToolArguments.Bool(arguments, "ignore_case", false)
                ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                : RegexOptions.CultureInvariant;
            regex = new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(
                $"grep: \"{pattern}\" is not a valid regular expression ({ex.Message}). Escape the "
                + "regex metacharacters (\\ . ( ) [ ] * + ? | ^ $) you meant literally.");
        }

        var glob = ToolArguments.String(arguments, "glob");
        return await SearchAsync(context, root, regex, glob, cancellationToken);
    }

    private static async Task<ToolResult> SearchAsync(
        ToolContext context, string root, Regex regex, string? glob, CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        var matches = 0;
        var files = 0;
        var scanned = 0;
        var capped = false;
        var shown = ToolPaths.Display(context, root);
        var scope = File.Exists(root)
            ? new ToolPathScope(ParentOf(shown), Path.GetDirectoryName(root) ?? root)
            : new ToolPathScope(shown, root);

        foreach (var file in Candidates(root, glob))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned++ >= ToolLimits.GrepFilesScanned || capped) break;

            var hits = await MatchesAsync(scope, file, regex, body, matches, cancellationToken);
            if (hits.Matches > 0) files++;
            matches += hits.Matches;
            capped = hits.Capped;
        }

        var where = shown == "." ? "the repository" : $"\"{shown}\"";
        var filter = glob is { Length: > 0 } ? $" matching \"{glob}\"" : string.Empty;

        if (matches == 0)
            return ToolResult.Ok(
                $"grep: no matches for /{regex}/ in {where}{filter} ({ToolText.Count(scanned)} files "
                + "searched). Try a shorter or case-insensitive pattern, or list_files to check the path.\n");

        var header = $"grep: {ToolText.Count(matches)} match(es) in {ToolText.Count(files)} file(s) "
            + $"for /{regex}/ in {where}{filter}\n";
        var text = header + body;
        if (!capped && scanned < ToolLimits.GrepFilesScanned) return ToolResult.Ok(text);

        var what = capped
            ? $"hit the {ToolLimits.GrepMatches}-match / {ToolLimits.GrepChars / 1024} KiB output cap"
            : $"stopped after searching {ToolText.Count(scanned)} files";
        return ToolResult.Ok(ToolText.Marker(
            text, "grep", what,
            "Re-run with a narrower 'path', a 'glob' such as \"*.cs\", or a more specific pattern"));
    }

    private static string ParentOf(string shown)
    {
        var cut = shown.LastIndexOf('/');
        return cut <= 0 ? "." : shown[..cut];
    }

    private static async Task<(int Matches, bool Capped)> MatchesAsync(
        ToolPathScope scope, string file, Regex regex, StringBuilder body, int matchesSoFar,
        CancellationToken cancellationToken)
    {
        var shown = scope.Display(file);
        var found = 0;
        var line = 0;

        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } text)
            {
                line++;
                if (line <= 8 && ToolFiles.LooksBinary(text)) return (0, false);
                if (!regex.IsMatch(text)) continue;

                if (matchesSoFar + found >= ToolLimits.GrepMatches || body.Length >= ToolLimits.GrepChars)
                    return (found, true);

                body.Append(shown).Append(':').Append(line).Append(": ")
                    .Append(ToolText.ClipLine(text.TrimEnd(), ToolLimits.GrepLineChars)).Append('\n');
                found++;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            body.Append(shown).Append(": [pattern timed out on this file after ")
                .Append(MatchTimeout.TotalSeconds).Append("s — simplify the regex]\n");
        }
        catch (IOException)
        {
            // Unreadable file: skipped silently, the same way grep(1) would.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return (found, false);
    }

    private static IEnumerable<string> Candidates(string root, string? glob)
    {
        if (File.Exists(root))
        {
            if (Matches(root, glob)) yield return root;
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') || ToolPaths.SkippedDirectories.Contains(name, StringComparer.Ordinal))
                    continue;

                if (Directory.Exists(entry))
                {
                    // Never followed: a link out of the tree is not searchable content.
                    if (new DirectoryInfo(entry).LinkTarget is null) pending.Push(entry);
                    continue;
                }

                if (Matches(entry, glob)) yield return entry;
            }
        }
    }

    private static bool Matches(string file, string? glob)
    {
        if (glob is { Length: > 0 }
            && !FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(file), ignoreCase: true))
            return false;

        try
        {
            return new FileInfo(file).Length <= ToolLimits.GrepFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
