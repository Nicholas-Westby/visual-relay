using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverManifestPrefixTests
{
    /// <summary>
    /// Stage-4 runner that returns a manifest containing a +-prefixed new-file
    /// entry alongside an existing file.  Delegates every other stage to the
    /// inner <see cref="CapturingSubagentRunner"/> so the test can inspect
    /// <see cref="StageInvocation.TestCommand"/> at later stages.
    /// </summary>
    private sealed class NewFilePrefixStage4Runner : ISubagentRunner
    {
        private readonly CapturingSubagentRunner _inner = new();
        private readonly string[] _manifest;
        private readonly string _plan;

        public NewFilePrefixStage4Runner(string plan, string[] manifest)
        {
            _plan = plan;
            _manifest = manifest;
            _inner.SeedHappyPath("src/Existing.cs", "tests/Existing.tests.cs");
        }

        public IReadOnlyList<StageInvocation> Invocations => _inner.Invocations;

        public Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            if (inv.Stage.Number == 4)
            {
                var mj = string.Join(",", _manifest.Select(x => $"\"{x}\""));
                var ep = _plan.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var j = $$"""{"plan":"{{ep}}","manifest":[{{mj}}]}""";
                return Task.FromResult(new SubagentResult(j, j, true, null));
            }
            return _inner.RunAsync(inv, ct);
        }
    }

    /// <summary>
    /// Stage-4 runner for plan-completeness retry tests: the first stage-4 call
    /// returns an incomplete manifest (triggering retry), and the second stage-4
    /// call returns a +-prefixed entry in the retry's manifest.
    /// </summary>
    private sealed class RetryNewFilePrefixStage4Runner : ISubagentRunner
    {
        private readonly CapturingSubagentRunner _inner = new();
        private int _stage4Calls;
        private readonly string _firstManifestFile;
        private readonly string _retryManifestFile;

        public RetryNewFilePrefixStage4Runner(string firstManifestFile, string retryManifestFile)
        {
            _firstManifestFile = firstManifestFile;
            _retryManifestFile = retryManifestFile;
            _inner.SeedHappyPath(firstManifestFile, "tests/T.cs");
        }

        public IReadOnlyList<StageInvocation> Invocations => _inner.Invocations;
        public int Stage4CallCount => _stage4Calls;

        public Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            if (inv.Stage.Number == 4)
            {
                _stage4Calls++;
                if (_stage4Calls == 1)
                {
                    // Incomplete plan: only covers one item but task has "## Done when"
                    // with two items, triggering a plan-completeness retry.
                    var j = $$"""{"plan":"Only do {{_firstManifestFile}}","manifest":["{{_firstManifestFile}}"]}""";
                    return Task.FromResult(new SubagentResult(j, j, true, null));
                }
                // Retry: now returns a +-prefixed new file in the manifest.
                var j2 = $$"""{"plan":"Do {{_firstManifestFile}} and create {{_retryManifestFile}}","manifest":["{{_firstManifestFile}}","+{{_retryManifestFile}}"]}""";
                return Task.FromResult(new SubagentResult(j2, j2, true, null));
            }
            return _inner.RunAsync(inv, ct);
        }
    }

    [Fact]
    public async Task Stage4_NewFilePrefix_IsStrippedFromInMemoryManifest()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "# Add feature\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Existing.cs"), "old");

        var runner = new NewFilePrefixStage4Runner(
            "Add new feature.", ["+src/New.cs", "src/Existing.cs"]);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(
                    new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");
        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);

        // The manifest.txt file on disk must have the bare path (no +).
        var manifestOnDisk = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "t", "manifest.txt"));
        Assert.Contains("src/New.cs", manifestOnDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("+src/New.cs", manifestOnDisk, StringComparison.Ordinal);

        // The in-memory manifest passed to BuildTargetedTestCommand must also
        // be clean.  The TestCommand field of stages 6 and 8 is where
        // targetedTestCommand flows.  It must NOT contain the + prefix.
        var s6 = runner.Invocations.FirstOrDefault(i => i.Stage.Number == 6);
        Assert.NotNull(s6);
        Assert.DoesNotContain("+src/New.cs", s6!.TestCommand ?? "", StringComparison.Ordinal);
        // The clean path should be present (unless the test command doesn't
        // include it because it's an impl file, not a test file — but the
        // + prefix definitely must be absent).
        var s8 = runner.Invocations.FirstOrDefault(i => i.Stage.Number == 8);
        Assert.NotNull(s8);
        Assert.DoesNotContain("+src/New.cs", s8!.TestCommand ?? "", StringComparison.Ordinal);

        // The manifest list passed in StageInvocation must also be clean.
        Assert.DoesNotContain(s6.Manifest, m => m.StartsWith('+'));
    }

    [Fact]
    public async Task PlanCompletenessRetry_NewFilePrefix_IsStrippedFromInMemoryManifest()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Task with a "## Done when" section that lists two items; the first
        // stage-4 plan only covers one, so a plan-completeness retry fires.
        repo.WriteTask("t", "## Done when\n- Implement Alpha\n- Create Beta\n");

        var runner = new RetryNewFilePrefixStage4Runner("src/Alpha.cs", "src/Beta.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(
                    new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "t");
        Assert.True(outcome.Status == RelayTaskOutcomeStatus.Committed, outcome.Reason);

        // The retry must have happened.
        Assert.Equal(2, runner.Stage4CallCount);

        // The manifest.txt on disk must NOT contain +src/Beta.cs.
        var manifestOnDisk = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "t", "manifest.txt"));
        Assert.Contains("src/Beta.cs", manifestOnDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("+src/Beta.cs", manifestOnDisk, StringComparison.Ordinal);

        // In-memory manifest at stage 6 must NOT contain +src/Beta.cs.
        var s6 = runner.Invocations.FirstOrDefault(i => i.Stage.Number == 6);
        Assert.NotNull(s6);
        Assert.DoesNotContain("+src/Beta.cs", s6!.TestCommand ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(s6.Manifest, m => m.StartsWith('+'));
    }
}
