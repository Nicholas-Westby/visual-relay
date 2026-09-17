using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The baseline verify subtracts the failures the base also has, so it must name a failing test the
/// same way in both runs, and never mistake a message for a test. It read only lines starting
/// "Failed ": dotnet's id kept its duration ("[4 ms]" in one run, "[6 ms]" in the next), PHPUnit's
/// "Failed asserting that two strings are identical." became an id shared by every such failure,
/// and pytest, go, cargo and the rest named nothing, so their red runs flagged "verify failed"
/// whatever the base held. The PHPUnit and duration lines are real output from the Windows arm.
/// </summary>
public sealed class RelayDriverBaselineFailureIdTests
{
    [Fact]
    public async Task BaselineVerify_DotnetFailureWithADifferentDuration_IsStillPreExisting()
    {
        using var repo = TestRepository.Create();
        var (outcome, _) = await RunAsync(repo,
            "  Failed LiteDB.Tests.Engine.Index_Tests.Index_With_Like_Trailing_Underscore [6 ms]\n",
            "  Failed LiteDB.Tests.Engine.Index_Tests.Index_With_Like_Trailing_Underscore [4 ms]\n");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task BaselineVerify_PhpunitFailureSharingOnlyAMessageWithTheBase_IsNew()
    {
        using var repo = TestRepository.Create();
        var (outcome, _) = await RunAsync(repo,
            "There were 2 failures:\n"
            + "1) LibRssTest::testEscapeToUnicodeAlternative with data set #0 ('It', true, 'It')\n"
            + "Failed asserting that two strings are identical.\n"
            + "2) LibRssTest::testNewlyBroken\n"
            + "Failed asserting that two strings are identical.\n",
            "There was 1 failure:\n"
            + "1) LibRssTest::testEscapeToUnicodeAlternative with data set #0 ('It', true, 'It')\n"
            + "Failed asserting that two strings are identical.\n");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Equal("new test failures: LibRssTest::testNewlyBroken", outcome.Reason);
    }

    /// <summary>Real go output: package b stopped compiling while a's old failure stayed.</summary>
    [Fact]
    public async Task BaselineVerify_APackageThatNoLongerBuilds_IsNewThoughTheOldFailureRemains()
    {
        using var repo = TestRepository.Create();
        var (outcome, _) = await RunAsync(repo,
            "# example.com/vrids/b [example.com/vrids/b.test]\nb/b_test.go:6:18: undefined: nope\n"
            + "--- FAIL: TestAddWrongly (0.00s)\n    a_test.go:7: got 3\nFAIL\n"
            + "FAIL\texample.com/vrids/a\t0.004s\nFAIL\texample.com/vrids/b [build failed]\nFAIL\n",
            "--- FAIL: TestAddWrongly (0.00s)\n    a_test.go:7: got 3\nFAIL\n"
            + "FAIL\texample.com/vrids/a\t0.004s\nok  \texample.com/vrids/b\t0.004s\nFAIL\n");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Equal("new test failures: example.com/vrids/b [build failed]", outcome.Reason);
    }

    [Fact]
    public async Task BaselineVerify_PytestFailureTheBaseAlsoHas_IsPreExisting()
    {
        using var repo = TestRepository.Create();
        var (outcome, _) = await RunAsync(repo,
            "FAILED test_sample.py::test_param[a b] - AssertionError: assert 'a b' == 'zz'\n",
            "FAILED test_sample.py::test_param[a b] - AssertionError: assert 'a b' == 'zz'\n");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    [Fact]
    public async Task BaselineVerify_RedRunThatNamesNoFailure_FlagsWithoutRunningTheBase()
    {
        using var repo = TestRepository.Create();
        var (outcome, tests) = await RunAsync(repo, "error: something went wrong\n", "never read\n");

        Assert.Equal("verify failed", outcome.Reason);
        Assert.Equal(3, tests.Calls.Count); // author gate, verify, retry: no base run
    }

    /// <summary>
    /// The operating system refused before the suite started: on i18next vitest could not create
    /// a file under the snapshot's node_modules, exited 1 and named no test. The bare "verify
    /// failed" sent Fix-verify looking for a test to repair, and it changed the checkout's modes.
    /// </summary>
    [Fact]
    public async Task BaselineVerify_RunRefusedByTheOperatingSystem_NamesTheRefusalInstead()
    {
        using var repo = TestRepository.Create();
        var (outcome, tests) = await RunAsync(repo,
            "failed to load config\nError: EACCES: permission denied, mkdir '/wt/node_modules/.vite-temp'\n",
            "never read\n");

        Assert.Equal(
            "the run failed before any test started: "
            + "Error: EACCES: permission denied, mkdir '/wt/node_modules/.vite-temp'",
            outcome.Reason);
        Assert.Equal(3, tests.Calls.Count); // author gate, verify, retry: no base run
    }

    [Fact]
    public async Task BaselineVerify_KeepsTheBaseOutputAndSaysWhatItSubtracted()
    {
        using var repo = TestRepository.Create();
        var events = new InMemoryRelayEventSink();

        await RunAsync(repo,
            "  Failed Demo.OldTest [1 ms]\n  Failed Demo.NewTest [1 ms]\n",
            "  Failed Demo.OldTest [2 ms]\nBASE-OUTPUT-MARKER\n", events);

        var baseline = Assert.Single(events.Events, e => e.EventName == "verify_baseline");
        Assert.Equal("Demo.NewTest", baseline.Data!["newFailures"]);
        Assert.Equal("1", baseline.Data["preExisting"]);
        var outputFile = Path.Combine(repo.Root, ".relay", "baseline-ids", "stage10-attempt1.baseline-output.txt");
        Assert.Equal(outputFile, baseline.Data["outputFile"]);
        Assert.Contains("BASE-OUTPUT-MARKER", await File.ReadAllTextAsync(outputFile, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a task whose Verify is red with <paramref name="verifyOutput"/> (twice, with the retry)
    /// and whose base run prints <paramref name="baseOutput"/>.
    /// </summary>
    private static async Task<(RelayTaskOutcome Outcome, RecordingTestRunner Tests)> RunAsync(
        TestRepository repo, string verifyOutput, string baseOutput, InMemoryRelayEventSink? events = null)
    {
        repo.WriteConfig("full-suite", [], baselineVerify: true, enableFixVerify: false);
        repo.WriteTask("baseline-ids", "# Baseline ids\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        var tests = new RecordingTestRunner(
            new TestRunResult(1, "red"),            // stage 5 author gate
            new TestRunResult(1, verifyOutput),     // stage 10 verify
            new TestRunResult(1, verifyOutput),     // stage 10 retry
            new TestRunResult(1, baseOutput));      // the base
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(new PrematureImplementationRunner(), tests, events ?? new InMemoryRelayEventSink(), sim),
            RelayDriverOptions.NoGitCommit);

        return (await driver.RunTaskAsync(repo.Root, "baseline-ids", TestContext.Current.CancellationToken), tests);
    }
}
