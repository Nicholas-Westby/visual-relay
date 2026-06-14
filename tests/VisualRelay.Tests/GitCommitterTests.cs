using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

[Collection("GitCommitter")]
public sealed partial class GitCommitterTests
{
    // ── Resilience: transient git failure tests (a–d) ───────────────────

    [Fact]
    public async Task CommitAsync_ProbeFailsTwiceThenSucceeds_CommitsSuccessfully()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        var shim = new TransientGitShim();
        shim.FailNext("rev-parse", failureCount: 2, exitCode: 128, stderr: "fatal: not a git repository");
        GitCommitter.RawGitRunner = shim.RunAsync;
        try
        {
            var result = await GitCommitter.CommitAsync(
                repo.Root, "my-task", "abc123",
                ["feat: add widget"], ["src/app.cs"], [],
                commitToken: null, preRunUntracked: null,
                tasksDir: null,
                CancellationToken.None);

            Assert.True(result.Success, $"Expected success, got: {result.Error}");
            Assert.False(string.IsNullOrWhiteSpace(result.CommitSha));
        }
        finally
        {
            GitCommitter.RawGitRunner = null;
        }
    }

    [Fact]
    public async Task CommitAsync_ProbeFailsPersistently_ReturnsFailureWithDiagnostics()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        var shim = new TransientGitShim();
        // 99 failures: effectively persistent for the 3-attempt retry window.
        shim.FailNext("rev-parse", failureCount: 99, exitCode: 128, stderr: "fatal: not a git repository");
        GitCommitter.RawGitRunner = shim.RunAsync;
        try
        {
            var result = await GitCommitter.CommitAsync(
                repo.Root, "my-task", "abc123",
                ["feat: add widget"], ["src/app.cs"], [],
                commitToken: null, preRunUntracked: null,
                tasksDir: null,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("git exit 128", result.Error, StringComparison.Ordinal);
            Assert.Contains("fatal: not a git repository", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            GitCommitter.RawGitRunner = null;
        }
    }

    [Fact]
    public async Task CommitAsync_AddFailsTransientlyThenSucceeds_CommitsSuccessfully()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        var shim = new TransientGitShim();
        shim.FailNext("add", failureCount: 1, exitCode: 128, stderr: "fatal: index file open failed");
        GitCommitter.RawGitRunner = shim.RunAsync;
        try
        {
            var result = await GitCommitter.CommitAsync(
                repo.Root, "my-task", "abc123",
                ["feat: add widget"], ["src/app.cs"], [],
                commitToken: null, preRunUntracked: null,
                tasksDir: null,
                CancellationToken.None);

            Assert.True(result.Success, $"Expected success after transient add failure, got: {result.Error}");
        }
        finally
        {
            GitCommitter.RawGitRunner = null;
        }
    }

    [Fact]
    public async Task CommitAsync_PersistentFailure_CompletesWithinReasonableTime()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var shim = new TransientGitShim();
        shim.FailNext("rev-parse", failureCount: 99, exitCode: 128, stderr: "fatal: not a git repository");
        GitCommitter.RawGitRunner = shim.RunAsync;
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await GitCommitter.CommitAsync(
                repo.Root, "my-task", "abc123",
                ["feat: test"], [], [],
                commitToken: null, preRunUntracked: null,
                tasksDir: null,
                CancellationToken.None);
            sw.Stop();

            Assert.False(result.Success);
            // 3 attempts with 250ms + 1s backoff = 1.25s max added latency.
            // Allow generous headroom for process spawn + OS scheduling.
            Assert.True(sw.Elapsed.TotalSeconds < 10,
                $"Persistent failure took {sw.Elapsed.TotalSeconds:F1}s, expected < 10s");
        }
        finally
        {
            GitCommitter.RawGitRunner = null;
        }
    }

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
                tasksDir: null,
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
                tasksDir: null,
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
                tasksDir: null,
                CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("commit rejected", result.Error);
    }

    // ── gitignore rejection at stage 11 backstop ─────────────────────

    [Fact]
    public async Task CommitAsync_WhenManifestContainsGitignoredPath_ReturnsExplicitPathNames()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), "swival.toml\n");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Runtime artifact that PrepareAsync regenerates — exists on disk
        // but is gitignored. The manifest must not claim it.
        File.WriteAllText(Path.Combine(repo.Root, "swival.toml"), "[runtime]\nkey = \"val\"");
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["swival.toml", "src/app.cs"], [],
            commitToken: null, preRunUntracked: null,
            tasksDir: null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        // Must name the offending path explicitly — not bury it in raw git output.
        Assert.Contains("manifest contains gitignored", result.Error, StringComparison.Ordinal);
        Assert.Contains("swival.toml", result.Error, StringComparison.Ordinal);
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

    private static string RunGit(string rootPath, string arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/sh", $"-c \"git -C '{rootPath}' {arguments}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Strip DEVELOPER_DIR/SDKROOT so xcrun shim cannot resurrect a stale
        // nix-store path inherited from the shell environment.
        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}
