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
        var capturedBinary = string.Empty;
        var capturedArgs = Array.Empty<string>();
        GitInvoker.Override = (binary, args, _, _, _, _) =>
        {
            capturedBinary = binary;
            capturedArgs = args.ToArray();
            return Task.FromResult((0, "override-output", false));
        };
        try
        {
            var result = await GitInvoker.RunAsync(
                "/tmp/test-repo", ["status", "--porcelain"], CancellationToken.None);
            Assert.Equal("override-output", result.Output);
            Assert.Equal(0, result.ExitCode);
            Assert.NotEqual("git", capturedBinary);
            Assert.Contains("status", capturedArgs);
            Assert.Contains("--porcelain", capturedArgs);
        }
        finally { GitInvoker.Override = null; }
    }

    [Fact]
    public async Task Override_WhenSet_PassesResolvedBinaryToOverride()
    {
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/usr/bin/git");
        GitInvoker.Override = (binary, _, _, _, _, _) =>
            Task.FromResult((0, binary, false));
        try
        {
            var result = await GitInvoker.RunAsync(
                "/tmp/test-repo", ["rev-parse", "--is-inside-work-tree"], CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("/usr/bin/git", result.Output);
        }
        finally { GitInvoker.Override = null; }
    }

    // ── Env sanitization: system git (outside /nix/store) ─────────────

    [Fact]
    public async Task RunAsync_WhenSystemGit_StripsDeveloperDirAndSdkroot()
    {
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/usr/bin/git");
        var capturedEnv = (IReadOnlyDictionary<string, string>?)null;
        GitInvoker.Override = (_, _, _, _, _, env) =>
        {
            capturedEnv = env;
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync("/tmp/test-repo", ["status"], CancellationToken.None,
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
        finally { GitInvoker.Override = null; }
    }

    [Fact]
    public async Task RunAsync_WithNullEnvironment_StillSanitizes()
    {
        GitInvoker.ResetForTests();
        GitInvoker.SetResolvedBinaryForTests("/usr/bin/git");
        var capturedEnv = (IReadOnlyDictionary<string, string>?)null;
        GitInvoker.Override = (_, _, _, _, _, env) =>
        {
            capturedEnv = env;
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync("/tmp/test-repo", ["status"], CancellationToken.None,
                environment: null);
            Assert.NotNull(capturedEnv);
        }
        finally { GitInvoker.Override = null; }
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
        var capturedEnv = (IReadOnlyDictionary<string, string>?)null;
        GitInvoker.Override = (_, _, _, _, _, env) =>
        {
            capturedEnv = env;
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync("/tmp/test-repo", ["status"], CancellationToken.None,
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
        finally { GitInvoker.Override = null; }
    }

    // ── Fail-fast ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenNoGitFound_ThrowsDescriptiveError()
    {
        GitInvoker.ResetForTests();
        GitInvoker.Override = (_, _, _, _, _, _) =>
            throw new InvalidOperationException(
                "git: no working git binary found — tried xcrun --find git, " +
                "command -v git, and /usr/bin/git");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => GitInvoker.RunAsync("/tmp/test-repo", ["status"], CancellationToken.None));
            Assert.Contains("no working git binary found", ex.Message);
        }
        finally { GitInvoker.Override = null; }
    }

    // ── Caching ───────────────────────────────────────────────────────

    [Fact]
    public async Task GitBinary_IsCached_AcrossInvocations()
    {
        GitInvoker.ResetForTests();
        var callCount = 0;
        string? firstBinary = null;
        GitInvoker.Override = (binary, _, _, _, _, _) =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count == 1) firstBinary = binary;
            if (count == 2) Assert.Equal(firstBinary, binary);
            return Task.FromResult((0, string.Empty, false));
        };
        try
        {
            await GitInvoker.RunAsync("/tmp/repo", ["rev-parse", "--git-dir"], CancellationToken.None);
            await GitInvoker.RunAsync("/tmp/repo", ["status"], CancellationToken.None);
            Assert.Equal(2, callCount);
        }
        finally { GitInvoker.Override = null; }
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
