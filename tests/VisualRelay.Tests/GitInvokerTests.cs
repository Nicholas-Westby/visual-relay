using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="GitInvoker"/> — the centralized git process factory
/// that pins a stable git binary at startup and sanitizes the environment so
/// nix-store churn on macOS cannot rot git invocations mid-run.
/// </summary>
[Collection("GitInvoker")]
public sealed class GitInvokerTests
{
    // ── Override seam ─────────────────────────────────────────────────

    [Fact]
    public async Task Override_WhenSet_DelegatesToOverride()
    {
        GitInvoker.ResetForTests();
        var repoPath = $"/tmp/git-invoker-override-{Guid.NewGuid():N}";
        var capturedBinary = string.Empty;
        var capturedArgs = Array.Empty<string>();
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }
            capturedBinary = binary;
            capturedArgs = args.ToArray();
            return Task.FromResult((0, "override-output", false));
        };
        try
        {
            var result = await GitInvoker.RunAsync(
                repoPath, ["status", "--porcelain"], CancellationToken.None);
            Assert.Equal("override-output", result.Output);
            Assert.Equal(0, result.ExitCode);
            Assert.NotEqual("git", capturedBinary);
            Assert.Contains("status", capturedArgs);
            Assert.Contains("--porcelain", capturedArgs);
        }
        finally { GitInvoker.ResetForTests(); }
    }

    [Fact]
    public async Task Override_WhenSet_PassesResolvedBinaryToOverride()
    {
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/usr/bin/git");
        var repoPath = $"/tmp/git-invoker-resolved-{Guid.NewGuid():N}";
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }
            return Task.FromResult((0, binary, false));
        };
        try
        {
            var result = await GitInvoker.RunAsync(
                repoPath, ["rev-parse", "--is-inside-work-tree"], CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("/usr/bin/git", result.Output);
        }
        finally { GitInvoker.ResetForTests(); }
    }

    // ── Env sanitization: system git (outside /nix/store) ─────────────

    [Fact]
    public async Task RunAsync_WhenSystemGit_StripsDeveloperDirAndSdkroot()
    {
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/usr/bin/git");
        var repoPath = $"/tmp/git-invoker-sys-{Guid.NewGuid():N}";
        var capturedEnv = (IReadOnlyDictionary<string, string>?)null;
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }
            capturedEnv = env;
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync(repoPath, ["status"], CancellationToken.None,
                environment: new Dictionary<string, string>
                {
                    ["DEVELOPER_DIR"] = "/nix/store/deadbeef-apple-sdk-14.4",
                    ["SDKROOT"] = "/nix/store/cafebabe-macos-sdk-14.0",
                    ["HOME"] = "/Users/test",
                });
            Assert.NotNull(capturedEnv);
            Assert.False(capturedEnv!.ContainsKey("DEVELOPER_DIR"),
                "DEVELOPER_DIR must be stripped when git binary is outside /nix/store");
            Assert.False(capturedEnv.ContainsKey("SDKROOT"),
                "SDKROOT must be stripped when git binary is outside /nix/store");
            Assert.Equal("/Users/test", capturedEnv["HOME"]);
        }
        finally { GitInvoker.ResetForTests(); }
    }

    [Fact]
    public async Task RunAsync_WithNullEnvironment_StillSanitizes()
    {
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/usr/bin/git");
        var repoPath = $"/tmp/git-invoker-null-{Guid.NewGuid():N}";
        var capturedEnv = (IReadOnlyDictionary<string, string>?)null;
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }
            capturedEnv = env;
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync(repoPath, ["status"], CancellationToken.None,
                environment: null);
            Assert.NotNull(capturedEnv);
        }
        finally { GitInvoker.ResetForTests(); }
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
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/nix/store/abc123-git-2.45/bin/git");
        var repoPath = $"/tmp/git-invoker-nix-{Guid.NewGuid():N}";
        var capturedEnv = (IReadOnlyDictionary<string, string>?)null;
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }
            capturedEnv = env;
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync(repoPath, ["status"], CancellationToken.None,
                environment: new Dictionary<string, string>
                {
                    ["DEVELOPER_DIR"] = "/nix/store/def456-apple-sdk-14.4",
                    ["SDKROOT"] = "/nix/store/ghi789-macos-sdk-14.0",
                    ["HOME"] = "/Users/test",
                });
            Assert.NotNull(capturedEnv);
            Assert.True(capturedEnv!.ContainsKey("DEVELOPER_DIR"),
                "DEVELOPER_DIR must be preserved when git binary is inside /nix/store");
            Assert.True(capturedEnv.ContainsKey("SDKROOT"),
                "SDKROOT must be preserved when git binary is inside /nix/store");
            Assert.Equal("/Users/test", capturedEnv["HOME"]);
        }
        finally { GitInvoker.ResetForTests(); }
    }

    // ── Fail-fast ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenNoGitFound_ThrowsDescriptiveError()
    {
        GitInvoker.ResetForTests();
        var repoPath = $"/tmp/git-invoker-nogit-{Guid.NewGuid():N}";
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }
            throw new InvalidOperationException(
                "git: no working git binary found — tried xcrun --find git, " +
                "command -v git, and /usr/bin/git");
        };
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => GitInvoker.RunAsync(repoPath, ["status"], CancellationToken.None));
            Assert.Contains("no working git binary found", ex.Message);
        }
        finally { GitInvoker.ResetForTests(); }
    }

    // ── Caching ───────────────────────────────────────────────────────

    [Fact]
    public async Task GitBinary_IsCached_AcrossInvocations()
    {
        GitInvoker.ResetForTests();
        var repoPath = $"/tmp/git-invoker-cache-{Guid.NewGuid():N}";
        var callCount = 0;
        string? firstBinary = null;
        GitInvoker.Override = (binary, args, rootPath, cancellationToken, timeout, env) =>
        {
            // Only intercept calls for this test's repo — pass through
            // unrelated calls (e.g. from DrainQueue tests that run in
            // parallel collections) so cross-collection interference
            // cannot skew the count.
            if (rootPath != repoPath)
            {
                return ProcessCapture.RunAsync(
                    binary, args, rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), cancellationToken, env);
            }

            var count = Interlocked.Increment(ref callCount);
            if (count == 1) firstBinary = binary;
            if (count == 2) Assert.Equal(firstBinary, binary);
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync(repoPath, ["rev-parse", "--git-dir"], CancellationToken.None);
            await GitInvoker.RunAsync(repoPath, ["status"], CancellationToken.None);
            Assert.Equal(2, callCount);
        }
        finally { GitInvoker.ResetForTests(); }
    }

    // ── Real-git smoke test ───────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithRealGit_ExecutesSuccessfully()
    {
        GitInvoker.ResetForTests();
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        File.WriteAllText(Path.Combine(repo.Root, "readme.md"), "hello");
        var result = await GitInvoker.RunAsync(
            repo.Root, ["status", "--porcelain"], CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("readme.md", result.Output, StringComparison.Ordinal);
    }
}
