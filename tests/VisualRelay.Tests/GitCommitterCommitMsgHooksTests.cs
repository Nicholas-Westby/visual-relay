using VisualRelay.Core.Execution;
using VisualRelay.GitSim;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

public sealed class GitCommitterCommitMsgHooksTests
{
    [Fact]
    public async Task CommitAsync_SetsRelayCommitTokenOnEveryAttempt()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        Write(repo, "src/app.cs", "updated");

        // Model both hooks as one PreCommitHook: the real pre-commit hook
        // requires RELAY_COMMIT_TOKEN to match the active-lock nonce, and the
        // commit-msg hook rejects the first candidate's subject. This proves
        // the token is set on both attempts.
        var nonce = Guid.NewGuid().ToString("N");
        GitCommitterTestHelpers.WriteActiveInfo(repo.Root, nonce);
        sim.PreCommitHook = req =>
        {
            if (req.Message.Split('\n')[0].Contains(".cs", StringComparison.Ordinal))
                return GitSimHookVerdict.Reject("hook: subject matches rejected pattern");
            return req.Environment.TryGetValue("RELAY_COMMIT_TOKEN", out var token) && token == nonce
                ? GitSimHookVerdict.Accept
                : GitSimHookVerdict.Reject("Visual Relay: commit rejected — a run is active.");
        };

        var candidates = new[] { "fix(src): update app.cs", "fix: correct update logic" };
        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            candidates,
            ["src/app.cs"],
            [],
            commitToken: nonce,
            preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success,
            "the second candidate should land; if it didn't, the token was missing on retry");
        Assert.Equal("fix: correct update logic", Subject(sim, repo, sim.Head(repo.Root)!));
    }

    [Fact]
    public async Task CommitAsync_SetsRelayNonce_SoOriginalRelayGuardAccepts()
    {
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        Write(repo, "src/app.cs", "updated");

        // Mimic the original Relay's commit-authority guard (e.g. JobFinder's
        // .relay/hooks/pre-commit.ts): it rejects the commit unless the env var
        // RELAY_NONCE equals the active-lock nonce. Visual Relay must set RELAY_NONCE
        // (not only its own RELAY_COMMIT_TOKEN) or it can never land a sealed commit
        // in such a repo.
        var nonce = Guid.NewGuid().ToString("N");
        GitCommitterTestHelpers.WriteActiveInfo(repo.Root, nonce);
        sim.PreCommitHook = req =>
            req.Environment.TryGetValue("RELAY_NONCE", out var n) && n == nonce
                ? GitSimHookVerdict.Accept
                : GitSimHookVerdict.Reject("guard: RELAY_NONCE does not match active lock nonce");

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            ["src/app.cs"],
            [],
            commitToken: nonce,
            preRunUntracked: null,
            tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success,
            "commit must pass a RELAY_NONCE-checking guard; if it didn't, GitCommitter isn't setting RELAY_NONCE");
    }
}
