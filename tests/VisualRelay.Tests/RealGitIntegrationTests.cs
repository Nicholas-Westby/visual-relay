using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Opt-in (<c>VR_RUN_SLOW_INTEGRATION=1</c>) end-to-end coverage for the handful of
/// behaviours that genuinely need the real git binary: a real
/// <c>.git/hooks/pre-commit</c> that git itself executes, a real stash cycle, a real
/// <c>git bundle</c> round-trip, and a real squash (soft-reset + re-commit). The
/// always-on suite covers the SAME decision logic in-memory via GitSim; these guard
/// the real seam without slowing the default run. Every fact is hermetic (no host git
/// config, no credential prompt) and skipped unless the opt-in env var is set.
/// </summary>
public sealed class RealGitIntegrationTests
{
    internal static bool Ready()
    {
        SlowIntegration.SkipIfNotOptedIn();
        if (!SlowIntegration.ToolAvailable("git"))
        {
            Assert.Skip("git binary not on PATH.");
            return false;
        }

        return true;
    }

    /// <summary>Runs real git under <paramref name="root"/>, hermetic, returning the exit code and combined output WITHOUT asserting success (so rejection paths are observable).</summary>
    private static (int Exit, string Output) RunGit(
        string root, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(File.Exists("/usr/bin/git") ? "/usr/bin/git" : "git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        process.StartInfo.Environment.Remove("DEVELOPER_DIR");
        process.StartInfo.Environment.Remove("SDKROOT");
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_SYSTEM"] = "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0",
        })
        {
            process.StartInfo.Environment[k] = v;
        }
        if (env is not null)
            foreach (var (k, v) in env)
                process.StartInfo.Environment[k] = v;
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(root);
        foreach (var a in args)
            process.StartInfo.ArgumentList.Add(a);

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>Runs real git and asserts exit 0 (setup steps that must succeed).</summary>
    internal static string Git(string root, params string[] args)
    {
        var (exit, output) = RunGit(root, null, args);
        Assert.True(exit == 0, $"git {string.Join(' ', args)} failed ({exit}): {output}");
        return output;
    }

    /// <summary>A real git repo with a single seed commit of <c>src/app.cs</c>.</summary>
    private static void SeedRepo(string root, string seedContent = "base")
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.cs"), seedContent);
        Git(root, "init", "-q");
        Git(root, "config", "user.email", "visual-relay@example.test");
        Git(root, "config", "user.name", "Visual Relay Tests");
        Git(root, "add", ".");
        Git(root, "commit", "-q", "-m", "chore: seed repo");
    }

    // ── pre-commit hook install + rejection e2e ──────────────────────────────

    [Fact]
    public async Task PreCommitHook_RealHook_RejectsUntokenedCommitDuringRun_AcceptsWithToken()
    {
        if (!Ready()) return;
        using var repo = TestRepository.Create();
        SeedRepo(repo.Root);

        // HookInstaller writes a REAL .git/hooks/pre-commit that git executes.
        var install = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None, new GitInvoker());
        Assert.True(install.Installed);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".git", "hooks", "pre-commit")));

        // An active run: only a commit carrying the matching RELAY_COMMIT_TOKEN may land.
        var activeDir = Path.Combine(repo.Root, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        await File.WriteAllTextAsync(Path.Combine(activeDir, "info.json"), "{\"nonce\":\"tok-9\"}");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "changed");
        Git(repo.Root, "add", "-A");

        // Without the token the real hook rejects the commit.
        var rejected = RunGit(repo.Root, null, "commit", "-m", "agent: premature");
        Assert.NotEqual(0, rejected.Exit);
        Assert.Contains("run is active", rejected.Output, StringComparison.Ordinal);

        // With the matching token the hook lets it through.
        var accepted = RunGit(
            repo.Root,
            new Dictionary<string, string> { ["RELAY_COMMIT_TOKEN"] = "tok-9" },
            "commit", "-m", "feat: sealed");
        Assert.Equal(0, accepted.Exit);
        Assert.Equal("feat: sealed", Git(repo.Root, "log", "-1", "--pretty=%s").Trim());
    }

    // ── RedGate stash cycle ──────────────────────────────────────────────────

    [Fact]
    public async Task RedGate_RealStash_StripsToRedAndRestores()
    {
        if (!Ready()) return;
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "src.txt"), "old\n");
        Git(repo.Root, "init", "-q");
        Git(repo.Root, "config", "user.email", "visual-relay@example.test");
        Git(repo.Root, "config", "user.name", "Visual Relay Tests");
        Git(repo.Root, "add", ".");
        Git(repo.Root, "commit", "-q", "-m", "chore: seed repo");
        File.WriteAllText(Path.Combine(repo.Root, "src.txt"), "new\n");

        var tag = RedGate.StashTag("task", "absent-path");
        var stashed = await RedGate.StripToRedAsync(repo.Root, ["src.txt", "ghost.txt"], tag, CancellationToken.None);

        Assert.True(stashed);
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(repo.Root, "src.txt")));
        Assert.NotNull(await RedGate.FindStashRefAsync(repo.Root, tag, CancellationToken.None));
        Assert.Equal(RedGateRestoreResult.Restored, await RedGate.RestoreStashAsync(repo.Root, tag, CancellationToken.None));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repo.Root, "src.txt")));
    }

    // ── FlaggedWorkStore bundle capture + restore (real git bundle) ──────────

    [Fact]
    public async Task FlaggedWorkStore_RealBundle_CapturesAndRestoresFlaggedWork()
    {
        if (!Ready()) return;
        using var repo = TestRepository.Create();
        var git = new GitInvoker();
        // .gitignore (.relay/*) is committed together with the seed so the bundle's
        // sidecar/bundle under .relay never shows as untracked.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), ".relay/*\n");
        SeedRepo(repo.Root);

        var taskId = "task-flagged";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "run-base.txt"), Git(repo.Root, "rev-parse", "HEAD").Trim());

        // Uncommitted work the flag must preserve.
        var featureFile = Path.Combine(repo.Root, "src", "Feature.cs");
        await File.WriteAllTextAsync(featureFile, "// feature");

        await FlaggedWorkStore.CaptureAsync(
            repo.Root, taskId, taskDirectory, flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath), "capture must write flagged-work.bundle");
        Assert.True(File.Exists(Path.Combine(taskDirectory, "flagged-work.json")));
        // Real git validates the bundle it produced (round-trip integrity).
        Assert.Equal(0, RunGit(repo.Root, null, "bundle", "verify", bundlePath).Exit);
        Assert.DoesNotContain("flagged-work.bundle", Git(repo.Root, "status", "--porcelain"), StringComparison.Ordinal);

        // Restore reinstates the flagged working-tree file from the bundle.
        File.Delete(featureFile);
        await FlaggedWorkStore.RestoreAsync(repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(File.Exists(featureFile), "restore must reinstate the flagged feature file");
        Assert.Equal("// feature", await File.ReadAllTextAsync(featureFile));
    }

    // ── squash end-to-end (real soft-reset + re-commit) ──────────────────────

    [Fact]
    public async Task GitCommitter_RealSquash_CollapsesAgentSelfCommitIntoOneSealedCommit()
    {
        if (!Ready()) return;
        using var repo = TestRepository.Create();
        SeedRepo(repo.Root);
        var runBase = Git(repo.Root, "rev-parse", "HEAD").Trim();

        // Agent self-commits the bulk implementation mid-run (bare, no trailers).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "implemented");
        File.WriteAllText(Path.Combine(repo.Root, "src", "feature.cs"), "new feature");
        Git(repo.Root, "add", "-A");
        Git(repo.Root, "commit", "-q", "-m", "wip");

        // A further working-tree edit after the self-commit, not yet committed.
        File.WriteAllText(Path.Combine(repo.Root, "src", "extra.cs"), "extra");

        var result = await GitCommitter.CommitAsync(
            repo.Root, "my-task", "abc123",
            ["feat: add widget"], ["src/app.cs", "src/feature.cs", "src/extra.cs"], [],
            commitToken: null, preRunUntracked: null, tasksDir: null,
            CancellationToken.None, new GitInvoker(), runBaseSha: runBase);

        Assert.True(result.Success, $"Expected success, got: {result.Error}");
        Assert.Equal("1", Git(repo.Root, "rev-list", "--count", $"{runBase}..HEAD").Trim());
        Assert.Equal(runBase, Git(repo.Root, "rev-parse", "HEAD^").Trim());
        var fullMessage = Git(repo.Root, "log", "-1", "--pretty=%B");
        Assert.Contains("Task: my-task", fullMessage, StringComparison.Ordinal);
        Assert.Contains("Relay-Seal: abc123", fullMessage, StringComparison.Ordinal);
        Assert.Equal("implemented", Git(repo.Root, "show", "HEAD:src/app.cs"));
        Assert.Equal("new feature", Git(repo.Root, "show", "HEAD:src/feature.cs"));
        Assert.Equal("extra", Git(repo.Root, "show", "HEAD:src/extra.cs"));
        Assert.Equal(string.Empty, Git(repo.Root, "status", "--porcelain").Trim());
    }
}
