using System.Text.Json;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    /// <summary>
    /// Runs <c>git check-ignore</c> on the manifest (stage 4) or amendManifest
    /// (stage 10) paths extracted from <paramref name="json"/>. Returns an error
    /// message naming any gitignored paths so the corrective-retry loop can tell
    /// the agent which runtime artifacts to remove. Returns null when all paths
    /// are clean or when check-ignore cannot run (non-git repo / git error).
    /// </summary>
    internal static async Task<string?> CheckManifestAgainstGitignoreAsync(
        string json, int stageNumber, string targetRoot, CancellationToken cancellationToken)
    {
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

        var args = new List<string> { "check-ignore", "--" };
        args.AddRange(paths);

        var result = await GitInvoker.RunAsync(targetRoot, args, cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        var ignored = result.Output.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (ignored.Length == 0)
            return null;

        var quoted = string.Join(", ", ignored.Select(p => $"`{p}`"));
        return ignored.Length == 1
            ? $"manifest rejected: {quoted} is a gitignored runtime artifact. Remove it from the manifest; only commit-tracked source files belong."
            : $"manifest rejected: {quoted} are gitignored runtime artifacts. Remove them from the manifest; only commit-tracked source files belong.";
    }
}
