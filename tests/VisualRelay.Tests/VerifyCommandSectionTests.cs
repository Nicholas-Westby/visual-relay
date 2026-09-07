using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Every stage whose system prompt points at the <c>## Verify command</c> section must
/// actually receive that section. Before this, only the two coding stages were handed a
/// command, so Author-tests, Review and Fix-verify readers were told to use a section
/// their prompt did not contain and improvised — up to running the whole suite twice.
/// </summary>
public sealed class VerifyCommandSectionTests
{
    /// <summary>Stages whose system prompt names the section.</summary>
    public static TheoryData<int> VerifyCommandStages => [5, 6, 7, 9];

    private static (CapturingSubagentRunner Runner, RelayTaskOutcome Outcome) RunHappyPath(
        TestRepository repo, string? testFileCmd)
    {
        repo.WriteConfig("dotnet test", [], baselineVerify: false, testFileCmd: testFileCmd);
        repo.WriteTask("verify-command", "# Verify command section\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        runner.SeedReviewChanges(); // keeps Fix (9) in the run instead of skipping it
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),      // stage 5 author gate — correctly red
            new TestRunResult(0, "green"));   // stage 10 verify gate
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink(), new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = driver.RunTaskAsync(repo.Root, "verify-command").GetAwaiter().GetResult();
        return (runner, outcome);
    }

    [Theory]
    [MemberData(nameof(VerifyCommandStages))]
    public void Stage_WithFilesToken_RendersNarrowedVerifyCommand(int stageNumber)
    {
        using var repo = TestRepository.Create();

        var (runner, outcome) = RunHappyPath(repo, testFileCmd: "dotnet test --filter {files}");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var prompt = SandboxedStage.BuildPrompt(
            runner.Invocations.Last(i => i.Stage.Number == stageNumber));
        Assert.Contains("## Verify command", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter tests/app.tests.cs", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(SandboxedStage.FullSuiteIsTargetedNotice, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(VerifyCommandStages))]
    public void Stage_WithoutFilesToken_RendersFullSuiteAndSaysSo(int stageNumber)
    {
        using var repo = TestRepository.Create();

        var (runner, outcome) = RunHappyPath(repo, testFileCmd: null);

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var prompt = SandboxedStage.BuildPrompt(
            runner.Invocations.Last(i => i.Stage.Number == stageNumber));
        Assert.Contains("## Verify command", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test", prompt, StringComparison.Ordinal);
        Assert.Contains(SandboxedStage.FullSuiteIsTargetedNotice, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage11_FixVerify_RendersTheFullGateWithoutTheFallbackNotice()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true,
            testFileCmd: "dotnet test --filter {files}");
        repo.WriteTask("fixverify-section", "# Fix-verify sees the verify command\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),           // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),  // stage 10 verify run
            new TestRunResult(1, "Failed TestX"),  // stage 10 verify retry
            new TestRunResult(1, "Failed TestX"),  // fix-verify attempt 1 gate
            new TestRunResult(0, "green"));        // fix-verify attempt 1 retry → green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink(), new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fixverify-section");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var prompt = SandboxedStage.BuildPrompt(
            runner.Invocations.Single(i => i.Stage.Number == 11));
        Assert.Contains("## Verify command", prompt, StringComparison.Ordinal);
        Assert.Contains("dotnet test", prompt, StringComparison.Ordinal);
        // Stage 11 is handed the full gate deliberately; the fallback notice would
        // misdescribe a project that DOES have a per-file form.
        Assert.DoesNotContain(SandboxedStage.FullSuiteIsTargetedNotice, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage10_Verify_StillGetsNoVerifyCommandSection()
    {
        using var repo = TestRepository.Create();

        var (runner, _) = RunHappyPath(repo, testFileCmd: "dotnet test --filter {files}");

        var prompt = SandboxedStage.BuildPrompt(
            runner.Invocations.Single(i => i.Stage.Number == 10));
        Assert.DoesNotContain("## Verify command", prompt, StringComparison.Ordinal);
    }
}
