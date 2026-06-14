using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverReviewEscalationTests
{
    // ── Balanced-only path (pass + small manifest) ────────────────────

    [Fact]
    public async Task Review_PassVerdict_SmallManifest_RunsOnBalancedOnly()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
        repo.WriteTask("clean-diff", "# Clean diff\n");
        var runner = new Stage7SequenceRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Balanced Review returns pass; no issues, small manifest.
        runner.EnqueueStage7Result("""{"verdict":"pass","issues":[]}""");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate
            new TestRunResult(0, "green")); // stage 9 verify
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "clean-diff");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage7Invocations = runner.Invocations.Where(i => i.Stage.Number == 7).ToArray();
        Assert.Single(stage7Invocations);
        Assert.Equal("balanced", stage7Invocations[0].Tier);
    }

    // ── Escalation on model signal ────────────────────────────────────

    [Fact]
    public async Task Review_FailVerdict_EscalatesToFrontier()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
        repo.WriteTask("bad-diff", "# Bad diff\n");
        var runner = new Stage7SequenceRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // First (balanced): verdict=changes → should escalate.
        runner.EnqueueStage7Result("""{"verdict":"changes","issues":["unused-import"]}""");
        // Second (frontier): authoritative result.
        runner.EnqueueStage7Result("""{"verdict":"changes","issues":["unused-import","missing-null-check"]}""");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-diff");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage7Invocations = runner.Invocations.Where(i => i.Stage.Number == 7).ToArray();
        Assert.Equal(2, stage7Invocations.Length);
        Assert.Equal("balanced", stage7Invocations[0].Tier);
        Assert.Equal("frontier", stage7Invocations[1].Tier);
    }

    // ── Escalation on size heuristic ──────────────────────────────────

    [Fact]
    public async Task Review_PassVerdict_LargeManifest_EscalatesToFrontier()
    {
        using var repo = TestRepository.Create();
        // Set file threshold low so the normal 2-file manifest triggers it.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1,
              "reviewEscalationManifestFileThreshold": 1
            }
            """);
        repo.WriteTask("large-diff", "# Large diff\n");
        var runner = new Stage7SequenceRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Balanced pass verdict — should still escalate because manifest > 1 file.
        runner.EnqueueStage7Result("""{"verdict":"pass","issues":[]}""");
        // Frontier authoritative result.
        runner.EnqueueStage7Result("""{"verdict":"pass","issues":[]}""");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "large-diff");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage7Invocations = runner.Invocations.Where(i => i.Stage.Number == 7).ToArray();
        Assert.Equal(2, stage7Invocations.Length);
        Assert.Equal("balanced", stage7Invocations[0].Tier);
        Assert.Equal("frontier", stage7Invocations[1].Tier);
    }

    // ── Escalation disabled ───────────────────────────────────────────

    [Fact]
    public async Task Review_EscalationDisabled_RunsOnceOnBalanced()
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
              "maxVerifyLoops": 1,
              "reviewEscalationEnabled": false
            }
            """);
        repo.WriteTask("no-escalation", "# No escalation\n");
        var runner = new Stage7SequenceRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Balanced returns changes — but escalation is disabled, so no second call.
        runner.EnqueueStage7Result("""{"verdict":"changes","issues":["bug"]}""");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-escalation");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage7Invocations = runner.Invocations.Where(i => i.Stage.Number == 7).ToArray();
        Assert.Single(stage7Invocations);
        Assert.Equal("balanced", stage7Invocations[0].Tier);
    }

    // ── Frontier result is authoritative in ledger ────────────────────

    [Fact]
    public async Task Review_FrontierResultIsUsedInLedger()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
        repo.WriteTask("escalated-review", "# Escalated review\n");
        var runner = new Stage7SequenceRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Balanced returns changes.
        runner.EnqueueStage7Result("""{"verdict":"changes","issues":["unused-import"]}""");
        // Frontier returns different (authoritative) issues.
        runner.EnqueueStage7Result("""{"verdict":"changes","issues":["unused-import","security-risk"]}""");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "escalated-review");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Ledger must contain the frontier result, not the balanced one.
        var ledger = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "escalated-review", "ledger.md"));
        Assert.Contains("security-risk", ledger, StringComparison.Ordinal);
        // The seals must reference the frontier body hash, not the balanced one.
        var seals = await File.ReadAllLinesAsync(
            Path.Combine(repo.Root, ".relay", "escalated-review", "escalated-review.seals"));
        var stage7Seal = seals.FirstOrDefault(line => line.Contains("\"n\":7", StringComparison.Ordinal));
        Assert.NotNull(stage7Seal);
        // The seal's artifactHash must match the frontier body (with "security-risk"),
        // not the balanced body (which only has "unused-import").
        var expectedFrontierHash = Hashing.Sha256Hex("7", "Review",
            """{"verdict":"changes","issues":["unused-import","security-risk"]}""");
        Assert.Contains(expectedFrontierHash, stage7Seal, StringComparison.Ordinal);
    }
}
