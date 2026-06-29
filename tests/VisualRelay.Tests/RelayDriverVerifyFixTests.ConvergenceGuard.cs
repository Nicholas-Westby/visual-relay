using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunVerifyFixLoop_RedTreeUnchanged_DoesNotEarlyFlag_SpendsAllRunsThenFlags()
    {
        // The non-convergence early-flag is REMOVED: a higher tier (the next escalation
        // run) may change the verdict even when the working tree / failure is identical,
        // so the loop must spend ALL runs (MaxStageFailures, here 3) before flagging —
        // NOT bail on attempt 2.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 3);
        repo.WriteTask("non-convergent", "# Non-convergent\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        // ScriptedSubagentRunner writes no files — manifest files never exist, so
        // WorkingTreeHash is identical every attempt (tree "unchanged").
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),                // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),        // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),        // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),        // fix-verify run 1 gate — red
            new TestRunResult(1, "Failed TestX"),        // fix-verify run 1 retry — red
            new TestRunResult(1, "Failed TestX"),        // fix-verify run 2 gate — red
            new TestRunResult(1, "Failed TestX"),        // fix-verify run 2 retry — red
            new TestRunResult(1, "Failed TestX"),        // fix-verify run 3 gate — red
            new TestRunResult(1, "Failed TestX"));       // fix-verify run 3 retry — red
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "non-convergent");

        // Flags only after all 3 runs are spent — with the run-cap reason, not an early
        // non-convergence bail.
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.NotNull(outcome.Reason);
        Assert.Contains("after 3 fix-verify", outcome.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("non-convergent", outcome.Reason!, StringComparison.OrdinalIgnoreCase);
        // All 3 fix-verify runs ran (no early bail at run 2).
        var fixVerifyStarts = sink.Events.Where(e => e is { EventName: "stage_start", StageNumber: 10 }).ToList();
        Assert.Equal(3, fixVerifyStarts.Count);
        var flagged = sink.Events.Single(e => e.EventName == "flagged");
        Assert.Contains("after 3 fix-verify", flagged.Data?["reason"] ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunVerifyFixLoop_TreeChangedBetweenAttempts_DoesNotBailEarly()
    {
        // When the agent actually changes a manifest file, the convergence guard must
        // NOT fire — the loop continues until green (or maxLoops).
        using var repo = TestRepository.Create();
        var srcPath = Path.Combine(repo.Root, "src");
        Directory.CreateDirectory(srcPath);
        File.WriteAllText(Path.Combine(srcPath, "app.cs"), "// v0");
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("convergent", "# Convergent\n");

        // Writes a manifest file on the first stage-10 invocation so the tree hash
        // changes between attempt 1 and attempt 2.
        var writeOnce = new WriteOnAttemptSubagentRunner(repo.Root, "src/app.cs", "// v1");
        writeOnce.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red (tree will change)
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — red
            new TestRunResult(0, "green"));            // fix-verify attempt 2 gate — green
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(writeOnce, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "convergent");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var fixVerifyStarts = sink.Events.Where(e => e is { EventName: "stage_start", StageNumber: 10 }).ToList();
        Assert.Equal(2, fixVerifyStarts.Count);
        Assert.DoesNotContain(sink.Events, e => e.EventName == "flagged");
    }

    [Fact]
    public async Task RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops()
    {
        using var repo = TestRepository.Create();
        var srcPath = Path.Combine(repo.Root, "src");
        Directory.CreateDirectory(srcPath);
        File.WriteAllText(Path.Combine(srcPath, "app.cs"), "// v0");
        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true, maxStageFailures: 2);
        repo.WriteTask("unfixable", "# Unfixable\n");
        // The genuine "agent keeps trying, verify stays red, loop exhausts the run cap"
        // path. (The run cap is now MaxStageFailures; here pinned to 2.)
        var runner = new WriteEachAttemptSubagentRunner(repo.Root, "src/app.cs");
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — red
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 2 gate — red
            new TestRunResult(1, "Failed TestX"));     // fix-verify attempt 2 retry — red
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "unfixable");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("verify failed after 2 fix-verify attempts", outcome.Reason, StringComparison.Ordinal);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "unfixable", "NEEDS-REVIEW"));
        Assert.Contains("verify failed after 2 fix-verify attempts", review, StringComparison.Ordinal);
        var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "unfixable", "unfixable.seals"));
        Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
        Assert.Contains(seals, line => line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
    }
}

/// <summary>
/// Wraps <see cref="ScriptedSubagentRunner"/> and writes a manifest file to
/// rootPath exactly once (on the first stage-10 invocation) so the working-tree
/// hash changes between fix-verify attempt 1 and attempt 2.
/// </summary>
internal sealed class WriteOnAttemptSubagentRunner(string rootPath, string relativePath, string content) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private bool _written;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 10 && !_written)
        {
            _written = true;
            var fullPath = Path.Combine(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// Writes DISTINCT content to a manifest file on EVERY stage-10 invocation so the
/// working-tree hash changes each attempt — the convergence guard never fires and
/// the loop runs to maxLoops.
/// </summary>
internal sealed class WriteEachAttemptSubagentRunner(string rootPath, string relativePath) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private int _n;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 10)
        {
            var fullPath = Path.Combine(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, $"// attempt {++_n}", cancellationToken);
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
