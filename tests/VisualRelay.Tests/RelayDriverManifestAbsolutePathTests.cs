using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The plan's manifest and Author-tests' test files are repo-relative by contract.
/// A model that writes an absolute path instead used to have its leading slash
/// trimmed, leaving a name that exists nowhere: the file dropped out of the commit,
/// the red gate and the test count, and nothing was logged. One under the workspace
/// now resolves to the name the model meant; any other is a reported drop.
/// </summary>
public sealed class RelayDriverManifestAbsolutePathTests
{
    /// <summary>
    /// Emits stage-4 and stage-5 lists built from the workspace the stage is actually
    /// running against — during stages 1 to 4 that is the planning worktree, which is
    /// what a model copying a path out of its own tool output would name.
    /// </summary>
    private sealed class AbsolutePathStage4Runner(
        Func<string, string[]> manifest, Func<string, string[]>? testFiles = null) : ISubagentRunner
    {
        private readonly CapturingSubagentRunner _inner = Seeded();

        private static CapturingSubagentRunner Seeded()
        {
            var runner = new CapturingSubagentRunner();
            runner.SeedHappyPath("src/Existing.cs", "tests/Existing.tests.cs");
            return runner;
        }

        public IReadOnlyList<StageInvocation> Invocations => _inner.Invocations;

        public Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            if (inv.Stage.Number == 4)
                return Json($$"""{"plan":"Do the work.","manifest":[{{Quote(manifest(inv.TargetRoot))}}]}""");
            if (inv.Stage.Number == 5 && testFiles is not null)
                return Json($$"""{"testFiles":[{{Quote(testFiles(inv.TargetRoot))}}],"rationale":"red first"}""");
            return _inner.RunAsync(inv, ct);
        }

        private static string Quote(IEnumerable<string> entries) =>
            string.Join(",", entries.Select(entry => $"\"{entry.Replace("\\", "\\\\")}\""));

        private static Task<SubagentResult> Json(string json) =>
            Task.FromResult(new SubagentResult(json, json, true, null));
    }

    private static (RelayDriver Driver, InMemoryRelayEventSink Sink, AbsolutePathStage4Runner Runner) Build(
        TestRepository repo, Func<string, string[]> manifest, Func<string, string[]>? testFiles = null)
    {
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "# Add feature\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Existing.cs"), "old");

        var runner = new AbsolutePathStage4Runner(manifest, testFiles);
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink),
            RelayDriverOptions.NoGitCommit);
        return (driver, sink, runner);
    }

    private static IEnumerable<RelayEvent> Drops(InMemoryRelayEventSink sink) =>
        sink.Events.Where(e => e.EventName == "path_entry_dropped");

    [Fact]
    public async Task Stage4_AbsoluteEntryUnderTheRoot_BecomesRepoRelative()
    {
        using var repo = TestRepository.Create();
        var (driver, sink, runner) = Build(
            repo, root => [Path.Combine(root, "src", "New.cs").Replace('\\', '/'), "src/Existing.cs"]);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var manifestOnDisk = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "t", "manifest.txt"));
        Assert.Contains("src/New.cs", manifestOnDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(repo.Root, manifestOnDisk, StringComparison.Ordinal);
        Assert.Empty(Drops(sink));

        var stage6 = runner.Invocations.First(i => i.Stage.Number == 6);
        Assert.Contains("src/New.cs", stage6.Manifest);
        Assert.DoesNotContain(stage6.Manifest, entry => ManifestPaths.IsRooted(entry));
    }

    [Fact]
    public async Task Stage4_AbsoluteEntryOutsideTheRoot_IsDroppedAndReported()
    {
        using var repo = TestRepository.Create();
        var (driver, sink, _) = Build(
            repo, _ => ["/elsewhere/src/Stray.cs", "src/Existing.cs"]);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var manifestOnDisk = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "t", "manifest.txt"));
        Assert.DoesNotContain("Stray.cs", manifestOnDisk, StringComparison.Ordinal);

        var drop = Assert.Single(Drops(sink));
        Assert.Equal("warn", drop.Level);
        Assert.Equal("/elsewhere/src/Stray.cs", drop.Data!["entry"]);
        Assert.Equal("absolute path outside the workspace", drop.Data["reason"]);
        Assert.Equal("manifest", drop.Data["list"]);
        Assert.Equal(4, drop.StageNumber);

        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "t", "ledger.md"));
        Assert.Contains(
            "dropped 1 entry from manifest (absolute path outside the workspace): `/elsewhere/src/Stray.cs`",
            ledger,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan-completeness retry rewrites the manifest and used to strip only the
    /// '+' from each entry: no resolution at all, so a rooted entry went through
    /// untouched. It now reads the list the way the first manifest is read.
    /// </summary>
    private sealed class RetryAbsolutePathStage4Runner(Func<string, string[]> retryManifest) : ISubagentRunner
    {
        private readonly CapturingSubagentRunner _inner = Seeded();
        private int _stage4Calls;

        private static CapturingSubagentRunner Seeded()
        {
            var runner = new CapturingSubagentRunner();
            runner.SeedHappyPath("src/Alpha.cs", "tests/T.cs");
            return runner;
        }

        public Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            if (inv.Stage.Number != 4)
                return _inner.RunAsync(inv, ct);

            _stage4Calls++;
            var json = _stage4Calls == 1
                ? """{"plan":"Only do src/Alpha.cs","manifest":["src/Alpha.cs"]}"""
                : $$"""{"plan":"Do src/Alpha.cs and create src/Beta.cs","manifest":[{{string.Join(",", retryManifest(inv.TargetRoot).Select(entry => $"\"{entry}\""))}}]}""";
            return Task.FromResult(new SubagentResult(json, json, true, null));
        }
    }

    private static (RelayDriver Driver, InMemoryRelayEventSink Sink) BuildRetry(
        TestRepository repo, Func<string, string[]> retryManifest)
    {
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "## Done when\n- Implement Alpha\n- Create Beta\n");

        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new RetryAbsolutePathStage4Runner(retryManifest),
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink),
            RelayDriverOptions.NoGitCommit);
        return (driver, sink);
    }

    [Fact]
    public async Task PlanCompletenessRetry_AbsoluteEntryUnderTheRoot_BecomesRepoRelative()
    {
        using var repo = TestRepository.Create();
        var (driver, sink) = BuildRetry(
            repo, root => ["src/Alpha.cs", Path.Combine(root, "src", "Beta.cs").Replace('\\', '/')]);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var manifestOnDisk = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "t", "manifest.txt"));
        Assert.Contains("src/Beta.cs", manifestOnDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(repo.Root, manifestOnDisk, StringComparison.Ordinal);
        Assert.Empty(Drops(sink));
    }

    [Fact]
    public async Task PlanCompletenessRetry_AbsoluteEntryOutsideTheRoot_IsDroppedAndReported()
    {
        using var repo = TestRepository.Create();
        var (driver, sink) = BuildRetry(repo, _ => ["src/Alpha.cs", "/elsewhere/src/Beta.cs"]);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var manifestOnDisk = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "t", "manifest.txt"));
        Assert.DoesNotContain("Beta.cs", manifestOnDisk, StringComparison.Ordinal);

        var drop = Assert.Single(Drops(sink));
        Assert.Equal("/elsewhere/src/Beta.cs", drop.Data!["entry"]);
        Assert.Equal("absolute path outside the workspace", drop.Data["reason"]);
        Assert.Equal(4, drop.StageNumber);
    }

    [Fact]
    public async Task Stage5_AbsoluteTestFile_IsResolvedLikeTheManifest()
    {
        using var repo = TestRepository.Create();
        var (driver, sink, runner) = Build(
            repo,
            _ => ["src/Existing.cs", "tests/Existing.tests.cs"],
            root =>
            [
                Path.Combine(root, "tests", "Existing.tests.cs").Replace('\\', '/'),
                "/elsewhere/tests/Stray.tests.cs",
            ]);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");

        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);
        var drop = Assert.Single(Drops(sink));
        Assert.Equal("testFiles", drop.Data!["list"]);
        Assert.Equal(5, drop.StageNumber);

        // The resolved one is what the gate command runs against; the absolute
        // spelling never reaches it.
        var stage6 = runner.Invocations.First(i => i.Stage.Number == 6);
        Assert.DoesNotContain(repo.Root, stage6.TestCommand ?? string.Empty, StringComparison.Ordinal);
    }
}
