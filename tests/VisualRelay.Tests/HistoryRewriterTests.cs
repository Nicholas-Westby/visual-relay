using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Integration tests for <see cref="HistoryRewriter"/> against a real throwaway
/// git repo (the <see cref="ScratchRepo"/> pattern). They seed commits with
/// non-conforming messages and varied authors/dates, export them, supply
/// rewritten messages, replay, and assert the spec invariants: messages now
/// validate clean, author identity + author dates preserved, branch ref moved,
/// working tree and index unchanged, a re-run is a no-op, and a merge commit
/// triggers a clear failure.
/// </summary>
public sealed class HistoryRewriterTests
{
    private static CommitLintContext FullCtx(IReadOnlyList<string> basenames) =>
        CommitLintContext.Human(basenames, []);

    [Fact]
    public async Task RewriteAsync_NonConformingHistory_RewritesAllAndPreservesIdentityAndDates()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Three non-conforming commits, distinct authors + author dates.
        await repo.SeedCommitAsync(git, "a.txt", "1",
            "Initial commit with a really long subject line that definitely exceeds the seventy-two character cap.",
            "2021-01-01T10:00:00", "2021-06-01T10:00:00",
            "Alice", "alice@example.test", "Alice", "alice@example.test");
        await repo.SeedCommitAsync(git, "b.txt", "2",
            "WIP — messy change\n\nthis is prose, not a bullet, and it mentions src/core/lock as a path",
            "2021-02-02T11:00:00", "2021-06-02T11:00:00",
            "Bob", "bob@example.test", "Bob", "bob@example.test");
        await repo.SeedCommitAsync(git, "c.txt", "3",
            "Fixed Stuff.",
            "2021-03-03T12:00:00", "2021-06-03T12:00:00",
            "Alice", "alice@example.test", "Alice", "alice@example.test");

        var beforeDates = await repo.AuthorDatesAsync(git, 3);
        var beforeTree = await TreeShaAsync(git, repo.Root);

        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, range: null, CancellationToken.None);
        Assert.True(export.Success, export.Error);
        Assert.Equal(3, export.Commits!.Count);

        // The "implementing agent" supplies a conforming rewrite per commit.
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [export.Commits[0].Sha] = "docs: add the initial project skeleton",
            [export.Commits[1].Sha] = "fix: tidy the lock handling\n\n- describe the change without naming paths",
            [export.Commits[2].Sha] = "fix: correct the broken behavior",
        };

        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);
        Assert.True(outcome.Rewrote);
        Assert.Equal(3, outcome.RewrittenCount);

        // Every rewritten message validates clean (full ruleset).
        var rows = await repo.CommitMetaAsync(git, 3);
        foreach (var row in rows)
        {
            var violations = CommitMessageValidator.Validate(row.Body, FullCtx([]));
            Assert.True(violations.Count == 0,
                $"message did not validate: {string.Join("; ", violations.Select(v => v.Message))}\n{row.Body}");
        }

        // Author identity + author dates preserved.
        Assert.Equal(beforeDates, await repo.AuthorDatesAsync(git, 3));
        Assert.Equal("alice@example.test", rows[0].AuthorEmail);
        Assert.Equal("Alice", rows[0].AuthorName);
        Assert.Equal("bob@example.test", rows[1].AuthorEmail);
        Assert.Equal("Bob", rows[1].AuthorName);

        // Tree (working set) unchanged: new tip tree == old tip tree.
        Assert.Equal(beforeTree, await TreeShaAsync(git, repo.Root));
    }

    [Fact]
    public async Task ReplayAsync_MovesBranchRef_AndLeavesIndexClean()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "Bad subject.",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        var oldHead = await repo.HeadShaAsync(git);
        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [export.Commits![0].Sha] = "chore: seed the repository",
        };

        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        // Branch ref moved (HEAD sha changed) but the index/working tree is clean.
        var newHead = await repo.HeadShaAsync(git);
        Assert.NotEqual(oldHead, newHead);
        var status = await git.RunAsync(repo.Root, ["status", "--porcelain"], CancellationToken.None);
        Assert.Equal(string.Empty, status.Output.Trim());
    }

    [Fact]
    public async Task RewriteAsync_AlreadyConforming_IsNoOp()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: add alpha\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        await repo.SeedCommitAsync(git, "b.txt", "2",
            "fix: correct the beta path\n\n- describe the change by behavior\n",
            "2021-02-02T11:00:00", "2021-02-02T11:00:00");

        var headBefore = await repo.HeadShaAsync(git);
        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);

        // Supply identical messages — nothing changes and every commit already
        // validates, so replay must be a byte-identical no-op (ref unmoved).
        var rewrites = export.Commits!.ToDictionary(c => c.Sha, c => c.Message.TrimEnd('\n'), StringComparer.Ordinal);
        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.False(outcome.Rewrote);
        Assert.Equal(headBefore, await repo.HeadShaAsync(git));
    }

    [Fact]
    public async Task ReplayAsync_RewrittenMessageStillInvalid_AbortsBeforeWriting()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "Bad subject.",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        var headBefore = await repo.HeadShaAsync(git);
        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);

        // A still-non-conforming rewrite (uppercase after prefix + trailing period).
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [export.Commits![0].Sha] = "feat: Still bad.",
        };

        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);
        // Nothing was written: HEAD is unmoved.
        Assert.Equal(headBefore, await repo.HeadShaAsync(git));
    }

    [Fact]
    public async Task ReplayAsync_MissingRewriteForACommit_FailsClearly()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "Bad one.",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        await repo.SeedCommitAsync(git, "b.txt", "2", "Bad two.",
            "2021-02-02T11:00:00", "2021-02-02T11:00:00");

        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [export.Commits![0].Sha] = "chore: one",
            // Second commit deliberately omitted.
        };

        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("missing", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportAsync_MergeCommitInRange_FailsClearly()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: base\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        await repo.CreateMergeAsync(git);

        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);
        Assert.False(export.Success);
        Assert.Contains("merge", export.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayAsync_DirtyWorkingTree_FailsClearly()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "Bad subject.",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [export.Commits![0].Sha] = "chore: seed",
        };

        // Dirty the tree after export, before replay.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a.txt"), "dirty");

        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("working tree", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayAsync_CreatesBackupRefBeforeMovingBranch()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "Bad subject.",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        var oldHead = await repo.HeadShaAsync(git);
        var rewriter = new HistoryRewriter(git);
        var export = await rewriter.ExportAsync(repo.Root, null, CancellationToken.None);
        var rewrites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [export.Commits![0].Sha] = "chore: seed",
        };

        var outcome = await rewriter.ReplayAsync(repo.Root, export, rewrites, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        // A backup tag points at the pre-rewrite tip.
        var backup = await git.RunAsync(repo.Root,
            ["rev-parse", "--verify", "--quiet", $"refs/tags/{HistoryRewriter.BackupTag}"],
            CancellationToken.None);
        Assert.Equal(0, backup.ExitCode);
        Assert.Equal(oldHead, backup.Output.Trim());
    }

    private static async Task<string> TreeShaAsync(IGitInvoker git, string root)
    {
        var (_, output, _) = await git.RunAsync(root, ["rev-parse", "HEAD^{tree}"], CancellationToken.None);
        return output.Trim();
    }
}
