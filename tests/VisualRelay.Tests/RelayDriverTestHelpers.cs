using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers extracted from the former RelayDriverTests partial class so
/// companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class RelayDriverTestHelpers
{
    /// <summary>
    /// Real-git repo seed (used only by the opt-in <see cref="RealGitIntegrationTests"/>
    /// and callers still asserting against the real binary). Hermetic: the spawn seam
    /// (<see cref="TestGit"/>) pins <c>GIT_CONFIG_GLOBAL/SYSTEM=/dev/null</c> so no host
    /// config is scanned. In-memory driver tests seed via <see cref="InitSim"/> instead.
    /// </summary>
    public static void InitGitRepo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "status.cs"), "old\n");
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", "chore: seed repo");
    }

    /// <summary>
    /// Driver dependencies for a git-FREE driver test: identical to
    /// <see cref="RelayDriverDependencies.ForTests(ISubagentRunner, ITestRunner, IRelayEventSink, IGitInvoker?, IEnvironmentAccessor?)"/>
    /// but binds an in-memory <see cref="GitSimEngine"/> (unregistered at
    /// <paramref name="repo"/>'s root, so every git probe answers
    /// <c>fatal: not a git repository</c> exactly as the real binary does on a
    /// non-repo <see cref="TestRepository"/> root) — removing the real-git process
    /// floor the default <c>new GitInvoker()</c> imposed on all eleven stages.
    /// </summary>
    public static RelayDriverDependencies DepsFor(
        TestRepository repo, ISubagentRunner runner, ITestRunner testRunner, IRelayEventSink sink)
    {
        _ = repo; // deps are only ever driven with repo.Root; a GitSim is root-agnostic.
        return RelayDriverDependencies.ForTests(runner, testRunner, sink, new GitSimEngine());
    }

    /// <summary>
    /// A GitSim registered at <paramref name="repo"/>'s root (empty, unborn HEAD),
    /// the in-memory stand-in for <c>git init</c> that a git-ASSERTING driver test
    /// injects (<c>ForTests(..., sim)</c>) then seeds via <c>sim.Seed</c>/<c>sim.Commit</c>
    /// and inspects via <c>sim.Head</c>/<c>sim.CommitInfo</c>/<c>sim.FilesInCommit</c>.
    /// </summary>
    public static GitSimEngine InitSim(TestRepository repo, string branch = "main")
    {
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root, branch);
        return sim;
    }

    /// <summary>
    /// Asserts a completed clean-review, green-verify happy path: Fix (9) and
    /// Fix-verify (11) are recorded Skipped (no findings / nothing to fix), and
    /// every other stage is Done.
    /// </summary>
    public static void AssertHappyPathStatuses(IReadOnlyList<StageStatusEntry> entries) =>
        Assert.All(entries, e => Assert.Equal(e.Stage is 9 or 11 ? "Skipped" : "Done", e.Status));

    public static async Task RunHappyPath(TestRepository repo, string taskId)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, (await driver.RunTaskAsync(repo.Root, taskId)).Status);
    }
}
