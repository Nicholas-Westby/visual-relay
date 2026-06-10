using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

[Collection("GitCommitter")]
public sealed class GitCommitterTests
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

    [Fact]
    public async Task CommitAsync_SetsRelayNonce_SoOriginalRelayGuardAccepts()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // Mimic the original Relay's commit-authority guard (e.g. JobFinder's
        // .relay/hooks/pre-commit.ts): it rejects the commit unless the env var
        // RELAY_NONCE equals the active-lock nonce. Visual Relay must set RELAY_NONCE
        // (not only its own RELAY_COMMIT_TOKEN) or it can never land a sealed commit
        // in such a repo.
        var nonce = Guid.NewGuid().ToString("N");
        WriteActiveInfo(repo.Root, nonce);
        InstallRelayNonceGuardHook(repo.Root);

        var result = await GitCommitter.CommitAsync(
            repo.Root,
            "my-task",
            "abc123",
            ["feat: add widget"],
            ["src/app.cs"],
            [],
            commitToken: nonce,
            preRunUntracked: null,
            CancellationToken.None);

        Assert.True(result.Success,
            "commit must pass a RELAY_NONCE-checking guard; if it didn't, GitCommitter isn't setting RELAY_NONCE");
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

    /// <summary>
    /// Installs a pre-commit hook mimicking the original Relay's commit-authority
    /// guard: it rejects the commit unless the RELAY_NONCE env var matches the nonce
    /// in .relay/ACTIVE/info.json.
    /// </summary>
    private static void InstallRelayNonceGuardHook(string repoRoot)
    {
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "pre-commit");
        File.WriteAllText(hookPath,
            """
            #!/usr/bin/env bash
            set -euo pipefail
            active=".relay/ACTIVE/info.json"
            [ -f "$active" ] || exit 0
            nonce="$(grep -o '"nonce"[[:space:]]*:[[:space:]]*"[^"]*"' "$active" | sed 's/.*"nonce"[[:space:]]*:[[:space:]]*"//; s/".*//' | head -1)"
            [ -z "$nonce" ] && exit 0
            if [ "${RELAY_NONCE:-}" = "$nonce" ]; then exit 0; fi
            echo "guard: RELAY_NONCE does not match active lock nonce" >&2
            exit 1
            """);
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

    // ── test seam: transient git failure shim ─────────────────────────

    /// <summary>
    /// Implements the <see cref="GitCommitter.RawGitRunner"/> signature.
    /// Intercepts git calls whose argument list contains a configured substring
    /// and returns synthetic failures for a specified count before falling
    /// through to the real git process.
    /// </summary>
    private sealed class TransientGitShim
    {
        private readonly Dictionary<string, int> _failureCounts = new();
        private int _exitCode = 128;
        private string _stderr = "fatal: transient error";

        /// <summary>
        /// Configure the next <paramref name="failureCount"/> git invocations whose
        /// arguments contain <paramref name="argumentSubstring"/> to return a
        /// synthetic failure instead of calling real git.
        /// </summary>
        public void FailNext(string argumentSubstring, int failureCount, int exitCode = 128, string stderr = "fatal: transient error")
        {
            _failureCounts[argumentSubstring] = failureCount;
            _exitCode = exitCode;
            _stderr = stderr;
        }

        public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken ct,
            TimeSpan? timeout, IReadOnlyDictionary<string, string>? environment)
        {
            var argsList = arguments.ToList();
            var argsStr = string.Join(' ', argsList);
            foreach (var kvp in _failureCounts)
            {
                if (argsStr.Contains(kvp.Key, StringComparison.Ordinal) && kvp.Value > 0)
                {
                    _failureCounts[kvp.Key] = kvp.Value - 1;
                    return (_exitCode, _stderr, false);
                }
            }

            // Fall through to real git.
            var gitArgs = new List<string> { "-C", rootPath };
            gitArgs.AddRange(argsList);
            return await ProcessCapture.RunAsync("git", gitArgs, rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, environment);
        }
    }
}
