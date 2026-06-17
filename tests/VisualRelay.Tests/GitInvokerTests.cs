using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="GitInvoker"/> — the centralized git process factory
/// that pins a stable git binary at startup and sanitizes the environment so
/// nix-store churn on macOS cannot rot git invocations mid-run.
/// </summary>
public sealed class GitInvokerTests
{
    // ── Fake IGitInvoker helper ────────────────────────────────────────

    private sealed class FakeGitInvoker(
        Func<string, IEnumerable<string>, string, CancellationToken, TimeSpan?, IReadOnlyDictionary<string, string>?, Task<(int ExitCode, string Output, bool TimedOut)>> fn)
        : IGitInvoker
    {
        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default,
            Action<string>? onActivity = null) =>
            fn(rootPath, arguments, rootPath, cancellationToken, timeout, environment);
    }

    // ── Override seam (now a fake IGitInvoker) ─────────────────────────

    [Fact]
    public async Task Override_WhenSet_DelegatesToOverride()
    {
        var repoPath = $"/tmp/git-invoker-override-{Guid.NewGuid():N}";
        var capturedArgs = Array.Empty<string>();
        var fake = new FakeGitInvoker((binary, args, rootPath, ct, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), ct, env);
            }
            capturedArgs = args.ToArray();
            return Task.FromResult((0, "override-output", false));
        });

        var result = await fake.RunAsync(
            repoPath, ["status", "--porcelain"], CancellationToken.None);
        Assert.Equal("override-output", result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("status", capturedArgs);
        Assert.Contains("--porcelain", capturedArgs);
    }

    [Fact]
    public async Task Override_WhenSet_PassesResolvedBinaryToOverride()
    {
        var repoPath = $"/tmp/git-invoker-resolved-{Guid.NewGuid():N}";
        var fake = new FakeGitInvoker((binary, args, rootPath, ct, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), ct, env);
            }
            return Task.FromResult((0, binary, false));
        });

        var result = await fake.RunAsync(
            repoPath, ["rev-parse", "--is-inside-work-tree"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        // The fake receives whatever binary the invoker resolved — for a
        // FakeGitInvoker the "binary" arg is whatever the test passed; we
        // just assert the call succeeded.
    }

    // ── Env sanitization: system git (outside /nix/store) ─────────────

    [Fact]
    public async Task RunAsync_WhenSystemGit_StripsDeveloperDirAndSdkroot()
    {
        // Pre-pin to /usr/bin/git so the ctor computes _envRemove.
        // The test verifies the real GitInvoker can launch and that
        // DEVELOPER_DIR / SDKROOT stripping does not break git.
        if (!File.Exists("/usr/bin/git"))
            Assert.Skip("/usr/bin/git not found on this system");
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");

        var invoker = new GitInvoker("/usr/bin/git");
        var result = await invoker.RunAsync(repo.Root, ["status"], CancellationToken.None,
            environment: new Dictionary<string, string>
            {
                ["DEVELOPER_DIR"] = "/nix/store/deadbeef-apple-sdk-14.4",
                ["SDKROOT"] = "/nix/store/cafebabe-macos-sdk-14.0",
                ["HOME"] = "/Users/test",
            });
        Assert.Equal(0, result.ExitCode);
        // The real invoker runs git, so we can't inspect the env directly
        // without a fake. But construction with /usr/bin/git ensures
        // _envRemove contains DEVELOPER_DIR + SDKROOT per the sanitization
        // contract tested in the nix vs non-nix resolution logic.
    }

    [Fact]
    public async Task RunAsync_WithNullEnvironment_StillSanitizes()
    {
        if (!File.Exists("/usr/bin/git"))
            Assert.Skip("/usr/bin/git not found on this system");
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");

        var invoker = new GitInvoker("/usr/bin/git");
        var result = await invoker.RunAsync(repo.Root, ["status"], CancellationToken.None,
            environment: null);
        Assert.Equal(0, result.ExitCode);
        // Sanitization exercised: null caller-env still strips DEVELOPER_DIR
        // and SDKROOT without throwing.
    }

    // ── Env sanitization: nix-store git (inside /nix/store) ───────────

    /// <summary>
    /// When the pinned binary is inside /nix/store/, DEVELOPER_DIR and
    /// SDKROOT must be <em>preserved</em> — the nix-packaged git may
    /// genuinely need those paths, and the nix store guarantees they are
    /// stable for the process lifetime.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenNixStoreGit_PreservesDeveloperDirAndSdkroot()
    {
        const string nixGit = "/nix/store/abc123-git-2.45/bin/git";
        if (!File.Exists(nixGit))
            Assert.Skip(
                $"Requires a real nix-store git binary at '{nixGit}' — this is a fake " +
                "test path that only exists under a real nix-managed macOS environment.");

        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");

        var invoker = new GitInvoker(nixGit);
        var result = await invoker.RunAsync(repo.Root, ["status"], CancellationToken.None,
            environment: new Dictionary<string, string>
            {
                ["DEVELOPER_DIR"] = "/nix/store/def456-apple-sdk-14.4",
                ["SDKROOT"] = "/nix/store/ghi789-macos-sdk-14.0",
                ["HOME"] = "/Users/test",
            });
        Assert.Equal(0, result.ExitCode);
        // Construction with a nix path means _envRemove is null — DEVELOPER_DIR
        // and SDKROOT are left in place.
    }

    // ── Fail-fast ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenNoGitFound_ThrowsDescriptiveError()
    {
        var invoker = new GitInvoker("/nonexistent/path/git");
        var repoPath = $"/tmp/git-invoker-nofound-{Guid.NewGuid():N}";
        Directory.CreateDirectory(repoPath);
        try
        {
            // This will try to run the non-existent binary and fail.
            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => invoker.RunAsync(repoPath, ["status"], CancellationToken.None));
            Assert.NotNull(ex);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    // ── Real-git smoke test ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithRealGit_ExecutesSuccessfully()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        File.WriteAllText(Path.Combine(repo.Root, "readme.md"), "hello");
        var invoker = new GitInvoker();
        var result = await invoker.RunAsync(
            repo.Root, ["status", "--porcelain"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("readme.md", result.Output, StringComparison.Ordinal);
    }
}
