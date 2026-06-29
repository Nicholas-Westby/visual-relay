using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The planning stages run inside a detached-HEAD worktree of the target repo
/// (<see cref="PlanningWorktree.CreateAsync"/>). A clean checkout contains only
/// COMMITTED files, but the per-repo config <c>.relay/config.json</c> is normally
/// git-ignored (a repo-level <c>.gitignore</c> with <c>.relay/</c>), so it is NOT
/// in the checkout. Planning then loads config from the worktree, finds nothing,
/// and stage 1 flags with ".relay/config.json not found".
///
/// The fix mirrors the verify worktree's "provide a needed git-ignored file the
/// checkout lacks" pattern: after the worktree is created, the source repo's
/// <c>.relay/config.json</c> is copied into the worktree so planning's config
/// load succeeds WITHOUT requiring the user to commit it.
///
/// These tests use the real GitInvoker against a temp <c>git init</c> repo (same
/// pattern as VerifyWorktreeIgnoredOverlay / NonoRollbackSkipDirs / WorktreeResetter).
/// </summary>
public sealed class PlanningWorktreeConfigCopyTests
{
    private static void InitRepo(string root)
    {
        TestGit.Run(root, "init", "-q");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
    }

    private static void CommitAll(string root, string message)
    {
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-q", "-m", message);
    }

    private const string SampleConfig =
        """
        {
          "testCmd": "dotnet test",
          "logSources": [],
          "enableFixVerify": false
        }
        """;

    // ───────────────────────────────────────────────────────────────────
    // 1. .relay/ git-ignored, config.json present in the working tree:
    //    after worktree creation + config copy, the planning config load
    //    SUCCEEDS and <worktree>/.relay/config.json matches the source.
    //    PRE-FIX (no copy) this fails with ".relay/config.json not found".
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ConfigCopiedIntoWorktree_WhenRelayIsGitIgnored_LoadSucceeds()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            InitRepo(root);
            // Repo-level .gitignore excludes .relay/ entirely — the normal setup.
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".relay/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // Real config in the working tree, but git-ignored (NOT committed).
            Directory.CreateDirectory(Path.Combine(root, ".relay"));
            await File.WriteAllTextAsync(Path.Combine(root, ".relay", "config.json"), SampleConfig);
            CommitAll(root, "seed"); // commits .gitignore + tracked.txt only; .relay ignored

            // Sanity: the clean checkout does NOT contain the git-ignored config.
            worktree = await PlanningWorktree.CreateAsync(root, "cfg-task", "run-cfg", CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(worktree, ".relay", "config.json")),
                "pre-condition: the detached checkout must lack the git-ignored config");

            // The fix: copy the source config into the worktree.
            PlanningWorktree.CopyConfigIntoWorktree(root, worktree);

            // The config is now present and the planning config load succeeds.
            var copied = Path.Combine(worktree, ".relay", "config.json");
            Assert.True(File.Exists(copied), "config.json must be copied into the worktree");
            Assert.Equal(SampleConfig, await File.ReadAllTextAsync(copied));

            // End-to-end: LoadAsync (what planning calls) no longer throws.
            var config = await RelayConfigLoader.LoadAsync(worktree, CancellationToken.None);
            Assert.Equal("dotnet test", config.TestCommand);
            Assert.False(config.EnableFixVerify);
        }
        finally
        {
            if (worktree is not null)
                await PlanningWorktree.RemoveAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. A repo with NO .relay/config.json at all behaves as before: the
    //    copy is a best-effort no-op (never throws, never creates a stray
    //    file), and the existing no-config path (Defaulted → FileNotFound)
    //    is unchanged.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task NoSourceConfig_CopyIsNoOp_AndDoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-pw-nocfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".relay/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            CommitAll(root, "seed"); // no .relay/config.json anywhere

            worktree = await PlanningWorktree.CreateAsync(root, "nocfg-task", "run-nocfg", CancellationToken.None);

            // Best-effort copy must not throw when the source has no config.
            var ex = Record.Exception(() => PlanningWorktree.CopyConfigIntoWorktree(root, worktree));
            Assert.Null(ex);

            // No stray config.json was created in the worktree.
            Assert.False(File.Exists(Path.Combine(worktree, ".relay", "config.json")),
                "no source config → no config.json fabricated in the worktree");

            // The existing no-config behavior is preserved: LoadAsync still throws
            // FileNotFound (callers already handle this separately).
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                RelayConfigLoader.LoadAsync(worktree, CancellationToken.None));
        }
        finally
        {
            if (worktree is not null)
                await PlanningWorktree.RemoveAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. END-TO-END regression: the whole plan phase succeeds against a repo
    //    that git-ignores .relay/ (the normal setup) with an UNCOMMITTED
    //    config in the working tree. PRE-FIX every task flagged stage 1 with
    //    "config not found" because the checkout lacked the ignored config.
    // ───────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunPlanPhase_ConfigGitIgnored_NotCommitted_PlansSuccessfully()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []); // .relay/config.json in the working tree
        repo.WriteTask("ignored-cfg", "# Ignored config\n");

        // Init the repo and commit EVERYTHING EXCEPT .relay/ (git-ignored), so the
        // detached planning checkout will NOT contain config.json — the real bug.
        TestGit.Run(repo.Root, "init", "-q");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), ".relay/\n");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-q", "-m", "seed");

        // Sanity: config.json is genuinely git-ignored (not in the committed tree).
        var tracked = TestGit.Run(repo.Root, "ls-files", ".relay");
        Assert.True(string.IsNullOrWhiteSpace(tracked),
            "pre-condition: .relay/config.json must be git-ignored (uncommitted)");

        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var config = PlanPhaseTestHelpers.MakeConfig(maxPlanConcurrency: 1);
        var results = await PlanPhaseRunner.RunPlanPhaseAsync(
            mainRootPath: repo.Root,
            tasks: [("ignored-cfg", runner)],
            config: config,
            testRunner: new ScriptedTestRunner(),
            cancellationToken: CancellationToken.None,
            environmentAccessor: PlanPhaseTestHelpers.TempXdg);

        Assert.Single(results);
        // PRE-FIX: Flagged with "config not found". POST-FIX: Planned.
        Assert.Equal(RelayTaskOutcomeStatus.Planned, results[0].Outcome.Status);
    }
}
