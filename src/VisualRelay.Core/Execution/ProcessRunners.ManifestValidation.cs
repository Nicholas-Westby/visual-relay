using System.Text.Json;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    /// <summary>
    /// Validates manifest paths for existence and gitignore status. Runs an
    /// existence check first (paths that already exist must be on disk; entries
    /// prefixed with '+' signal new files and are exempt), then runs
    /// <c>git check-ignore</c> on the existing-file entries only. Returns an error
    /// message so the corrective-retry loop can tell the agent what to fix. Returns
    /// null when all paths are clean or when the check cannot run (non-git repo /
    /// git error / unparseable JSON).
    /// </summary>
    internal static async Task<string?> CheckManifestAgainstGitignoreAsync(
        string json, int stageNumber, string targetRoot, CancellationToken cancellationToken, IGitInvoker? gitInvoker = null)
    {
        var gi = gitInvoker ?? new GitInvoker();
        var key = stageNumber == 4 ? "manifest" : "amendManifest";
        List<string> paths;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            paths = new List<string>();
            foreach (var el in arr.EnumerateArray())
            {
                var p = el.GetString();
                if (!string.IsNullOrWhiteSpace(p))
                    paths.Add(p);
            }
        }
        catch
        {
            return null;
        }

        if (paths.Count == 0)
            return null;

        // Existence check: manifest entries for existing files must be present on disk.
        // Entries prefixed with '+' signal "new file to be created" and are exempt.
        var existingEntries = paths.Where(p => !p.StartsWith("+", StringComparison.Ordinal)).ToList();

        var missing = existingEntries
            .Where(p => !File.Exists(Path.Combine(targetRoot, p))
                     && !Directory.Exists(Path.Combine(targetRoot, p)))
            .ToList();

        if (missing.Count > 0)
        {
            var quoted = string.Join(", ", missing.Select(p => $"`{p}`"));
            return missing.Count == 1
                ? $"manifest rejected: {quoted} does not exist in the target repo. " +
                  "Verify the exact path with list_files or find before including it. " +
                  "If this is a NEW file to be created, prefix it with '+' (e.g. '+src/NewFile.cs')."
                : $"manifest rejected: {quoted} do not exist in the target repo. " +
                  "Verify the exact paths with list_files or find before including them. " +
                  "If any are NEW files to be created, prefix them with '+'.";
        }

        // Gitignore check: only for files that exist on disk (new entries aren't on
        // disk yet and can't be gitignored).
        if (existingEntries.Count == 0)
            return null;

        var args = new List<string> { "check-ignore", "--" };
        args.AddRange(existingEntries);

        var result = await gi.RunAsync(targetRoot, args, cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        var ignored = result.Output.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (ignored.Length == 0)
            return null;

        var quotedIgnored = string.Join(", ", ignored.Select(p => $"`{p}`"));
        return ignored.Length == 1
            ? $"manifest rejected: {quotedIgnored} is a gitignored runtime artifact. Remove it from the manifest; only commit-tracked source files belong."
            : $"manifest rejected: {quotedIgnored} are gitignored runtime artifacts. Remove them from the manifest; only commit-tracked source files belong.";
    }
}
