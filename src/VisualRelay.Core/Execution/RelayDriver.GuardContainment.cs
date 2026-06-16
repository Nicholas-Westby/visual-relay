using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>True when <paramref name="resolvedPath"/> is within <paramref name="guardRoot"/>.</summary>
    internal static bool IsPathWithinGuardRoot(string resolvedPath, string guardRoot)
    {
        var root = guardRoot.EndsWith(Path.DirectorySeparatorChar) ? guardRoot
            : guardRoot + Path.DirectorySeparatorChar;
        return resolvedPath.StartsWith(root, StringComparison.Ordinal) || resolvedPath == guardRoot;
    }

    /// <summary>
    /// Resolves manifest entries that match <paramref name="patterns"/> and pass
    /// path-containment under <c>tools/guards/</c>.  Entries that escape via
    /// <c>..</c> traversal are dropped with a <c>warn</c> event.
    /// </summary>
    private async Task<List<string>> ResolveGuardCandidatesAsync(
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> patterns,
        string rootPath,
        CancellationToken ct)
    {
        var guardRoot = Path.GetFullPath(Path.Combine(rootPath, "tools", "guards"));
        var candidates = new List<string>();
        foreach (var entry in manifest)
        {
            if (!patterns.Any(p => MatchesGuardGlob(entry, p)))
                continue;
            var resolved = Path.GetFullPath(Path.Combine(rootPath, entry));
            if (!IsPathWithinGuardRoot(resolved, guardRoot))
            {
                await _dependencies.EventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "warn", "guard_containment_blocked",
                    "", rootPath, "", 9,
                    Data: new Dictionary<string, string>
                    { ["entry"] = entry, ["resolved"] = resolved, ["guardRoot"] = guardRoot }), ct);
                continue;
            }
            if (File.Exists(resolved))
                candidates.Add(resolved);
        }
        return candidates;
    }
}
