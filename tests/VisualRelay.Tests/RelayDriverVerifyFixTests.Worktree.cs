using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunVerifyFixLoop_AgentEditUncommitted_AdvisoryExcludesAgentEditAndIncludesTestWrite()
    {
        // Defect-D regression: verify_mutated_tree delta must EXCLUDE the agent's own
        // uncommitted edits (overlaid into the snapshot before the "before" capture) and
        // INCLUDE only files the test suite wrote during the gate.
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        var manifestRel = Path.Combine("src", "app.cs");
        var manifestPath = Path.Combine(repo.Root, manifestRel);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "// committed");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("delta-exclusion", "# Delta exclusion\n");

        // Subagent runner that writes the agent's fix (src/app.cs) to the LIVE rootPath
        // at stage 6 (Implement) — simulating the real agent editing files. With
        // NoGitCommit the edit remains uncommitted through the gate, so it is overlaid
        // into the snapshot and must NOT appear in the delta.
        var agentRunner = new WriteAtStage6SubagentRunner(repo.Root, manifestRel, "// agent edit");
        agentRunner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");

        // Test runner that writes a DIFFERENT file (TEST-TIMING.md) from the second call
        // onward (skipping the in-place stage-5 author gate). This write lands in the
        // snapshot, not the real repo, and IS the expected delta.
        var mutatingTests = new MutatingTestRunner(
            "TEST-TIMING.md", "timing data",
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red
            new TestRunResult(0, "green"));            // fix-verify attempt 1 retry — green

        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(agentRunner, mutatingTests, sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "delta-exclusion");

        // The agent's fix must survive in the live tree — the gate ran in a snapshot.
        Assert.Equal("// agent edit", await File.ReadAllTextAsync(manifestPath));

        var advisory = sink.Events.FirstOrDefault(e => e.EventName == "verify_mutated_tree");
        Assert.NotNull(advisory);
        var files = advisory!.Data?["files"] ?? "";

        // The test suite's write IS in the delta (it was new in the snapshot after the gate).
        Assert.Contains("TEST-TIMING.md", files, StringComparison.Ordinal);

        // The agent's own uncommitted edit is NOT in the delta — it was already present in
        // the "before" snapshot capture (overlaid before the suite ran). This assertion is
        // the discriminator: a buggy delta that included all uncommitted files would fail here.
        Assert.DoesNotContain("src/app.cs", files, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunVerifyFixLoop_VerifyWritesTrackedFile_RealRepoUnmodifiedAndAdvisoryEmitted()
    {
        // When the test command writes a tracked file during verify, the real rootPath
        // must be unmodified (the write lands in the isolated snapshot), and a
        // verify_mutated_tree advisory naming that file must be emitted.
        using var repo = TestRepository.Create();
        // Real git repo (full-fidelity snapshot needs HEAD). TestGit.Run is SYNC.
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        var trackedRel = Path.Combine("src", "app.cs");
        var trackedFilePath = Path.Combine(repo.Root, trackedRel);
        Directory.CreateDirectory(Path.GetDirectoryName(trackedFilePath)!);
        File.WriteAllText(trackedFilePath, "// original");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        repo.WriteConfig("dotnet test", [], baselineVerify: false, enableFixVerify: true);
        repo.WriteTask("mutating-verify", "# Mutating verify\n");

        // A test runner that writes a TRACKED file relative to the rootPath it RECEIVES
        // (= the snapshot when isolation works), simulating a non-idempotent suite that
        // rewrites tracked files (e.g. TEST-TIMING.md, ratchet-status.json).
        var mutatingTests = new MutatingTestRunner(
            trackedRel, "// written by test run",
            new TestRunResult(1, "red"),              // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red
            new TestRunResult(0, "green"));            // fix-verify attempt 1 retry — green

        var sink = new InMemoryRelayEventSink();
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, mutatingTests, sink),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "mutating-verify");

        // Real repo must still have the original committed content — the suite's write
        // went into the throwaway snapshot, not here.
        Assert.Equal("// original", await File.ReadAllTextAsync(trackedFilePath));
        // Advisory event must name the file the TEST RUN wrote (forward-slash path).
        var advisory = sink.Events.FirstOrDefault(e => e.EventName == "verify_mutated_tree");
        Assert.NotNull(advisory);
        Assert.Contains("src/app.cs", advisory!.Data?["files"] ?? "", StringComparison.Ordinal);
    }
}

/// <summary>
/// Wraps <see cref="ScriptedSubagentRunner"/> and writes <paramref name="content"/>
/// to <paramref name="relativePath"/> under the LIVE <paramref name="rootPath"/> on
/// stage 6 (Implement), simulating the real agent editing a manifest file.  With
/// <see cref="RelayDriverOptions.NoGitCommit"/> the edit stays uncommitted through
/// the gate, so it is overlaid into the worktree snapshot before the "before" delta
/// capture — and therefore must NOT appear in the <c>verify_mutated_tree</c> advisory.
/// </summary>
internal sealed class WriteAtStage6SubagentRunner(string rootPath, string relativePath, string content) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 6)
        {
            var fullPath = Path.Combine(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// From the SECOND gate call onward, writes <paramref name="content"/> to
/// <paramref name="relativePath"/> UNDER THE ROOTPATH IT RECEIVES (the snapshot
/// when isolation is active), simulating a non-idempotent verify suite, then
/// returns the next scripted result.
/// <para>
/// The FIRST call is the stage-5 author gate, which runs in-place against the real
/// <c>rootPath</c> and is intentionally NOT isolated by Task 8 (only the two
/// authoritative verify gates — stage 9 and stage 10 — are). Skipping the write on
/// that first call keeps the real repo clean through stage 5 so the test exercises
/// exactly the stage-9/10 isolation contract: a tracked-file write during verify
/// must land in the throwaway snapshot (not the real repo) and surface as a
/// delta-detected <c>verify_mutated_tree</c> advisory.
/// </para>
/// </summary>
internal sealed class MutatingTestRunner(
    string relativePath,
    string content,
    params TestRunResult[] results) : ITestRunner
{
    private readonly Queue<TestRunResult> _results = new(results);
    private int _calls;

    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        // Skip the in-place stage-5 author-gate call (#1); mutate from the first
        // authoritative verify gate (stage 9, call #2) onward.
        if (_calls++ > 0)
        {
            var target = Path.Combine(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content, cancellationToken);
        }
        return _results.Count > 0 ? _results.Dequeue() : new TestRunResult(0, "green");
    }
}
