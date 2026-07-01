using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverResumeTests
{
    [Fact]
    public async Task Driver_Resume_RestoresWork_DeletesBundleOnCommit()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("resume-driver", "# Resume driver\n");
        InitTestRepo(repo.Root);

        // Run 1: flag at stage 6 (Implement). Use CreateGitCommit so the
        // run-base SHA is persisted — needed for the flagged-work bundle.
        var flagRunner = new FlagAtStageSubagentRunner(flagAtStage: 6);
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(flagRunner, new ScriptedTestRunner(
                new TestRunResult(1, "red")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: true));
        var outcome1 = await driver1.RunTaskAsync(repo.Root, "resume-driver");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);

        var taskDir = Path.Combine(repo.Root, ".relay", "resume-driver");
        var bundlePath = Path.Combine(taskDir, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath), "Bundle should exist after flag");

        // Run 2: resume — should restore flagged work and complete.
        var happyRunner = new ScriptedSubagentRunner();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(happyRunner, new ScriptedTestRunner(
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "resume-driver");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);

        // Bundle must be deleted on successful commit.
        Assert.False(File.Exists(bundlePath), "Bundle should be deleted on commit");
    }

    [Fact]
    public async Task Driver_Resume_RestoresWork_OntoAdvancedBase()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("resume-adv", "# Advanced base\n");
        InitTestRepo(repo.Root);

        // Run 1: flag at stage 6.
        var flagRunner = new FlagAtStageSubagentRunner(flagAtStage: 6);
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(flagRunner, new ScriptedTestRunner(
                new TestRunResult(1, "red")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: true));
        await driver1.RunTaskAsync(repo.Root, "resume-adv");

        var taskDir = Path.Combine(repo.Root, ".relay", "resume-adv");
        var bundlePath = Path.Combine(taskDir, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath));

        // Advance HEAD with an unrelated commit.
        File.WriteAllText(Path.Combine(repo.Root, "unrelated.txt"), "unrelated");
        TestGit.Run(repo.Root, "add", "unrelated.txt");
        TestGit.Run(repo.Root, "commit", "-m", "unrelated commit");

        // Run 2: resume onto advanced base.
        var happyRunner = new ScriptedSubagentRunner();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(happyRunner, new ScriptedTestRunner(
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "resume-adv");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);
        Assert.False(File.Exists(bundlePath), "Bundle should be deleted on commit");
    }

    [Fact]
    public async Task Driver_Resume_DoesNotRestore_WhenNotMidPipeline()
    {
        // Flag at stage 3 (pre-code) — resume should NOT restore.
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 0", [], enableFixVerify: false);
        repo.WriteTask("resume-s3", "# Stage 3 resume\n");
        InitTestRepo(repo.Root);

        var flagRunner = new FlagAtStageSubagentRunner(flagAtStage: 3);
        var driver1 = new RelayDriver(
            RelayDriverDependencies.ForTests(flagRunner, new ScriptedTestRunner(),
                new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: true));
        var outcome1 = await driver1.RunTaskAsync(repo.Root, "resume-s3");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome1.Status);

        var taskDir = Path.Combine(repo.Root, ".relay", "resume-s3");
        var bundlePath = Path.Combine(taskDir, "flagged-work.bundle");
        if (File.Exists(bundlePath))
            File.Delete(bundlePath); // bundle may or may not exist; regardless, resume skips restore

        // Resume from stage 3: restore is gated to 5-10 only.
        var happyRunner = new ScriptedSubagentRunner();
        var driver2 = new RelayDriver(
            RelayDriverDependencies.ForTests(happyRunner, new ScriptedTestRunner(
                new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            new RelayDriverOptions(CreateGitCommit: true, Resume: true));
        var outcome2 = await driver2.RunTaskAsync(repo.Root, "resume-s3");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome2.Status);
    }

    private static void InitTestRepo(string root)
    {
        TestGit.Run(root, "init", "-b", "main");
        TestGit.Run(root, "config", "user.email", "test@test");
        TestGit.Run(root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".relay/*\n");
        TestGit.Run(root, "add", ".gitignore");
        TestGit.Run(root, "commit", "-m", "initial");
    }
}
