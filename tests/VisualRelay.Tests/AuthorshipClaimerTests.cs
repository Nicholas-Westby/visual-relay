using VisualRelay.Core.Authorship;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Integration tests for <see cref="AuthorshipClaimer"/> against a real
/// throwaway git repo (under the git-ignored <c>.relay-scratch/</c> tree). They
/// seed commits with a <strong>foreign author/committer</strong> ("Managed via
/// Tart") and a mix of Claude / human / plain trailers, run the claimer, and
/// assert the five spec invariants plus a second-run no-op.
/// </summary>
public sealed class AuthorshipClaimerTests
{
    private const string ClaimEmail = "w@minify.org";
    private const string ClaimName = "W";

    [Fact]
    public async Task ClaimAsync_ForeignAuthoredCommitsWithTrailers_ClaimsAndStrips()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Three commits authored/committed by a foreign identity, carrying a mix
        // of trailers. Distinct author dates so we can assert date preservation.
        await repo.SeedCommitAsync(git, "a.txt", "1",
            "feat: alpha\n\nCo-Authored-By: Claude Opus <noreply@anthropic.com>\n",
            "2021-01-01T10:00:00", "2021-06-01T10:00:00");
        await repo.SeedCommitAsync(git, "b.txt", "2",
            "fix: beta\n\nCo-Authored-By: Jane Doe <jane@example.com>\nClaude-Session: https://claude.ai/code/xyz\n",
            "2021-02-02T11:00:00", "2021-06-02T11:00:00");
        await repo.SeedCommitAsync(git, "c.txt", "3",
            "docs: gamma\n\nReviewed-by: Dev <dev@example.com>\n",
            "2021-03-03T12:00:00", "2021-06-03T12:00:00");

        var beforeDates = await repo.AuthorDatesAsync(git, 3);
        var claimer = new AuthorshipClaimer(git);

        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var rows = await repo.CommitMetaAsync(git, 3);

        // Invariant 1: every commit author email == committer email == claim.
        foreach (var row in rows)
        {
            Assert.Equal(ClaimEmail, row.AuthorEmail);
            Assert.Equal(ClaimEmail, row.CommitterEmail);
            Assert.Equal(ClaimName, row.AuthorName);
            Assert.Equal(ClaimName, row.CommitterName);
        }

        // Invariant 2: author dates unchanged (oldest->newest order preserved).
        var afterDates = await repo.AuthorDatesAsync(git, 3);
        Assert.Equal(beforeDates, afterDates);

        // Invariant 3: Claude trailers gone; human co-author + body + subjects kept.
        var bodies = string.Join("\n----\n", rows.Select(r => r.Body));
        Assert.DoesNotContain("Claude", bodies, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anthropic", bodies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Co-Authored-By: Jane Doe <jane@example.com>", bodies, StringComparison.Ordinal);
        Assert.Contains("Reviewed-by: Dev <dev@example.com>", bodies, StringComparison.Ordinal);
        Assert.Contains("feat: alpha", bodies, StringComparison.Ordinal);
        Assert.Contains("fix: beta", bodies, StringComparison.Ordinal);
        Assert.Contains("docs: gamma", bodies, StringComparison.Ordinal);

        // Invariant 5: second run is a no-op (HEAD sha unchanged).
        var headAfterFirst = await repo.HeadShaAsync(git);
        var outcome2 = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);
        Assert.True(outcome2.Success, outcome2.Error);
        var headAfterSecond = await repo.HeadShaAsync(git);
        Assert.Equal(headAfterFirst, headAfterSecond);
    }

    [Fact]
    public async Task ClaimAsync_AlreadyClaimedTrailerFreeRange_IsByteIdenticalNoOp()
    {
        // Invariant 4: a fully-claimed, Claude-trailer-free range is left
        // byte-identical (no ref move).
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git, authorName: ClaimName, authorEmail: ClaimEmail);

        await repo.SeedCommitAsync(git, "a.txt", "1",
            "feat: alpha\n", "2021-01-01T10:00:00", "2021-01-01T10:00:00",
            ClaimName, ClaimEmail, ClaimName, ClaimEmail);
        await repo.SeedCommitAsync(git, "b.txt", "2",
            "fix: beta\n\nReviewed-by: Dev <dev@example.com>\n", "2021-02-02T11:00:00", "2021-02-02T11:00:00",
            ClaimName, ClaimEmail, ClaimName, ClaimEmail);

        var headBefore = await repo.HeadShaAsync(git);
        var claimer = new AuthorshipClaimer(git);

        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        var headAfter = await repo.HeadShaAsync(git);
        Assert.Equal(headBefore, headAfter);
    }

    [Fact]
    public async Task ClaimAsync_DirtyWorkingTree_FailsClearly()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: alpha\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        // Dirty the tree.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a.txt"), "dirty");

        var claimer = new AuthorshipClaimer(git);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("working tree", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimAsync_MergeCommitInRange_FailsClearly()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: base\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        await repo.CreateMergeAsync(git);

        var claimer = new AuthorshipClaimer(git);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("merge", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimAsync_InvalidClaimEmail_FailsAsUsageError()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1", "feat: alpha\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");

        var claimer = new AuthorshipClaimer(git);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, "no-at-sign", null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.True(outcome.IsUsageError);
        Assert.Contains("@", outcome.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimAsync_FewerCommitsThanCount_FallsBackToRoot()
    {
        // HEAD~5 does not resolve on a 2-commit branch; the claimer must fall
        // back to the whole branch (root) rather than failing.
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        await repo.SeedCommitAsync(git, "a.txt", "1",
            "feat: alpha\n\nClaude-Session: https://claude.ai/code/abc\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        await repo.SeedCommitAsync(git, "b.txt", "2", "fix: beta\n",
            "2021-02-02T11:00:00", "2021-02-02T11:00:00");

        var claimer = new AuthorshipClaimer(git);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        var rows = await repo.CommitMetaAsync(git, 2);
        Assert.All(rows, r => Assert.Equal(ClaimEmail, r.AuthorEmail));
        Assert.DoesNotContain("Claude", string.Join("\n", rows.Select(r => r.Body)), StringComparison.OrdinalIgnoreCase);
    }
}
