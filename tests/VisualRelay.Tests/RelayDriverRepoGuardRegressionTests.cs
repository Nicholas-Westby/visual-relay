using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for the count-normalized guard baseline diff (Lever A).
/// Proves that pre-existing oversize files the task only touches (count
/// drift, e.g. 332→333) do NOT enter fix-verify, while genuinely-new
/// oversize files still block.  Uses helpers shared from
/// <see cref="RelayDriverRepoGuardTests"/>.
/// </summary>
public sealed class RelayDriverRepoGuardRegressionTests
{
    /// <summary>(e) Count-only difference (333 vs 332) must NOT block.</summary>
    [Fact]
    public async Task GuardRed_PreExistingOversizeTouched_CountChanged_DoesNotBlock()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": true,
              "maxVerifyLoops": 2,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("count-drift", "# Count drift\n");
        RelayDriverRepoGuardTests.InitGitRepo(repo.Root);

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "file too large: src/big.cs has 333 lines (limit 300)"),
            new TestRunResult(1, "file too large: src/big.cs has 332 lines (limit 300)"));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"), new TestRunResult(0, "all green"));
        var combined = new RelayDriverRepoGuardTests.CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner), ("dotnet test", testRunner));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "count-drift");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.Null(subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10));
        var ledger = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "count-drift", "ledger.md"));
        Assert.Contains("pre-existing", ledger, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>(f) Genuinely-new oversize file still blocks.</summary>
    [Fact]
    public async Task GuardRed_TaskPushedFileOverLimit_StillBlocks()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": true,
              "maxVerifyLoops": 2,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("new-oversize", "# New oversize\n");
        RelayDriverRepoGuardTests.InitGitRepo(repo.Root);

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "file too large: src/touched.cs has 305 lines (limit 300)"),
            new TestRunResult(0, ""));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"), new TestRunResult(0, "all green"),
            new TestRunResult(0, "all green"));
        var combined = new RelayDriverRepoGuardTests.CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner), ("dotnet test", testRunner));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "new-oversize");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage10 = subagent.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Contains("touched.cs", stage10.LastTestOutput!, StringComparison.Ordinal);
    }

    /// <summary>(g) Mixed: pre-existing excluded, new surfaces.</summary>
    [Fact]
    public async Task GuardRed_PreExistingOversize_PlusNewOversize_OnlyNewBlocks()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": true,
              "maxVerifyLoops": 2,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("mixed", "# Mixed\n");
        RelayDriverRepoGuardTests.InitGitRepo(repo.Root);

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "file too large: src/big.cs has 333 lines (limit 300)\nfile too large: src/brand-new.cs has 350 lines (limit 300)"),
            new TestRunResult(1, "file too large: src/big.cs has 332 lines (limit 300)"));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"), new TestRunResult(0, "all green"),
            new TestRunResult(0, "all green"));
        var combined = new RelayDriverRepoGuardTests.CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner), ("dotnet test", testRunner));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "mixed");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage10 = subagent.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Contains("brand-new.cs", stage10.LastTestOutput!, StringComparison.Ordinal);
        Assert.DoesNotContain("big.cs", stage10.LastTestOutput!, StringComparison.Ordinal);
    }

    /// <summary>(h) Numbered sibling pre-existing, new numbered sibling still blocks.</summary>
    [Fact]
    public async Task GuardRed_NumberedSiblingPreExisting_NewNumberedSiblingStillBlocks()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test {files}",
              "logSources": [],
              "baselineVerify": true,
              "maxVerifyLoops": 2,
              "archiveOnDone": true,
              "guardCmd": "tools/guards/check-file-size.sh"
            }
            """);
        repo.WriteTask("numbered-sibling", "# Numbered sibling\n");
        RelayDriverRepoGuardTests.InitGitRepo(repo.Root);

        var subagent = new CapturingSubagentRunner();
        subagent.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // Working: Page1 (pre-existing, same count) + Page2 (newly oversize)
        var guardRunner = new ScriptedTestRunner(
            new TestRunResult(1, "file too large: src/Page1.cs has 320 lines (limit 300)\nfile too large: src/Page2.cs has 999 lines (limit 300)"),
            new TestRunResult(1, "file too large: src/Page1.cs has 320 lines (limit 300)"));
        var testRunner = new ScriptedTestRunner(
            new TestRunResult(1, "red"), new TestRunResult(0, "all green"),
            new TestRunResult(0, "all green"));
        var combined = new RelayDriverRepoGuardTests.CommandDispatchTestRunner(
            ("check-file-size.sh", guardRunner), ("dotnet test", testRunner));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, combined, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "numbered-sibling");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage10 = subagent.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Contains("Page2.cs", stage10.LastTestOutput!, StringComparison.Ordinal);
        Assert.DoesNotContain("Page1.cs", stage10.LastTestOutput!, StringComparison.Ordinal);
    }
}
