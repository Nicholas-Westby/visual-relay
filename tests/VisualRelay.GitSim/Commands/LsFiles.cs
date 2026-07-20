using System.Text.RegularExpressions;
using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>ls-files</c>: <c>--others [--ignored] --exclude-standard [--directory]</c>
    /// (untracked, split on the ignore rules), <c>--deleted</c> (tracked-but-missing),
    /// <c>-u</c> (unmerged <c>mode sha stage\tpath</c>), and the tracked-listing forms
    /// (bare, a <c>*.cs</c> pattern, or <c>-- &lt;path&gt;</c>). <c>-z</c> switches to
    /// NUL separators.
    /// </summary>
    public static GitSimResult LsFiles(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository (or any of the parent directories): .git");

        var nul = ctx.Has("-z");
        var index = ctx.Index(wt);
        var tracked = index.Stage0.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);

        if (ctx.Has("-u"))
        {
            var lines = index.Unmerged.Select(e => $"{e.Entry.Mode} {e.Entry.Sha} {e.Entry.Stage}\t{e.Path}");
            return GitSimResult.Ok(JoinPaths(lines, nul));
        }

        // --stage / -s: show mode sha stage\tpath for tracked entries.
        var stage = ctx.Has("--stage");

        if (ctx.Has("--deleted"))
        {
            var deleted = tracked.Where(p => !WorkingTree.FileExists(wt.Root, p)).OrderBy(p => p, StringComparer.Ordinal);
            return GitSimResult.Ok(stage
                ? JoinPaths(deleted.Select(p => FormatStageEntry(index, p)), nul)
                : JoinPaths(deleted, nul));
        }

        if (ctx.Has("--others"))
            return GitSimResult.Ok(JoinPaths(Others(ctx, wt, tracked), nul));

        // Tracked listing: bare, pattern, or -- <paths>.
        var patterns = ctx.Pathspecs();
        if (patterns.Count == 0)
            patterns = ctx.Args.Skip(1).Where(a => !a.StartsWith('-') && a != "--" && a != "--stage").ToList();

        var listed = tracked
            .Where(p => patterns.Count == 0 || patterns.Any(pat => MatchesTracked(p, pat)))
            .OrderBy(p => p, StringComparer.Ordinal);
        return GitSimResult.Ok(stage
            ? JoinPaths(listed.Select(p => FormatStageEntry(index, p)), nul)
            : JoinPaths(listed, nul));
    }

    private static string FormatStageEntry(GitIndex index, string path)
    {
        // Try stage-0 first; fall back to first unmerged entry.
        if (index.TryGet(path, out var entry))
            return $"{entry.Mode} {entry.Sha} {entry.Stage}\t{path}";
        var unmerged = index.Unmerged.FirstOrDefault(e => e.Path == path);
        return $"{unmerged.Entry.Mode} {unmerged.Entry.Sha} {unmerged.Entry.Stage}\t{path}";
    }

    private static IEnumerable<string> Others(GitSimContext ctx, Worktree wt, HashSet<string> tracked)
    {
        var ignore = GitIgnore.Load(wt.Root);
        var wantIgnored = ctx.Has("--ignored");
        var pathspecs = ctx.Pathspecs();
        var others = WorkingTree.EnumerateFiles(wt.Root)
            .Where(p => !tracked.Contains(p))
            .Where(p => pathspecs.Count == 0 || MatchesAny(p, pathspecs))
            .Where(p => ignore.IsIgnored(p) == wantIgnored)
            .ToList();

        if (!wantIgnored || !ctx.Has("--directory"))
            return others.OrderBy(p => p, StringComparer.Ordinal);

        // --directory: collapse a fully-ignored subtree to its top ignored dir + "/".
        var collapsed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in others)
            collapsed.Add(TopIgnoredDir(ignore, file) is { } dir ? dir + "/" : file);
        return collapsed;
    }

    private static string? TopIgnoredDir(GitIgnore ignore, string file)
    {
        var segments = file.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var prefix = string.Join('/', segments.Take(i));
            if (ignore.IsIgnored(prefix))
                return prefix;
        }

        return null;
    }

    private static bool MatchesTracked(string path, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return MatchesAny(path, [pattern]);
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(path, regex);
    }
}
