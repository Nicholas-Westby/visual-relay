using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class GitCommitterTests
{
    [Fact]
    public async Task CommitAsync_FirstCandidateAccepted_CommitsAndReturnsSha()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Modify a file so there's something to commit.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        var candidates = new[] { "feat: add widget", "docs: update readme" };
        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            candidates,
            ["src/app.cs"],
            [],
            commitToken: null,
            preRunUntracked: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.CommitSha));
        var subject = RunGit(repo.Root, "log -1 --pretty=%s");
        Assert.Equal("feat: add widget", subject.Trim());
        var fullMessage = RunGit(repo.Root, "log -1 --pretty=%B");
        Assert.Contains("Task: my-task", fullMessage);
        Assert.Contains("Relay-Seal: abc123", fullMessage);
    }

    [Fact]
    public async Task CommitAsync_FirstCandidateRejectedByCommitMsgHook_UsesSecond()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Modify a file so there's something to commit.
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // Install a commit-msg hook that rejects subjects containing "*.cs" or ".cs"
        InstallRejectingCommitMsgHook(repo.Root, "\\.cs");

        // First candidate contains a file-name pattern, second avoids it.
        var candidates = new[] { "fix(src): update app.cs logic", "fix: correct update logic" };
        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            candidates,
            ["src/app.cs"],
            [],
            commitToken: null,
            preRunUntracked: null,
            CancellationToken.None);

        Assert.True(result.Success);
        var subject = RunGit(repo.Root, "log -1 --pretty=%s");
        Assert.Equal("fix: correct update logic", subject.Trim());
    }

    [Fact]
    public async Task CommitAsync_AllCandidatesRejected_ReturnsFailure()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // Install a commit-msg hook that rejects everything.
        InstallRejectAllCommitMsgHook(repo.Root);

        var candidates = new[] { "feat: first", "fix: second" };
        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            candidates,
            ["src/app.cs"],
            [],
            commitToken: null,
            preRunUntracked: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error);
    }

    [Fact]
    public async Task CommitAsync_SetsRelayCommitTokenOnEveryAttempt()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // Install both hooks: pre-commit requires the token, commit-msg rejects
        // the first candidate. This proves the token is set on both attempts.
        RepoSetup.InstallPreCommitHook(repo.Root);
        InstallRejectingCommitMsgHook(repo.Root, "\\.cs");

        // Write the ACTIVE/info.json so the pre-commit hook demands the token.
        var nonce = Guid.NewGuid().ToString("N");
        WriteActiveInfo(repo.Root, nonce);

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
            CancellationToken.None);

        Assert.True(result.Success,
            "the second candidate should land; if it didn't, the token was missing on retry");
        var subject = RunGit(repo.Root, "log -1 --pretty=%s");
        Assert.Equal("fix: correct update logic", subject.Trim());
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static async Task InitGitRepo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        RunGit(root, "init");
        RunGit(root, "config user.email test@example.test");
        RunGit(root, "config user.name \"Test\"");
    }

    private static async Task StageAndCommitSeed(string root, string message)
    {
        RunGit(root, "add .");
        RunGit(root, $"commit -m \"{message}\"");
    }

    /// <summary>Installs a commit-msg hook that rejects subjects matching <paramref name="rejectPattern"/>.</summary>
    private static void InstallRejectingCommitMsgHook(string repoRoot, string rejectPattern)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "commit-msg");
        File.WriteAllText(hookPath,
            $"#!/usr/bin/env bash{Environment.NewLine}" +
            $"set -euo pipefail{Environment.NewLine}" +
            $"subject=\"$(head -n 1 \"$1\")\"{Environment.NewLine}" +
            $"if echo \"$subject\" | grep -qE '{rejectPattern}'; then{Environment.NewLine}" +
            $"  echo \"hook: subject matches rejected pattern\" >&2{Environment.NewLine}" +
            $"  exit 1{Environment.NewLine}" +
            $"fi{Environment.NewLine}" +
            $"exit 0{Environment.NewLine}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Installs a commit-msg hook that rejects every commit.</summary>
    private static void InstallRejectAllCommitMsgHook(string repoRoot)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "commit-msg");
        File.WriteAllText(hookPath,
            $"#!/usr/bin/env bash{Environment.NewLine}" +
            $"echo \"hook: all commits rejected\" >&2{Environment.NewLine}" +
            $"exit 1{Environment.NewLine}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void WriteActiveInfo(string repoRoot, string nonce)
    {
        var activeDir = Path.Combine(repoRoot, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        File.WriteAllText(Path.Combine(activeDir, "info.json"),
            $"{{\"nonce\":\"{nonce}\"}}");
    }

    private static string RunGit(string rootPath, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", $"-lc \"git -C '{rootPath}' {arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
