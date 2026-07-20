using VisualRelay.Core.Authorship;
using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

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
        var (sim, repo) = GitSimTestHelpers.NewRepo();

        // Three commits authored/committed by a foreign identity, carrying a mix
        // of trailers. Distinct author dates so we can assert date preservation.
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "feat: alpha\n\nCo-Authored-By: Claude Opus <noreply@anthropic.com>\n",
            dates: (DateTimeOffset.Parse("2021-01-01T10:00:00"), DateTimeOffset.Parse("2021-06-01T10:00:00")));
        sim.Seed(repo.Root, "b.txt", "2");
        sim.Commit(repo.Root, "fix: beta\n\nCo-Authored-By: Jane Doe <jane@example.com>\nClaude-Session: https://claude.ai/code/xyz\n",
            dates: (DateTimeOffset.Parse("2021-02-02T11:00:00"), DateTimeOffset.Parse("2021-06-02T11:00:00")));
        sim.Seed(repo.Root, "c.txt", "3");
        sim.Commit(repo.Root, "docs: gamma\n\nReviewed-by: Dev <dev@example.com>\n",
            dates: (DateTimeOffset.Parse("2021-03-03T12:00:00"), DateTimeOffset.Parse("2021-06-03T12:00:00")));

        var beforeDates = await AuthorDatesAsync(sim, repo.Root, 3);
        var claimer = new AuthorshipClaimer(sim);

        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var rows = await CommitMetaAsync(sim, repo.Root, 3);

        // Invariant 1: every commit author email == committer email == claim.
        foreach (var row in rows)
        {
            Assert.Equal(ClaimEmail, row.AuthorEmail);
            Assert.Equal(ClaimEmail, row.CommitterEmail);
            Assert.Equal(ClaimName, row.AuthorName);
            Assert.Equal(ClaimName, row.CommitterName);
        }

        // Invariant 2: author dates unchanged (oldest->newest order preserved).
        var afterDates = await AuthorDatesAsync(sim, repo.Root, 3);
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
        var headAfterFirst = sim.Head(repo.Root)!;
        var outcome2 = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);
        Assert.True(outcome2.Success, outcome2.Error);
        var headAfterSecond = sim.Head(repo.Root)!;
        Assert.Equal(headAfterFirst, headAfterSecond);
    }

    [Fact]
    public async Task ClaimAsync_AlreadyClaimedTrailerFreeRange_IsByteIdenticalNoOp()
    {
        // Invariant 4: a fully-claimed, Claude-trailer-free range is left
        // byte-identical (no ref move).
        var (sim2, repo2) = GitSimTestHelpers.NewRepo();

        sim2.Seed(repo2.Root, "a.txt", "1");
        sim2.Commit(repo2.Root, "feat: alpha\n",
            author: (ClaimName, ClaimEmail),
            dates: (DateTimeOffset.Parse("2021-01-01T10:00:00"), DateTimeOffset.Parse("2021-01-01T10:00:00")));
        sim2.Seed(repo2.Root, "b.txt", "2");
        sim2.Commit(repo2.Root, "fix: beta\n\nReviewed-by: Dev <dev@example.com>\n",
            author: (ClaimName, ClaimEmail),
            dates: (DateTimeOffset.Parse("2021-02-02T11:00:00"), DateTimeOffset.Parse("2021-02-02T11:00:00")));

        var headBefore = sim2.Head(repo2.Root)!;
        var claimer2 = new AuthorshipClaimer(sim2);

        var outcome = await claimer2.ClaimAsync(repo2.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        var headAfter = sim2.Head(repo2.Root)!;
        Assert.Equal(headBefore, headAfter);
    }

    [Fact]
    public async Task ClaimAsync_DirtyWorkingTree_FailsClearly()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "feat: alpha\n");

        // Dirty the tree.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "a.txt"), "dirty");

        var claimer = new AuthorshipClaimer(sim);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("working tree", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimAsync_MergeCommitInRange_FailsClearly()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "feat: base\n");
        // Create a merge commit via sim commands.
        await CreateMergeAsync(sim, repo.Root);

        var claimer = new AuthorshipClaimer(sim);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("merge", outcome.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimAsync_InvalidClaimEmail_FailsAsUsageError()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "feat: alpha\n");

        var claimer = new AuthorshipClaimer(sim);
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
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        sim.Seed(repo.Root, "a.txt", "1");
        sim.Commit(repo.Root, "feat: alpha\n\nClaude-Session: https://claude.ai/code/abc\n");
        sim.Seed(repo.Root, "b.txt", "2");
        sim.Commit(repo.Root, "fix: beta\n");

        var claimer = new AuthorshipClaimer(sim);
        var outcome = await claimer.ClaimAsync(repo.Root, 5, ClaimEmail, ClaimName, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        var rows = await CommitMetaAsync(sim, repo.Root, 2);
        Assert.All(rows, r => Assert.Equal(ClaimEmail, r.AuthorEmail));
        Assert.DoesNotContain("Claude", string.Join("\n", rows.Select(r => r.Body)), StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers (replacing ScratchRepo methods) ────────────────────────

    /// <summary>Returns author dates (ISO) oldest-&gt;newest for the last <paramref name="count"/> commits.</summary>
    private static async Task<List<string>> AuthorDatesAsync(GitSimEngine sim, string root, int count)
    {
        var output = await sim.Git(root, "log", "--reverse", $"-{count}", "--format=%aI");
        return output.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Returns per-commit identity + full message, oldest-&gt;newest.</summary>
    private static async Task<List<CommitMeta>> CommitMetaAsync(GitSimEngine sim, string root, int count)
    {
        const string sep = "\u0001";
        const string recordSep = "\u0002";
        var output = await sim.Git(root, "log", "--reverse", $"-{count}",
            $"--format=%an{sep}%ae{sep}%cn{sep}%ce{sep}%B{recordSep}");

        var rows = new List<CommitMeta>();
        foreach (var record in output.Output.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = record.TrimStart('\n');
            if (trimmed.Length == 0)
                continue;
            var parts = trimmed.Split(sep);
            if (parts.Length < 5)
                continue;
            rows.Add(new CommitMeta(parts[0], parts[1], parts[2], parts[3], parts[4]));
        }

        return rows;
    }

    /// <summary>Creates a second branch, commits on it, and merges it into the current branch.</summary>
    private static async Task CreateMergeAsync(GitSimEngine sim, string root)
    {
        await sim.Git(root, "checkout", "-b", "side");
        sim.Seed(root, "side.txt", "side");
        await sim.Git(root, "commit", "-m", "feat: side");
        await sim.Git(root, "checkout", "main");
        sim.Seed(root, "main2.txt", "main2");
        await sim.Git(root, "commit", "-m", "feat: main2");
        await sim.Git(root, "merge", "--no-ff", "--no-edit", "side");
    }
}
