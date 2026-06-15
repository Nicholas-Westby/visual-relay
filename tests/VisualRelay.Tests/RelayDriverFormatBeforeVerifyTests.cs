using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests that <c>formatCmd</c> runs before <c>guardCmd</c> at both Verify
/// (stage 9) and each fix-verify re-verify (stage 10), eliminating false
/// format-only guard failures from the Fix-verify loop.
/// </summary>
public sealed class RelayDriverFormatBeforeVerifyTests
{
    /// <summary>
    /// (a.1) formatCmd is set, guard passes on first call after the formatter
    /// fires.  Assert formatter ran before guard, stage 10 was never entered,
    /// and the outcome is Committed.
    /// </summary>
    [Fact]
    public async Task FormatCmd_Set_RunsBeforeGuardAtVerifyAndNoFixVerifyEntered()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1,
              "archiveOnDone": true,
              "guardCmd": "my-guard",
              "formatCmd": "my-formatter"
            }
            """);
        repo.WriteTask("fmt-green", "# Format then guard green\n");

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var testRunner = new DispatchRecordingTestRunner(
            ("my-formatter", [new TestRunResult(0, "")]),
            ("my-guard", [new TestRunResult(0, "guard clean")]),
            ("dotnet test",
            [
                new TestRunResult(1, "red"),
                new TestRunResult(0, "all green")
            ]));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fmt-green");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Formatter must run before guard.
        var calls = testRunner.Calls.ToList();
        var fmtIdx = calls.FindIndex(c => c.Command.Contains("my-formatter", StringComparison.Ordinal));
        var guardIdx = calls.FindIndex(c => c.Command.Contains("my-guard", StringComparison.Ordinal));
        Assert.True(fmtIdx >= 0, "formatter was never called");
        Assert.True(guardIdx >= 0, "guard was never called");
        Assert.True(fmtIdx < guardIdx, "formatter must run before guard");

        // Verify was green on the first try, so the Fix-verify (stage 10) LLM
        // call is skipped entirely — there must be no stage-10 subagent invocation.
        Assert.DoesNotContain(subagent.Invocations, i => i.Stage.Number == 10);
    }

    /// <summary>
    /// (a.2) Guard returns red on the first call (stage 9), then green on the
    /// fix-verify re-verify (stage 10).  The formatter fires before every guard
    /// call — twice total.  Stage 10 commits.
    /// </summary>
    [Fact]
    public async Task FormatCmd_Set_RunsBeforeGuardInFixVerifyIteration()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 2,
              "archiveOnDone": true,
              "guardCmd": "my-guard",
              "formatCmd": "my-formatter"
            }
            """);
        repo.WriteTask("fmt-fix", "# Format then fix-verify\n");

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var testRunner = new DispatchRecordingTestRunner(
            ("my-formatter",
            [
                new TestRunResult(0, ""),
                new TestRunResult(0, "")
            ]),
            ("my-guard",
            [
                new TestRunResult(1, "ERROR: src/big.cs is 301 lines (limit: 300)"),
                new TestRunResult(0, "guard clean")
            ]),
            ("dotnet test",
            [
                new TestRunResult(1, "red"),
                new TestRunResult(0, "all green"),
                new TestRunResult(0, "all green")
            ]));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fmt-fix");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Two formatter calls: one before stage-9 guard, one before
        // fix-verify re-verify guard.
        var fmtCalls = testRunner.Calls.Count(c => c.Command.Contains("my-formatter", StringComparison.Ordinal));
        var guardCalls = testRunner.Calls.Count(c => c.Command.Contains("my-guard", StringComparison.Ordinal));
        Assert.Equal(2, fmtCalls);
        Assert.Equal(2, guardCalls);

        // Formatter must run before guard in each pair.
        for (var i = 0; i < testRunner.Calls.Count - 1; i++)
        {
            if (testRunner.Calls[i].Command.Contains("my-formatter", StringComparison.Ordinal))
            {
                var nextGuard = testRunner.Calls.Skip(i + 1)
                    .FirstOrDefault(c => c.Command.Contains("my-guard", StringComparison.Ordinal));
                Assert.NotEqual(default, nextGuard);
            }
        }

        // Stage 10 was entered and committed.
        var stage10 = subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
        Assert.NotNull(stage10);
        Assert.NotNull(stage10!.LastTestOutput);
        Assert.Contains("big.cs", stage10.LastTestOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// (a.3) formatCmd is absent from config.  No formatter runs, and the
    /// driver behaves identically to the existing guard tests (green guard
    /// → committed, no stage 10).
    /// </summary>
    [Fact]
    public async Task FormatCmd_Unset_NeitherFormatterNorBehaviorChanges()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 0,
              "archiveOnDone": true,
              "guardCmd": "my-guard"
            }
            """);
        repo.WriteTask("no-fmt", "# No format command\n");

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        var testRunner = new DispatchRecordingTestRunner(
            ("my-guard", [new TestRunResult(0, "guard clean")]),
            ("dotnet test",
            [
                new TestRunResult(1, "red"),
                new TestRunResult(0, "all green")
            ]));

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-fmt");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // No formatter command in any call.
        Assert.DoesNotContain(testRunner.Calls,
            c => c.Command.Contains("my-formatter", StringComparison.Ordinal));
        Assert.DoesNotContain(testRunner.Calls,
            c => c.Command.Contains("format", StringComparison.Ordinal));

        // Guard was still called (existing behavior unchanged).
        Assert.Contains(testRunner.Calls,
            c => c.Command.Contains("my-guard", StringComparison.Ordinal));

        // Verify was green on the first try, so the Fix-verify (stage 10) LLM
        // call is skipped entirely — there must be no stage-10 subagent invocation.
        Assert.DoesNotContain(subagent.Invocations, i => i.Stage.Number == 10);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches <see cref="ITestRunner.RunAsync"/> to separate queues of
    /// <see cref="TestRunResult"/> based on command sentinel matching, and
    /// records every call for assertion.
    /// </summary>
    private sealed class DispatchRecordingTestRunner : ITestRunner
    {
        private readonly List<(string RootPath, string Command)> _calls = [];
        private readonly Dictionary<string, Queue<TestRunResult>> _queues;

        public DispatchRecordingTestRunner(
            params (string Sentinel, TestRunResult[] Results)[] routes)
        {
            _queues = routes.ToDictionary(
                r => r.Sentinel,
                r => new Queue<TestRunResult>(r.Results));
        }

        public IReadOnlyList<(string RootPath, string Command)> Calls => _calls;

        public Task<TestRunResult> RunAsync(
            string rootPath, string command, CancellationToken cancellationToken = default)
        {
            _calls.Add((rootPath, command));
            foreach (var (sentinel, queue) in _queues)
            {
                if (command.Contains(sentinel, StringComparison.Ordinal) && queue.Count > 0)
                    return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(new TestRunResult(0, string.Empty));
        }
    }
}
