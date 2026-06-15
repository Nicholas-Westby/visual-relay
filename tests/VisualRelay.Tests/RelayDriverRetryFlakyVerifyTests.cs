using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverRetryFlakyVerifyTests
{
    /// <summary>
    /// A transient first-run failure that clears on retry must produce a green
    /// verify — the task commits without entering the fix-verify loop.
    /// </summary>
    [Fact]
    public async Task RetryFlakyVerify_TransientFailThenPass_CommitsGreen()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 0);
        repo.WriteTask("transient", "# Transient failure\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "transient fault"),   // stage 9 verify — first run fails
            new TestRunResult(0, "green"));            // stage 9 verify — retry passes
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "transient");

        // Must commit — the retry turned a transient failure green.
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "transient", "NEEDS-REVIEW")));

        // Retry event must have fired.
        Assert.Contains(sink.Events, e => e.EventName == "verify_retry" && e.Level == "warn");
        // Fail→pass flip event must exist.
        Assert.Contains(sink.Events, e => e.EventName == "verify_retry_pass" && e.Level == "info");

        // No stage-10 LLM invocation (green verify skips the fix-verify loop).
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 10);
    }

    /// <summary>
    /// A persistent failure (both first run and retry fail) must still block —
    /// the task flags as before.
    /// </summary>
    [Fact]
    public async Task RetryFlakyVerify_PersistentFail_Flags()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 0);
        repo.WriteTask("persistent", "# Persistent failure\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"));     // stage 9 verify — retry also fails
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "persistent");

        // Must flag — both runs failed.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "persistent", "NEEDS-REVIEW")));

        // Retry event must have fired.
        Assert.Contains(sink.Events, e => e.EventName == "verify_retry" && e.Level == "warn");
        // No fail→pass flip — the retry also failed.
        Assert.DoesNotContain(sink.Events, e => e.EventName == "verify_retry_pass");
    }

    /// <summary>
    /// When retryFlakyVerify is explicitly false, the verify runs exactly once
    /// with no retry — byte-for-byte unchanged from the single-run path.
    /// </summary>
    [Fact]
    public async Task RetryFlakyVerify_False_NoRetry_SingleRun()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 0,
              "retryFlakyVerify": false
            }
            """);
        repo.WriteTask("no-retry", "# No retry allowed\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new RecordingTestRunner(
            new TestRunResult(1, "red"),         // stage 5 author gate
            new TestRunResult(1, "Failed TestX")); // stage 9 verify — fail, no retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-retry");

        // Must flag — single run failed, no retry attempted.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);

        // No retry events at all.
        Assert.DoesNotContain(sink.Events, e => e.EventName == "verify_retry");
        Assert.DoesNotContain(sink.Events, e => e.EventName == "verify_retry_pass");

        // Exactly two test-runner calls: stage 5 author gate + stage 9 verify.
        Assert.Equal(2, tests.Calls.Count);
    }
}
