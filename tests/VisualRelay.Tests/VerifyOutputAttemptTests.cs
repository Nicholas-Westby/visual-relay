using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A verify's full output is the autopsy artifact for a red run, so a later run must not write
/// over it. Stage 10 and the stage-12 commit gate named theirs attempt 1 whatever attempt they
/// were. Found with max-sixty/worktrunk on the Mac: the resumed Verify wrote its green output
/// over the first run's red one, and the only record of why that run failed was gone.
/// </summary>
public sealed class VerifyOutputAttemptTests
{
    [Fact]
    public async Task RunTaskAsync_ResumedVerify_KeepsThePreviousRunsVerifyOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: false);
        repo.WriteTask("reverify", "# Re-verify\n");
        var taskDir = Path.Combine(repo.Root, ".relay", "reverify");

        var first = await RunAsync(repo, "reverify", new RelayDriverOptions(CreateGitCommit: false),
            new TestRunResult(1, "red"),                    // stage 5 author gate
            new TestRunResult(1, "Failed FirstRunTest"),    // stage 10 verify
            new TestRunResult(1, "Failed FirstRunTest"));   // stage 10 retry
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, first.Status);

        var resumed = await RunAsync(repo, "reverify", new RelayDriverOptions(CreateGitCommit: false, Resume: true),
            new TestRunResult(0, "SecondRunGreen"));        // stage 10 verify
        Assert.Equal(RelayTaskOutcomeStatus.Committed, resumed.Status);

        Assert.Contains("FirstRunTest", await ReadAsync(taskDir, "stage10-attempt1.verify-output.txt"), StringComparison.Ordinal);
        Assert.Contains("SecondRunGreen", await ReadAsync(taskDir, "stage10-attempt2.verify-output.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ResumedFixVerify_KeepsThePreviousRunsFixVerifyOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 1);
        repo.WriteTask("refix", "# Re-fix\n");
        var taskDir = Path.Combine(repo.Root, ".relay", "refix");

        var first = await RunAsync(repo, "refix", new RelayDriverOptions(CreateGitCommit: false),
            new TestRunResult(1, "red"),                    // stage 5 author gate
            new TestRunResult(1, "Failed Verify"),          // stage 10 verify
            new TestRunResult(1, "Failed Verify"),          // stage 10 retry
            new TestRunResult(1, "Failed FirstRunFix"),     // stage 11 verify
            new TestRunResult(1, "Failed FirstRunFix"));    // stage 11 retry
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, first.Status);

        await RunAsync(repo, "refix", new RelayDriverOptions(CreateGitCommit: false, Resume: true),
            new TestRunResult(1, "Failed Verify"),          // stage 10 verify, re-entered
            new TestRunResult(1, "Failed Verify"),          // stage 10 retry
            new TestRunResult(0, "SecondRunFixGreen"));     // stage 11 verify

        Assert.Contains("FirstRunFix", await ReadAsync(taskDir, "stage11-attempt1.verify-output.txt"), StringComparison.Ordinal);
        Assert.Contains("SecondRunFixGreen", await ReadAsync(taskDir, "stage11-attempt2.verify-output.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_CommitGateResumedTwice_KeepsBothVerifyOutputs()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("exit 1", []);
        repo.WriteTask("regate", "# Re-gate\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "app.cs"), "hello", TestContext.Current.CancellationToken);
        var manifest = new[] { "src/app.cs" };
        RelayDriverResumeTestHelpers.SetupCommitGateResumeScenario(
            repo.Root, "regate", manifest, RelayDriverResumeTestHelpers.ComputeTreeHash(repo.Root, manifest));
        var taskDir = Path.Combine(repo.Root, ".relay", "regate");
        var resume = new RelayDriverOptions(CreateGitCommit: false, Resume: true);

        await RunAsync(repo, "regate", resume,
            new TestRunResult(1, "FAIL: FirstGate"), new TestRunResult(1, "FAIL: FirstGate"));
        await RunAsync(repo, "regate", resume,
            new TestRunResult(1, "FAIL: SecondGate"), new TestRunResult(1, "FAIL: SecondGate"));

        Assert.Contains("FirstGate", await ReadAsync(taskDir, "stage12-attempt1.verify-output.txt"), StringComparison.Ordinal);
        Assert.Contains("SecondGate", await ReadAsync(taskDir, "stage12-attempt2.verify-output.txt"), StringComparison.Ordinal);
    }

    private static Task<RelayTaskOutcome> RunAsync(
        TestRepository repo, string taskId, RelayDriverOptions options, params TestRunResult[] testResults)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(testResults), new InMemoryRelayEventSink()),
            options);
        return driver.RunTaskAsync(repo.Root, taskId);
    }

    private static async Task<string> ReadAsync(string taskDir, string fileName)
    {
        var path = Path.Combine(taskDir, fileName);
        Assert.True(File.Exists(path), $"{fileName} was not written");
        return await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
    }
}
