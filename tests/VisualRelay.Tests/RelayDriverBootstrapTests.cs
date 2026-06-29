using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverBootstrapTests
{
    /// <summary>
    /// Test (a): When the manifest includes a recognized bootstrap file
    /// (flake.nix), the driver must run a bootstrap smoke check before
    /// the normal test command. This test proves the bootstrap gate fires.
    ///
    /// Without the implementation this test FAILS because the
    /// RecordingTestRunner will see only 2 calls (stage 5 + stage 9 test)
    /// instead of the expected 3 (stage 5 + bootstrap + stage 9 test).
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_ManifestIncludesFlakeNix_RunsBootstrapCheck()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("add-nono", "# Add nono to flake\n");

        var subagent = new FlakeNixManifestSubagentRunner();
        var testRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"),       // stage 5 author gate — red (passes)
            new TestRunResult(0, "green"),     // stage 9 bootstrap check — green
            new TestRunResult(0, "green"));    // stage 9 verify test — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-nono");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Without bootstrap: 2 calls (stage 5 + stage 9 test).
        // With bootstrap: 3 calls (stage 5 + stage 9 bootstrap + stage 9 test).
        Assert.True(testRunner.Calls.Count >= 3,
            $"Expected at least 3 test-runner calls (stage 5 + bootstrap + stage 9), got {testRunner.Calls.Count}");

        // At least one call must be the bootstrap smoke command.
        Assert.Contains(testRunner.Calls, c => c.Command.Contains("nix develop"));
    }

    /// <summary>
    /// Test (b): When the manifest contains only regular source files
    /// (no bootstrap paths), the bootstrap check is skipped entirely —
    /// zero extra cost. The test-runner call count stays at 2 (stage 5
    /// author gate + stage 9 verify).
    ///
    /// Without the implementation this test PASSES (no bootstrap exists),
    /// serving as a baseline. After implementation it guards the zero-cost
    /// property.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_ManifestWithoutBootstrapFiles_SkipsBootstrapCheck()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("add-feature", "# Add feature\n");

        var subagent = new ScriptedSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var testRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"),       // stage 5 author gate — red (passes)
            new TestRunResult(0, "green"));    // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "add-feature");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Exactly 2 test-runner calls: stage 5 author gate + stage 9 verify.
        Assert.Equal(2, testRunner.Calls.Count);

        // No call should contain a bootstrap command.
        Assert.DoesNotContain(testRunner.Calls, c => c.Command.Contains("nix develop"));
    }

    /// <summary>
    /// Test (c): When the bootstrap smoke check fails but the normal test
    /// suite passes, the overall stage-9 check must be "red" (the bootstrap
    /// failure is not ignored) and the fix-verify loop must be entered.
    /// The bootstrap failure output must be fed into the stage-10 subagent
    /// invocation as <c>LastTestOutput</c> so the agent can remediate.
    ///
    /// Key invariant: a red bootstrap check is treated like a red test run
    /// — it enters the fix-verify loop, never silently commits.
    ///
    /// Uses a <see cref="CommandAwareTestRunner"/> that fails the first
    /// bootstrap call and passes everything else. Without the bootstrap-gate
    /// implementation the stage-9 test passes green, the driver commits
    /// immediately, and the assertion that stage 10 ran FAILS.
    ///
    /// With the implementation: stage 9 bootstrap fails → overall red →
    /// fix-verify → agent fixes bootstrap → bootstrap re-check passes →
    /// test re-verify passes → Committed.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_BootstrapFailsTestPasses_EntersFixVerifyWithBootstrapOutput()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("break-flake", "# Break flake\n");

        var capturingSubagent = new CapturingFlakeNixSubagentRunner();
        var testRunner = new CommandAwareTestRunner();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(capturingSubagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "break-flake");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The stage-10 fix-verify invocation must carry the bootstrap failure.
        var stage10 = capturingSubagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
        Assert.NotNull(stage10);
        Assert.NotNull(stage10!.LastTestOutput);
        Assert.Contains("nix build of nono failed", stage10.LastTestOutput, StringComparison.Ordinal);

        // Stage 9 must be recorded as red (bootstrap failed).
        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "break-flake", "break-flake.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        // Stage 10 must be green (fix succeeded).
        Assert.Contains(seals, line => line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"green\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fix #1 (complete log): the persisted verify-output file must be the FULL version of
    /// the trimmed tail handed to the fix-verify agent. When the failure is a BOOTSTRAP
    /// failure (the test command itself PASSES), the in-prompt tail carries the bootstrap
    /// text, so the file the prompt calls "the complete log" must contain it too — not
    /// merely the passing test output. Before the fix the seed file held only the passing
    /// test output and lacked the bootstrap failure entirely.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_BootstrapFailsTestPasses_PersistedFileIsTheCompleteLog()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
        repo.WriteTask("bootstrap-complete-log", "# Bootstrap failure persisted in full\n");
        var runner = new CapturingFlakeNixSubagentRunner();
        var tests = new CommandAwareTestRunner();
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bootstrap-complete-log");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The stage-9 seed gate went red on the bootstrap failure even though the test passed.
        var stage9 = sink.Events.Single(e => e is { EventName: "verify_result", StageNumber: 9 });
        Assert.Equal("red", stage9.Data?["check"]);
        var outputFile = stage9.Data?["outputFile"];
        Assert.False(string.IsNullOrEmpty(outputFile));
        var persisted = await File.ReadAllTextAsync(outputFile!);
        // The complete log MUST carry the bootstrap failure text.
        Assert.Contains("nix build of nono failed", persisted, StringComparison.Ordinal);
        // And the file must match the SOURCE of the tail the agent actually received.
        var stage10 = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Contains("nix build of nono failed", stage10.LastTestOutput!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Test (d): When <c>.relay/config.json</c> specifies custom
    /// <c>bootstrapFiles</c> globs and a <c>bootstrapCheckCmd</c>, those
    /// override the built-in defaults. The driver must scan the manifest
    /// against the configured globs and run the configured command.
    ///
    /// Without the implementation this test FAILS because the driver
    /// ignores the custom config fields and never runs the custom command.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_ConfiguredBootstrapGlobsAndCommand_OverridesDefaults()
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
              "maxVerifyLoops": 2,
              "archiveOnDone": true,
              "bootstrapFiles": ["custom.bootstrap", "*.env"],
              "bootstrapCheckCmd": "custom-bootstrap-check"
            }
            """);
        repo.WriteTask("custom-bootstrap", "# Custom bootstrap\n");

        var subagent = new CustomManifestSubagentRunner(["custom.bootstrap", "src/app.cs"]);
        var testRunner = new RecordingTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(0, "green"),            // stage 9 bootstrap — custom command
            new TestRunResult(0, "green"));           // stage 9 verify
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "custom-bootstrap");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The custom bootstrap command must have been passed to the test runner.
        Assert.Contains(testRunner.Calls, c => c.Command == "custom-bootstrap-check");

        // The built-in nix command must NOT have been used.
        Assert.DoesNotContain(testRunner.Calls, c => c.Command.Contains("nix develop"));
    }
}

// ── Test doubles for bootstrap tests ──────────────────────────────

/// <summary>
/// Returns a stage-4 manifest that includes <c>flake.nix</c> alongside
/// regular code files, simulating a run that edits an environment-
/// bootstrap file.
/// </summary>
internal sealed class FlakeNixManifestSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["add-nono"]}""",
            2 => """{"findings":"nono adds flake.nix dep","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"add nono to flake.nix","manifest":["flake.nix","src/app.cs","tests/app.tests.cs"]}""",
            5 => """{"testFiles":["tests/app.tests.cs"],"rationale":"red first"}""",
            6 => """{"summary":"added nono to flake.nix devShell"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified","commitMessages":["feat: add nono to dev shell","test: add coverage"]}""",
            10 => """{"summary":"fixed bootstrap"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// Combines <see cref="CapturingSubagentRunner"/> behaviour with a
/// manifest that includes <c>flake.nix</c>. Records every stage
/// invocation so tests can inspect <c>LastTestOutput</c>.
/// </summary>
internal sealed class CapturingFlakeNixSubagentRunner : ISubagentRunner
{
    private readonly FlakeNixManifestSubagentRunner _inner = new();
    private readonly List<StageInvocation> _invocations = [];

    public IReadOnlyList<StageInvocation> Invocations => _invocations;

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        _invocations.Add(invocation);
        return _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Returns a stage-4 manifest with the exact files specified at
/// construction time. Used to inject custom bootstrap file paths.
/// </summary>
internal sealed class CustomManifestSubagentRunner(string[] manifest) : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var manifestJson = string.Join("\",\"", manifest);
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["custom"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"custom","manifest":["{{manifestJson}}"]}""",
            5 => """{"testFiles":[],"rationale":"no authored tests"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"no changes"}""",
            9 => """{"summary":"verified","commitMessages":["chore: custom change"]}""",
            10 => """{"summary":"fixed"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
