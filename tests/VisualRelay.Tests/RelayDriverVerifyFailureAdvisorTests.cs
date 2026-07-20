using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the identical-failure advisory (text and tree triggers) extracted from
/// <see cref="RelayDriverBaselineVerifyTests"/> to stay under the 300-line guard.
/// </summary>
public sealed class RelayDriverVerifyFailureAdvisorTests
{
    // ── NormalizeVerifySignature unit tests ────────────────────────────────────

    [Fact]
    public void NormalizeVerifySignature_MasksDigitRuns()
    {
        var a = RelayDriver.NormalizeVerifySignature("failed after 0.016 seconds with 1 issue");
        var b = RelayDriver.NormalizeVerifySignature("failed after 0.019 seconds with 1 issue");
        Assert.Equal(a, b);
    }

    [Fact]
    public void NormalizeVerifySignature_DifferentTestNames_StayDistinct()
    {
        var a = RelayDriver.NormalizeVerifySignature("Failed FooTest after 0.1 seconds");
        var b = RelayDriver.NormalizeVerifySignature("Failed BarTest after 0.1 seconds");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NormalizeVerifySignature_StripsIsoTimestamp()
    {
        var a = RelayDriver.NormalizeVerifySignature("2025-01-15T10:30:00Z failed TestX");
        Assert.DoesNotContain("2025", a, StringComparison.Ordinal);
        Assert.Contains("failed TestX", a, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeVerifySignature_StripsOutputFile()
    {
        var a = RelayDriver.NormalizeVerifySignature(
            "failed FooTest \"outputFile\": \"/tmp/stuff.txt\" done");
        Assert.DoesNotContain("outputFile", a, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/stuff.txt", a, StringComparison.Ordinal);
        Assert.Contains("failed FooTest", a, StringComparison.Ordinal);
        Assert.Contains("done", a, StringComparison.Ordinal);
    }

    // ── Tree-trigger incident replay ──────────────────────────────────────────

    [Fact]
    public async Task VerifyFixLoop_IdenticalTreeHashDifferentText_FiresTreeTrigger()
    {
        const string MochaNotFound = "sh: line 1: mocha: command not found\n";
        using var repo = TestRepository.Create();
        repo.WriteConfig("npm test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("tree-trigger", "# Tree trigger\n");
        // ScriptedSubagentRunner writes no files → equal tree hashes.
        // Test outputs differ in the test name, so text normalization won't equalize them.
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5
            new TestRunResult(1, "Failed FooTest"),                         // stage 10 first
            new TestRunResult(1, "Failed FooTest"),                         // stage 10 retry
            new TestRunResult(1, "Failed BarTest"),                         // fv-1 gate
            new TestRunResult(1, "Failed BarTest"),                         // fv-1 retry
            new TestRunResult(1, "Failed BazTest"),                         // fv-2 gate
            new TestRunResult(1, "Failed BazTest"),                         // fv-2 retry
            new TestRunResult(1, MochaNotFound),                            // fv-3 gate
            new TestRunResult(1, MochaNotFound));                           // fv-3 retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "tree-trigger");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("tree unchanged across all attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("identical failure across all attempts", outcome.Reason, StringComparison.Ordinal);

        var warn = sink.Events.SingleOrDefault(e => e.EventName == "verify_identical_failures");
        Assert.NotNull(warn);
        Assert.Equal("tree", warn!.Data!["trigger"]);
    }

    // ── Text-trigger with digit variants ─────────────────────────────────────

    [Fact]
    public async Task VerifyFixLoop_DigitVariantReasonsDistinctTrees_FiresTextTrigger()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("npm test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("text-trigger", "# Text trigger\n");
        // WriteEachAttemptSubagentRunner writes distinct content → tree hashes differ.
        var runner = new WriteEachAttemptSubagentRunner(repo.Root, "src/app.cs");
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Test outputs differ ONLY in digit runs — after digit masking they normalize equal.
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5
            new TestRunResult(1, "Failed after 0.016 seconds with 1 issue"), // stage 10 first
            new TestRunResult(1, "Failed after 0.016 seconds with 1 issue"), // stage 10 retry
            new TestRunResult(1, "Failed after 0.019 seconds with 1 issue"), // fv-1 gate
            new TestRunResult(1, "Failed after 0.019 seconds with 1 issue"), // fv-1 retry
            new TestRunResult(1, "Failed after 0.022 seconds with 1 issue"), // fv-2 gate
            new TestRunResult(1, "Failed after 0.022 seconds with 1 issue"), // fv-2 retry
            new TestRunResult(1, "Failed after 0.025 seconds with 1 issue"), // fv-3 gate
            new TestRunResult(1, "Failed after 0.025 seconds with 1 issue"));// fv-3 retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "text-trigger");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("identical failure across all attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("tree unchanged across all attempts", outcome.Reason, StringComparison.Ordinal);

        var warn = sink.Events.SingleOrDefault(e => e.EventName == "verify_identical_failures");
        Assert.NotNull(warn);
        Assert.Equal("text", warn!.Data!["trigger"]);
    }

    // ── Negative: different failures + different trees → silent ──────────────

    [Fact]
    public async Task VerifyFixLoop_DifferentFailuresDifferentTrees_NoAdvisory()
    {
        const string MochaNotFound = "sh: line 1: mocha: command not found\n";
        using var repo = TestRepository.Create();
        repo.WriteConfig("npm test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("no-advisory", "# No advisory\n");
        // WriteEachAttemptSubagentRunner → different tree hashes.
        var runner = new WriteEachAttemptSubagentRunner(repo.Root, "src/app.cs");
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Different test names AND different tree hashes → both triggers stay silent.
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                                    // stage 5
            new TestRunResult(1, "Failed AlphaTest"),                       // stage 10 first
            new TestRunResult(1, "Failed AlphaTest"),                       // stage 10 retry
            new TestRunResult(1, "Failed BetaTest"),                        // fv-1 gate
            new TestRunResult(1, "Failed BetaTest"),                        // fv-1 retry
            new TestRunResult(1, "Failed GammaTest"),                       // fv-2 gate
            new TestRunResult(1, "Failed GammaTest"),                       // fv-2 retry
            new TestRunResult(1, MochaNotFound),                            // fv-3 gate
            new TestRunResult(1, MochaNotFound));                           // fv-3 retry
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-advisory");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.DoesNotContain("identical failure across all attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("tree unchanged across all attempts", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "verify_identical_failures");
    }
}
