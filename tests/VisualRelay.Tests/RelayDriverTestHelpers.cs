using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers extracted from the former RelayDriverTests partial class so
/// companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class RelayDriverTestHelpers
{
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

    public static async Task RunHappyPath(TestRepository repo, string taskId)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        Assert.Equal(RelayTaskOutcomeStatus.Committed, (await driver.RunTaskAsync(repo.Root, taskId)).Status);
    }
}
