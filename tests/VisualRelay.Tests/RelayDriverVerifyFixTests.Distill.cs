using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunVerifyFixLoop_FailureOutputShownToAgent_HasNonoNoiseStripped()
    {
        // The agent's LastTestOutput must have nono advisory noise (blocked-by lines,
        // bare deny_*, "Verified N pack(s)", and the STANDING keychain/system-services
        // advisory nono prints AFTER the test summary) stripped — only the real failure
        // survives. The keychain block trails the summary on a real run, so an unfiltered
        // 600-char tail lands on it and truncates the real failure away.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("noise-strip", "# Noise strip test\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        const string nonoNoise =
            "deny_read_user_home\n" +
            "'/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access\n" +
            "Verified 1 pack(s)\n";
        const string realFailure =
            "Failed ImportantTest — expected 42 but got 0\n" +
            "Failed!  - Failed:     1, Passed:  1860, Skipped:     0, Total:  1861, Duration: 12 s\n";
        // nono's standing vr-guard advisory — printed AFTER the runner's summary on every
        // run (nothing actually requested the keychain). NOT the test failure.
        const string keychainAdvisory =
            "system services: mach-lookup (com.apple.SecurityServer) — Keychain / Security framework\n" +
            "Keychain access requires granting the login keychain path: --read-file ~/Library/Keychains/login.keychain-db\n" +
            "Next steps:\n" +
            "  Discover paths: nono learn -p vr-guard -- dotnet test\n" +
            "  Query policy:   nono why -p vr-guard --op read --path ~/Library/Keychains/login.keychain-db\n" +
            "  --allow ~/Library/Keychains/login.keychain-db\n" +
            "  --read-file ~/Library/Keychains/login.keychain-db\n" +
            "  nono learn -p vr-guard\n" +
            "  nono why -p vr-guard\n";
        var rawOutput = nonoNoise + realFailure + keychainAdvisory;
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
        // The runner's own failure (a "Failed <Test>" line AND the summary) must survive.
        Assert.Contains("Failed ImportantTest", lastOutput, StringComparison.Ordinal);
        Assert.Contains("Failed!", lastOutput, StringComparison.Ordinal);
        // None of nono's standing keychain/system-services advisory may leak through.
        Assert.DoesNotContain("mach-lookup", lastOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Keychain access requires", lastOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("login.keychain-db", lastOutput, StringComparison.Ordinal);
    }
}
