using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the repo-guard gate in stage 9.  A configurable <c>guardCmd</c>
/// runs alongside the test command, its output is baselined and diffed, and
/// new violation lines enter the existing fix-verify loop.
/// </summary>
public sealed class RelayDriverRepoGuardTests
{
    /// <summary>
    /// (a) Guard fails with violations that are NOT all present in the
    /// baseline: the new violation lines must turn stage 9 red and the
    /// guard output must enter the fix-verify loop so the stage-10 agent
    /// can remediate.  Uses <c>baselineVerify: false</c> so every guard
    /// failure is treated as new and no stash round-trip is needed; the
    /// guard-aware runner produces the right outputs per command.
    ///
    /// Without implementation FAILS: guardCmd is ignored → the non-guard
    /// runner returns green for the stage-9 test → driver commits
    /// immediately → zero stage-10 invocations.
    /// </summary>
    [Fact]
    public async Task GuardRed_NewViolations_EntersFixVerifyWithOutput()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": true,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("big-file", "# Add oversized file\n");

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "ERROR: src/big.cs is 301 lines (limit: 300)\nERROR: tests/new-file.cs is 305 lines (limit: 300)"),
            new TestRunResult(0, "guard clean"));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "all green"),
            new TestRunResult(0, "all green"));
        var combined = new CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner),
            ("dotnet test", testRunner));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "big-file");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stage10 = subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
        Assert.NotNull(stage10);
        Assert.NotNull(stage10!.LastTestOutput);
        Assert.Contains("new-file.cs", stage10.LastTestOutput, StringComparison.Ordinal);

        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "big-file", "big-file.seals"));
        Assert.Contains(seals, line =>
            line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line =>
            line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// (b) Guard fails but all violation lines are present in the baseline:
    /// commit proceeds (pre-existing debt doesn't block) and the ledger
    /// records a note about pre-existing guard violations.
    ///
    /// Without implementation FAILS: the ledger contains no guard note.
    /// </summary>
    [Fact]
    public async Task GuardRed_PreExistingOnly_CommitsWithLedgerNote()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": true,
              "enableFixVerify": false,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("old-debt", "# Old debt\n");
        InitGitRepo(repo.Root);

        var subagent = new ScriptedSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        // Guard working tree and baseline return the same output.
        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "ERROR: src/big.cs is 301 lines (limit: 300)"),
            new TestRunResult(1, "ERROR: src/big.cs is 301 lines (limit: 300)"));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "all green"));
        var combined = new CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner),
            ("dotnet test", testRunner));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "old-debt");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var ledger = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "old-debt", "ledger.md"));
        Assert.Contains("guard violation", ledger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pre-existing", ledger, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// (c) <c>guardCmd</c> absent from config: zero guard invocations,
    /// zero overhead. The test-runner call count stays at exactly 2
    /// (stage 5 author gate + stage 9 verify).
    ///
    /// Without implementation this test PASSES — it guards the zero-cost
    /// property after implementation.
    /// </summary>
    [Fact]
    public async Task NoGuardCmd_NoGuardInvocation()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("no-guard", "# No guard\n");

        var subagent = new ScriptedSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-guard");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Equal(2, testRunner.Calls.Count);
        Assert.DoesNotContain(testRunner.Calls, c => c.Command.Contains("guard"));
    }

    /// <summary>
    /// (d) Guard fails (new violations), fix-verify agent remediates,
    /// guard re-check turns green, commit seals.
    ///
    /// Without implementation FAILS: guardCmd ignored → test passes green →
    /// driver commits without fix-verify → zero stage-10 invocations.
    /// </summary>
    [Fact]
    public async Task GuardFixedInFixVerify_SealsGreen()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": true,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("fix-guard", "# Fix guard violation\n");

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "ERROR: src/oversized.cs is 312 lines (limit: 300)"),
            new TestRunResult(0, "guard clean"));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "all green"),
            new TestRunResult(0, "all green"));
        var combined = new CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner),
            ("dotnet test", testRunner));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fix-guard");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var stage10 = subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
        Assert.NotNull(stage10);
        Assert.NotNull(stage10!.LastTestOutput);
        Assert.Contains("oversized.cs", stage10.LastTestOutput, StringComparison.Ordinal);

        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "fix-guard", "fix-guard.seals"));
        Assert.Contains(seals, line =>
            line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line =>
            line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    internal static void InitGitRepo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "status.cs"), "old\n");
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", "chore: seed repo");
    }

    // ReSharper disable once InvalidXmlDocComment — cref ambiguities acceptable in test helper docs
    /// <summary>
    /// Dispatches <see cref="ITestRunner.RunAsync"/> to one of several
    /// inner runners based on whether the command string contains a
    /// registered sentinel substring.  The first matching sentinel wins;
    /// if no sentinel matches the call is forwarded to the first
    /// registered runner whose sentinel is <c>"*"</c> (the default).
    /// Each inner runner is typically a <see cref="ScriptedTestRunner"/>
    /// with its own independent queue.
    /// </summary>
    internal sealed class CommandDispatchTestRunner(
        params (string Sentinel, ITestRunner Runner)[] routes) : ITestRunner
    {
        private readonly List<(string Sentinel, ITestRunner Runner)> _routes = [.. routes];

        public Task<TestRunResult> RunAsync(
            string rootPath, string command, CancellationToken cancellationToken = default)
        {
            foreach (var (sentinel, runner) in _routes)
            {
                if (sentinel == "*" || command.Contains(sentinel, StringComparison.Ordinal))
                    return runner.RunAsync(rootPath, command, cancellationToken);
            }

            return Task.FromResult(new TestRunResult(0, string.Empty));
        }
    }
}
