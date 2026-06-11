using System.Diagnostics;

namespace VisualRelay.Tests;

public sealed class PreCommitHookTests
{
    private static string ProjectHookPath => Path.Combine(RepoSetup.Root, ".githooks", "pre-commit");

    [Fact]
    public void HookFile_ExistsAndHasMarker()
    {
        Assert.True(File.Exists(ProjectHookPath),
            $"pre-commit hook not found at {ProjectHookPath}");

        var content = File.ReadAllText(ProjectHookPath);
        Assert.Contains("# Visual Relay pre-commit hook", content);

        // Must be executable on Unix.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(ProjectHookPath);
            Assert.True((mode & UnixFileMode.UserExecute) != 0,
                "pre-commit hook is not user-executable");
        }
    }

    [Fact]
    public void NoOp_WhenNoActiveRun_AllowsCommit()
    {
        using var repo = CreateRepoWithHook();
        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "hello");
        RunGit(repo.Root, ["add", "test.txt"]);
        var (exitCode, stderr) = RunGitCapture(repo.Root, ["commit", "-m", "chore: test commit"]);
        Assert.True(exitCode == 0, $"commit should succeed without active run. stderr: {stderr}");
    }

    [Fact]
    public void Rejects_WhenActiveRunWithoutToken()
    {
        using var repo = CreateRepoWithHook();
        CreateActiveLock(repo.Root, "test-task", "abc123nonce456def789ghi012jkl345");
        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "hello");
        RunGit(repo.Root, ["add", "test.txt"]);
        var (exitCode, stderr) = RunGitCapture(repo.Root, ["commit", "-m", "chore: test commit"]);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("Visual Relay", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("active", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_WhenActiveRunWithWrongToken()
    {
        using var repo = CreateRepoWithHook();
        CreateActiveLock(repo.Root, "test-task", "abc123nonce456def789ghi012jkl345");
        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "hello");
        RunGit(repo.Root, ["add", "test.txt"]);
        var (exitCode, stderr) = RunGitCapture(repo.Root, ["commit", "-m", "chore: test commit"],
            ("RELAY_COMMIT_TOKEN", "wrong-token-value"));
        Assert.NotEqual(0, exitCode);
        Assert.Contains("Visual Relay", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_WhenActiveRunWithMatchingToken()
    {
        using var repo = CreateRepoWithHook();
        var nonce = "abc123nonce456def789ghi012jkl345";
        CreateActiveLock(repo.Root, "test-task", nonce);
        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "hello");
        RunGit(repo.Root, ["add", "test.txt"]);
        var (exitCode, _) = RunGitCapture(repo.Root, ["commit", "-m", "chore: test commit"],
            ("RELAY_COMMIT_TOKEN", nonce));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void NonceFromInfoJson_Is32CharHexString()
    {
        // The nonce is Guid.ToString("N") — 32 lowercase hex chars.
        // The hook must parse it correctly, stripping any JSON cruft.
        using var repo = CreateRepoWithHook();
        // Use a nonce that looks like a real Guid.ToString("N") output.
        var nonce = "10aacdc1a69b458784b6ddb3c2f9e5bd";
        Assert.Equal(32, nonce.Length);
        Assert.Matches("^[0-9a-f]{32}$", nonce);

        CreateActiveLock(repo.Root, "real-task", nonce);
        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "real");
        RunGit(repo.Root, ["add", "test.txt"]);
        var (exitCode, _) = RunGitCapture(repo.Root, ["commit", "-m", "chore: real commit"],
            ("RELAY_COMMIT_TOKEN", nonce));
        Assert.Equal(0, exitCode);
    }

    private static TestRepo CreateRepoWithHook()
    {
        var repo = TestRepository.Create();
        // Initialize a git repository.
        var initStartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        initStartInfo.ArgumentList.Add("-C");
        initStartInfo.ArgumentList.Add(repo.Root);
        initStartInfo.ArgumentList.Add("init");
        // Strip DEVELOPER_DIR/SDKROOT so xcrun shim cannot resurrect a stale
        // nix-store path inherited from the shell environment.
        initStartInfo.Environment.Remove("DEVELOPER_DIR");
        initStartInfo.Environment.Remove("SDKROOT");
        using var initProcess = Process.Start(initStartInfo)!;
        initProcess.WaitForExit();
        Assert.True(initProcess.ExitCode == 0, "git init failed");

        // Configure a dummy user for commits.
        RunGit(repo.Root, ["config", "user.email", "test@example.test"]);
        RunGit(repo.Root, ["config", "user.name", "Test User"]);

        // Install the project's pre-commit hook into this repo.
        RepoSetup.InstallPreCommitHook(repo.Root);

        return new TestRepo(repo);
    }

    private static void CreateActiveLock(string rootPath, string taskId, string nonce)
    {
        var activeDir = Path.Combine(rootPath, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        var info = $$"""{"task":"{{taskId}}","pid":99999,"nonce":"{{nonce}}"}""";
        File.WriteAllText(Path.Combine(activeDir, "info.json"), info);
    }

    private static void RunGit(string rootPath, string[] arguments)
    {
        var (exitCode, stderr) = RunGitCapture(rootPath, arguments);
        Assert.True(exitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    private static (int ExitCode, string Stderr) RunGitCapture(
        string rootPath,
        string[] arguments,
        params (string Key, string Value)[] environment)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(rootPath);
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Strip DEVELOPER_DIR/SDKROOT so xcrun shim cannot resurrect a stale
        // nix-store path inherited from the shell environment.
        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");

        foreach (var (key, value) in environment)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// Wraps TestRepository so the hook is cleaned up on disposal even if the
    /// hook file is read-only after chmod.
    /// </summary>
    private sealed class TestRepo : IDisposable
    {
        private readonly TestRepository _repo;

        public TestRepo(TestRepository repo) => _repo = repo;

        public string Root => _repo.Root;

        public void Dispose()
        {
            // Ensure hook file is writable before cleanup on Unix.
            var hookPath = Path.Combine(_repo.Root, ".git", "hooks", "pre-commit");
            if (File.Exists(hookPath) && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(hookPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    // Best-effort.
                }
            }

            _repo.Dispose();
        }
    }
}
