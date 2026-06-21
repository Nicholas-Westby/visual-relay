using System.Text;
using VisualRelay.CheckCommitMessage;
using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Integration tests for the <c>--rewrite-history</c> driver mode
/// (<see cref="RewriteHistoryRunner"/>) against a throwaway git repo. They seed
/// a mix of conforming and non-conforming commits, drop per-commit rewrite files
/// into a directory keyed by full sha, run the driver, and assert: the reported
/// outcome, that every message validates afterwards, that a conforming commit
/// with no rewrite file keeps its original message verbatim, that author dates
/// are preserved, and that the usage / missing-directory paths behave.
/// </summary>
public sealed class RewriteHistoryRunnerTests
{
    private static CommitLintContext FullCtx() => CommitLintContext.Human([], []);

    [Fact]
    public async Task RunAsync_RewritesNonConformingAndKeepsConformingMessages()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // c0: non-conforming. c1: already conforming (no file written for it).
        // c2: non-conforming. Distinct authors + author dates.
        await repo.SeedCommitAsync(git, "a.txt", "1", "Initial commit, very BAD subject.",
            "2021-01-01T10:00:00", "2021-06-01T10:00:00",
            "Alice", "alice@example.test", "Alice", "alice@example.test");
        const string conforming = "feat: add the beta module";
        await repo.SeedCommitAsync(git, "b.txt", "2", conforming,
            "2021-02-02T11:00:00", "2021-06-02T11:00:00",
            "Bob", "bob@example.test", "Bob", "bob@example.test");
        await repo.SeedCommitAsync(git, "c.txt", "3", "Fixed Stuff.",
            "2021-03-03T12:00:00", "2021-06-03T12:00:00",
            "Alice", "alice@example.test", "Alice", "alice@example.test");

        var beforeDates = await repo.AuthorDatesAsync(git, 3);

        // Export to learn the shas, then drop rewrite files for the two bad ones.
        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, range: null, CancellationToken.None);
        Assert.True(export.Success, export.Error);

        // Messages dir lives OUTSIDE the repo so writing files does not dirty
        // the working tree (the replay refuses to run on a dirty tree).
        using var msgs = new TempDir();
        await WriteMsgAsync(msgs.Path, export.Commits![0].Sha, "docs: add the initial project skeleton");
        // No file for export.Commits[1] — it already conforms.
        await WriteMsgAsync(msgs.Path, export.Commits[2].Sha, "fix: correct the broken behavior");

        var (exit, stdout, stderr) = await RunAsync(git, repo.Root, [msgs.Path]);

        Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}");
        // The first commit's message changed, so the whole chain is rebuilt (all
        // 3 commits get new shas) — RewrittenCount is the rebuilt-commit count,
        // not the count of changed messages. The conforming commit's *message* is
        // still preserved verbatim, which is asserted below.
        Assert.Contains("rewrote 3 commit", stdout, StringComparison.Ordinal);

        // Every message validates clean afterwards.
        var rows = await repo.CommitMetaAsync(git, 3);
        foreach (var row in rows)
        {
            var violations = CommitMessageValidator.Validate(row.Body, FullCtx());
            Assert.True(violations.Count == 0,
                $"message did not validate: {string.Join("; ", violations.Select(v => v.Message))}\n{row.Body}");
        }

        // The conforming commit (no file) kept its original message verbatim.
        Assert.Equal(conforming, rows[1].Body.TrimEnd('\n'));

        // Author identity + dates preserved.
        Assert.Equal(beforeDates, await repo.AuthorDatesAsync(git, 3));
        Assert.Equal("alice@example.test", rows[0].AuthorEmail);
        Assert.Equal("bob@example.test", rows[1].AuthorEmail);
    }

    [Fact]
    public async Task RunAsync_AllConformingNoFiles_ReportsNoChanges()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: add alpha",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        await repo.SeedCommitAsync(git, "b.txt", "2", "fix: correct the beta behavior",
            "2021-02-02T11:00:00", "2021-02-02T11:00:00");

        var headBefore = await repo.HeadShaAsync(git);

        // A directory that exists but holds no rewrite files: every commit falls
        // back to its (already conforming) original message → byte-identical no-op.
        using var msgs = new TempDir();

        var (exit, stdout, stderr) = await RunAsync(git, repo.Root, [msgs.Path]);

        Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}");
        Assert.Contains("no changes", stdout, StringComparison.Ordinal);
        Assert.Equal(headBefore, await repo.HeadShaAsync(git));
    }

    [Fact]
    public async Task RunAsync_NonexistentDirectory_TreatsAsNoRewriteFiles()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: already fine",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        var headBefore = await repo.HeadShaAsync(git);
        var missing = Path.Combine(repo.Root, "does-not-exist");

        var (exit, stdout, stderr) = await RunAsync(git, repo.Root, [missing]);

        // Must NOT crash: the lone conforming commit keeps its message → no-op.
        Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}");
        Assert.Contains("no changes", stdout, StringComparison.Ordinal);
        Assert.Equal(headBefore, await repo.HeadShaAsync(git));
    }

    [Fact]
    public async Task RunAsync_MissingDirectoryArgument_PrintsUsageAndExits2()
    {
        var git = new GitInvoker();
        var (exit, _, stderr) = await RunAsync(git, Directory.GetCurrentDirectory(), []);

        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ExportFailure_PrintsErrorAndExits1()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: only commit",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        // An empty range (HEAD..HEAD) exports no commits → export fails.
        using var msgs = new TempDir();

        var (exit, _, stderr) = await RunAsync(git, repo.Root, [msgs.Path, "HEAD..HEAD"]);

        Assert.Equal(1, exit);
        Assert.NotEqual(string.Empty, stderr.Trim());
    }

    private static async Task WriteMsgAsync(string dir, string sha, string message) =>
        await File.WriteAllTextAsync(Path.Combine(dir, $"{sha}.txt"), message, new UTF8Encoding(false));

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        IGitInvoker git, string startDir, IReadOnlyList<string> args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await RewriteHistoryRunner.RunAsync(args, startDir, git, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A throwaway directory OUTSIDE any repo, for the messages dir.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vr-rewrite-msgs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
