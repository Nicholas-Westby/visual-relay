using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class GitCommitterCommitMsgHooksTests
{
    [Fact]
    public async Task CommitAsync_SetsRelayCommitTokenOnEveryAttempt()
    {
        using var repo = TestRepository.Create();
        await GitCommitterTestHelpers.InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await GitCommitterTestHelpers.StageAndCommitSeed(repo.Root, "chore: seed");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // Install both hooks: pre-commit requires the token, commit-msg rejects
        // the first candidate. This proves the token is set on both attempts.
        RepoSetup.InstallPreCommitHook(repo.Root);
        GitCommitterTestHelpers.InstallRejectingCommitMsgHook(repo.Root, "\\.cs");

        // Write the ACTIVE/info.json so the pre-commit hook demands the token.
        var nonce = Guid.NewGuid().ToString("N");
        GitCommitterTestHelpers.WriteActiveInfo(repo.Root, nonce);

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
            CancellationToken.None);

        Assert.True(result.Success,
            "the second candidate should land; if it didn't, the token was missing on retry");
        var subject = GitCommitterTestHelpers.RunGit(repo.Root, "log -1 --pretty=%s");
        Assert.Equal("fix: correct update logic", subject.Trim());
    }

    [Fact]
    public async Task CommitAsync_SetsRelayNonce_SoOriginalRelayGuardAccepts()
    {
        using var repo = TestRepository.Create();
        await GitCommitterTestHelpers.InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await GitCommitterTestHelpers.StageAndCommitSeed(repo.Root, "chore: seed");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // Mimic the original Relay's commit-authority guard (e.g. JobFinder's
        // .relay/hooks/pre-commit.ts): it rejects the commit unless the env var
        // RELAY_NONCE equals the active-lock nonce. Visual Relay must set RELAY_NONCE
        // (not only its own RELAY_COMMIT_TOKEN) or it can never land a sealed commit
        // in such a repo.
        var nonce = Guid.NewGuid().ToString("N");
        GitCommitterTestHelpers.WriteActiveInfo(repo.Root, nonce);
        GitCommitterTestHelpers.InstallRelayNonceGuardHook(repo.Root);

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
            CancellationToken.None);

        Assert.True(result.Success,
            "commit must pass a RELAY_NONCE-checking guard; if it didn't, GitCommitter isn't setting RELAY_NONCE");
    }
}
