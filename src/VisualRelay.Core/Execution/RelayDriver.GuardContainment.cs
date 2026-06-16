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
    /// Resolves the final symlink target of <paramref name="resolvedPath"/> and
    /// re-checks containment under <paramref name="guardRoot"/>.
    /// Returns <c>true</c> if the path is not a symlink (lexical check already
    /// passed) or if the resolved target is also inside the guards directory.
    /// </summary>
    internal static bool IsSymlinkTargetContained(string resolvedPath, string guardRoot)
    {
        // If the path is not a symlink, the lexical check already passed.
        var linkTarget = File.ResolveLinkTarget(resolvedPath, returnFinalTarget: true);
        if (linkTarget is null)
            return true;

        // The resolved target escapes the guards directory — reject.
        if (!IsPathWithinGuardRoot(linkTarget.FullName, guardRoot))
            return false;

        return true;
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
            {
                if (!IsSymlinkTargetContained(resolved, guardRoot))
                {
                    await _dependencies.EventSink.PublishAsync(new RelayEvent(
                        DateTimeOffset.UtcNow, "warn", "guard_symlink_escaped",
                        "", rootPath, "", 9,
                        Data: new Dictionary<string, string>
                        { ["entry"] = entry, ["resolved"] = resolved, ["guardRoot"] = guardRoot }), ct);
                    continue;
                }
                candidates.Add(resolved);
            }
        }
        return candidates;
    }
}
