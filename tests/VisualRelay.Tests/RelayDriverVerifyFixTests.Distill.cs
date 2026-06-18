using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunVerifyFixLoop_FailureOutputShownToAgent_HasNonoNoiseStripped()
    {
        // The agent's LastTestOutput must have nono advisory noise (blocked-by lines,
        // bare deny_*, "Verified N pack(s)") stripped — only the real failure survives.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("noise-strip", "# Noise strip test\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        const string nonoNoise =
            "deny_read_user_home\n" +
            "'/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access\n" +
            "Verified 1 pack(s)\n";
        const string realFailure = "Failed ImportantTest — expected 42 but got 0";
        var rawOutput = nonoNoise + realFailure;
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                       // stage 5 author gate
            new TestRunResult(1, rawOutput),                   // stage 9 verify — first run fails with noise
            new TestRunResult(1, rawOutput),                   // stage 9 verify — retry also fails
            new TestRunResult(1, rawOutput),                   // fix-verify attempt 1 gate — red
            new TestRunResult(0, "green"));                    // fix-verify attempt 1 retry — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "noise-strip");

        var stage10Invocation = runner.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
        Assert.NotNull(stage10Invocation);
        var lastOutput = stage10Invocation!.LastTestOutput ?? "";
        Assert.DoesNotContain("is blocked by", lastOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--bypass-protection", lastOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("deny_read_user_home", lastOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Verified 1 pack(s)", lastOutput, StringComparison.Ordinal);
        Assert.Contains("Failed ImportantTest", lastOutput, StringComparison.Ordinal);
    }
}
