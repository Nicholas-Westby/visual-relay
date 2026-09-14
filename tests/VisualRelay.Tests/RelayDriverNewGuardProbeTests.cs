using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverNewGuardProbeTests
{
    private static async Task<TestRepository> Setup(string taskId, string taskPrompt,
        string? extraConfig = null, bool enableFixVerify = false, bool createGuardScript = false)
    {
        var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        if (createGuardScript)
        {
            Directory.CreateDirectory(Path.Combine(repo.Root, "tools", "guards"));
            await File.WriteAllTextAsync(Path.Combine(repo.Root, "tools", "guards", "new.sh"), "#!/bin/sh\nexit 0\n");
        }
        var config = $$"""
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": {{enableFixVerify.ToString().ToLowerInvariant()}},
              "archiveOnDone": true{{extraConfig ?? ""}}
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), config);
        repo.WriteTask(taskId, taskPrompt);
        return repo;
    }

    [Fact]
    public async Task Stage9_NoMatchingGuardsInManifest_VerifyProceeds()
    {
        using var repo = await Setup("no-guards", "# No new guards\n");
        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"), new TestRunResult(0, "all green"));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-guards");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(2, testRunner.Calls.Count);
        Assert.DoesNotContain(testRunner.Calls, c => c.Command.Contains("tools/guards"));
    }

    [Fact]
    public async Task Stage10_NewGuardPassesProbe_VerifySucceeds()
    {
        using var repo = await Setup("guard-ok", "# Guard passes probe\n", createGuardScript: true);
        var subagent = new CapturingGuardedManifestSubagentRunner();
        var testRunner = new RecordingDispatchTestRunner(
            ("tools/guards/new.sh", [new TestRunResult(0, "guard clean")]),
            ("dotnet test", [new TestRunResult(1, "red"), new TestRunResult(0, "all green")]));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "guard-ok");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain(subagent.Invocations, i => i.Stage.Number == 11);
        Assert.Single(testRunner.Calls, c => c.Command.Contains("tools/guards/new.sh"));
    }

    /// <summary>
    /// The probe runs a new guard by its path in the repository, which the test runner's shell
    /// resolves from the repository root. The host's absolute path is not one the sandbox's
    /// shell can run on the Windows arm: there it is a <c>\\wsl.localhost</c> or <c>C:\</c> path
    /// handed to <c>/bin/sh</c> inside the distro, so every new-guard probe failed.
    /// </summary>
    [Fact]
    public async Task Stage10_NewGuardProbe_RunsTheScriptByItsPathInTheRepository()
    {
        using var repo = await Setup("guard-relative", "# Guard runs by relative path\n", createGuardScript: true);
        var testRunner = new RecordingDispatchTestRunner(
            ("tools/guards/new.sh", [new TestRunResult(0, "guard clean")]),
            ("dotnet test", [new TestRunResult(1, "red"), new TestRunResult(0, "all green")]));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new CapturingGuardedManifestSubagentRunner(), testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "guard-relative");

        Assert.Single(testRunner.Calls, c => c.Command == "tools/guards/new.sh");
    }

    [Fact]
    public async Task Stage10_NewGuardFailsProbe_EntersFixVerifyLoop()
    {
        using var repo = await Setup("guard-fail", "# Guard fails probe\n", enableFixVerify: true, createGuardScript: true);
        var subagent = new CapturingGuardedManifestSubagentRunner();
        var testRunner = new RecordingDispatchTestRunner(
            ("tools/guards/new.sh", [new TestRunResult(1, "ERROR: src/big.cs is 301 lines (limit: 300)")]),
            ("dotnet test", [new TestRunResult(1, "red"), new TestRunResult(0, "all green"),
                new TestRunResult(0, "all green")]));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "guard-fail");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var fixVerifyStage = subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 11);
        Assert.NotNull(fixVerifyStage);
        Assert.NotNull(fixVerifyStage!.LastTestOutput);
        Assert.Contains("301 lines", fixVerifyStage.LastTestOutput, StringComparison.Ordinal);
        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "guard-fail", "guard-fail.seals"));
        Assert.Contains(seals, line =>
            line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line =>
            line.Contains("\"n\":11", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage9_EmptyNewGuardPatterns_NeverProbes()
    {
        using var repo = await Setup("disabled", "# Disabled probe\n",
            extraConfig: ",\n              \"newGuardPatterns\": []");
        var subagent = new CapturingGuardedManifestSubagentRunner();
        var testRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"), new TestRunResult(0, "all green"));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "disabled");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(2, testRunner.Calls.Count);
        Assert.DoesNotContain(testRunner.Calls, c => c.Command.Contains("tools/guards"));
    }

    [Fact]
    public async Task Stage9_NewGuardTimesOut_Flags()
    {
        using var repo = await Setup("guard-timeout", "# Guard times out\n", createGuardScript: true);
        var subagent = new CapturingGuardedManifestSubagentRunner();
        var testRunner = new RecordingDispatchTestRunner(
            ("tools/guards/new.sh", [new TestRunResult(-1, "killed: timeout", TimedOut: true)]),
            ("dotnet test", [new TestRunResult(1, "red"), new TestRunResult(0, "all green")]));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "guard-timeout");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        // The reason is the failure's first line, so the header itself says it timed out.
        Assert.DoesNotContain('\n', outcome.Reason);
        Assert.Contains("timed out", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
