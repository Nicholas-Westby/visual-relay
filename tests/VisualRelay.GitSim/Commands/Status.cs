using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>status --porcelain</c> (± a trailing pathspec). Emits porcelain-v1 <c>XY path</c>
    /// lines for staged/worktree changes and <c>?? path</c> for untracked-not-ignored
    /// files. Empty output means a clean tree — the one contract consumers rely on.
    /// </summary>
    public static GitSimResult Status(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var store = wt.Repo.Objects;
        var index = ctx.Index(wt);
        var head = HeadSnapshot(wt).ByPath;
        var idx = IndexSnapshot(index).ByPath;
        var pathspecs = ctx.Pathspecs();
        var lines = new List<(string Path, string Line)>();

        foreach (var path in head.Keys.Concat(idx.Keys).Distinct(StringComparer.Ordinal))
        {
            if (!MatchesAny(path, pathspecs) && pathspecs.Count > 0)
                continue;

            var inHead = head.TryGetValue(path, out var headSha);
            var inIndex = idx.TryGetValue(path, out var indexSha);
            var onDisk = WorkingTree.FileExists(wt.Root, path)
                ? WorkingTree.StageBlob(store, wt.Root, path)
                : null;

            var x = !inHead && inIndex ? 'A'
                : inHead && !inIndex ? 'D'
                : inHead && inIndex && headSha != indexSha ? 'M'
                : ' ';
            var y = !inIndex ? ' '
                : onDisk is null ? 'D'
                : onDisk != indexSha ? 'M'
                : ' ';

            if (x != ' ' || y != ' ')
                lines.Add((path, $"{x}{y} {path}"));
        }

        var ignore = GitIgnore.Load(wt.Root);
        foreach (var rel in WorkingTree.EnumerateFiles(wt.Root))
        {
            if (idx.ContainsKey(rel) || head.ContainsKey(rel) || ignore.IsIgnored(rel))
                continue;
            if (pathspecs.Count > 0 && !MatchesAny(rel, pathspecs))
                continue;
            lines.Add((rel, $"?? {rel}"));
        }

        if (lines.Count == 0)
            return GitSimResult.Ok();
        var ordered = lines.OrderBy(l => l.Path, StringComparer.Ordinal).Select(l => l.Line);
        return GitSimResult.Ok(string.Join('\n', ordered) + "\n");
    }
}
