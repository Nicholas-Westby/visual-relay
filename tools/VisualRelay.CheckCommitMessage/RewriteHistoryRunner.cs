using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

namespace VisualRelay.CheckCommitMessage;

/// <summary>
/// Driver for <c>VisualRelay.CheckCommitMessage --rewrite-history
/// &lt;messages-dir&gt; [&lt;range&gt;]</c>. Exports every commit in the range
/// (oldest→newest) via <see cref="HistoryRewriter"/>, then for each commit reads
/// a conforming replacement from <c>&lt;messages-dir&gt;/&lt;full-sha&gt;.txt</c>
/// if present, falling back to the commit's original message verbatim when no
/// file exists (so already-conforming commits need no file). It then replays
/// through the in-process rewrite engine, which validates every message and
/// aborts before writing if any is still invalid.
///
/// Extracted from <c>Program.cs</c> so the dispatch entry point stays small and
/// the driver is unit-testable: <see cref="RunAsync"/> returns the process exit
/// code (0 success, 1 export/replay failure, 2 usage) and writes to the supplied
/// streams rather than the console statics.
/// </summary>
public static class RewriteHistoryRunner
{
    /// <summary>
    /// Runs the driver. <paramref name="args"/> is the argument slice that
    /// follows the <c>--rewrite-history</c> flag: <c>[messages-dir]</c> or
    /// <c>[messages-dir, range]</c>.
    /// </summary>
    /// <param name="args">Args after the mode flag.</param>
    /// <param name="startDir">Directory to resolve the repo root from.</param>
    /// <param name="git">Injected git invoker (never shells out directly).</param>
    /// <param name="stdout">Success / progress sink.</param>
    /// <param name="stderr">Error / usage sink.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, string startDir, IGitInvoker git,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            stderr.WriteLine(
                "usage: VisualRelay.CheckCommitMessage --rewrite-history <messages-dir> [<range>]");
            return 2;
        }

        var messagesDir = args[0];
        var range = args.Count > 1 ? args[1] : null;

        var repoRoot = await RepoRootAsync(git, startDir, ct);
        var rewriter = new HistoryRewriter(git);

        var export = await rewriter.ExportAsync(repoRoot, range, ct);
        if (!export.Success || export.Commits is null)
        {
            stderr.WriteLine($"rewrite-history: {export.Error}");
            return 1;
        }

        var rewrites = await BuildRewritesAsync(export.Commits, messagesDir, ct);

        var outcome = await rewriter.ReplayAsync(repoRoot, export, rewrites, ct);
        if (!outcome.Success)
        {
            stderr.WriteLine($"rewrite-history: {outcome.Error}");
            return 1;
        }

        stdout.WriteLine(outcome.Rewrote
            ? $"rewrite-history: rewrote {outcome.RewrittenCount} commit(s)"
            : "rewrite-history: no changes (already conforming)");
        return 0;
    }

    /// <summary>
    /// Builds the per-sha rewrite map the replay requires for EVERY commit: the
    /// file text from <c>&lt;dir&gt;/&lt;sha&gt;.txt</c> when it exists, else the
    /// commit's original message verbatim. A missing directory is fine — every
    /// commit then falls back to its original message.
    /// </summary>
    private static async Task<Dictionary<string, string>> BuildRewritesAsync(
        IReadOnlyList<RewriteCommit> commits, string messagesDir, CancellationToken ct)
    {
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var file = Path.Combine(messagesDir, $"{commit.Sha}.txt");
            rewrites[commit.Sha] = File.Exists(file)
                ? await File.ReadAllTextAsync(file, ct)
                : commit.Message;
        }

        return rewrites;
    }

    private static async Task<string> RepoRootAsync(IGitInvoker git, string startDir, CancellationToken ct)
    {
        var (exit, output, _) = await git.RunAsync(
            startDir, ["rev-parse", "--show-toplevel"], ct, TimeSpan.FromSeconds(30));
        return exit == 0 && output.Trim().Length > 0 ? output.Trim() : startDir;
    }
}
