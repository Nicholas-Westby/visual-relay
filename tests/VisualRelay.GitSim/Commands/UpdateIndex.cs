using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

internal static partial class GitSimCommands
{
    /// <summary>
    /// <c>update-index --chmod=(+|-)x &lt;path&gt;</c>: sets or clears the
    /// executable bit on the index entry for the given path without touching
    /// the working tree file. Used by <c>FlaggedWorkStore</c> capture/restore
    /// to preserve executable mode across a round-trip.
    /// </summary>
    public static GitSimResult UpdateIndex(GitSimContext ctx)
    {
        if (!ctx.TryRepo(out var wt))
            return GitSimResult.Fatal("not a git repository");

        var chmodArg = ctx.Args.FirstOrDefault(a => a.StartsWith("--chmod=", StringComparison.Ordinal));
        if (chmodArg is null)
            return ctx.Unsupported();

        var chmod = chmodArg["--chmod=".Length..];

        var path = ctx.Args.LastOrDefault(a => !a.StartsWith('-'));
        if (path is null)
            return ctx.Unsupported();

        var index = ctx.Index(wt);
        if (!index.TryGet(path, out var entry))
            return GitSimResult.Fatal($"error: {path}: is not in the index");

        var mode = chmod switch
        {
            "+x" => (entry.Mode[0] == '1' ? "100755" : "120755"),
            "-x" => (entry.Mode[0] == '1' ? "100644" : "120644"),
            _ => entry.Mode
        };
        index.Set(path, new IndexEntry(mode, entry.Sha));
        return GitSimResult.Ok();
    }
}
