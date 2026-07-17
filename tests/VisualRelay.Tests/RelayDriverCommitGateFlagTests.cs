using VisualRelay.Core.Execution;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed class RelayDriverCommitGateFlagTests
{
    /// <summary>
    /// Wraps a <see cref="GitSimEngine"/> so that the first
    /// <c>ls-files --others --exclude-standard</c> call after a commit
    /// returns an extra "missed" file — simulating a post-commit invariant
    /// failure without needing a real filesystem race.
    /// All other calls pass through.
    /// </summary>
    private sealed class PostCommitFlagTriggeringGitInvoker : IGitInvoker
    {
        private readonly GitSimEngine _inner;
        private bool _commitHappened;
        private bool _alreadyInjected;

        public PostCommitFlagTriggeringGitInvoker(GitSimEngine inner) => _inner = inner;

        public GitSimEngine Inner => _inner;

        public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default,
            Action<string>? onActivity = null)
        {
            var args = arguments.ToList();

            // The post-commit check (FindUncommittedAuthoredFilesAsync) is
            // the only ls-files --others --exclude-standard call that happens
            // after a commit. Wait for the commit, then inject on the very
            // next ls-files --others call.
            if (!_alreadyInjected && _commitHappened
                && args.Contains("ls-files") && args.Contains("--others"))
            {
                _alreadyInjected = true;
                var normal = await _inner.RunAsync(rootPath, arguments, cancellationToken, timeout, environment, killToken, onActivity);
                var injectedPath = "src/uncommitted-file.cs";
                var output = string.IsNullOrWhiteSpace(normal.Output)
                    ? injectedPath
                    : normal.Output.TrimEnd() + "\n" + injectedPath;
                return (0, output, false);
            }

            if (args.Contains("commit") && args.Contains("-m"))
                _commitHappened = true;

            return await _inner.RunAsync(rootPath, arguments, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }

    [Fact]
    public async Task CommitSucceeds_PostCommitCheckFlags_StatusRecordsFlagged()
    {
        // ── Arrange ──────────────────────────────────────────────────
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("commit-flag", "batch: 2\n\n# Commit flag test\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        var wrapper = new PostCommitFlagTriggeringGitInvoker(sim);
        // EditingSubagentRunner actually writes src/status.cs → "new" at
        // stage 6 so the code-change gate (stage 11) sees a real diff and
        // lets the run proceed to the commit stage (12).
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new EditingSubagentRunner(),
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                wrapper),
            RelayDriverOptions.Default);

        // ── Act ──────────────────────────────────────────────────────
        var outcome = await driver.RunTaskAsync(repo.Root, "commit-flag");

        // ── Assert ───────────────────────────────────────────────────
        // 1. Outcome reports Flagged (not Committed).
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        Assert.Contains("sealed commit is missing", outcome.Reason);

        // 2. Persisted status.json records stage 12 Flagged with the reason.
        var statusDir = Path.Combine(repo.Root, ".relay", "commit-flag");
        Assert.True(Directory.Exists(statusDir), "task directory must exist");
        var statusPath = Path.Combine(statusDir, "status.json");
        Assert.True(File.Exists(statusPath), "status.json must exist");
        var statusEntries = StageStatusRecord.Read(statusDir);
        Assert.Contains(statusEntries, e => e.Stage == 12);
        var stage12 = statusEntries.First(e => e.Stage == 12);
        Assert.Equal("Flagged", stage12.Status);
        Assert.NotNull(stage12.Error);
        Assert.Contains("sealed commit is missing", stage12.Error);

        // 3. Re-read: flag survives a fresh hydrate (no stale in-memory copy).
        var reRead = StageStatusRecord.Read(statusDir);
        var reRead12 = reRead.First(e => e.Stage == 12);
        Assert.Equal("Flagged", reRead12.Status);
        Assert.Equal(stage12.Error, reRead12.Error);

        // 4. The sealed commit IS on the branch (Option A: keep the seal).
        var head = sim.Head(repo.Root);
        Assert.NotNull(head);
        var headInfo = sim.CommitInfo(repo.Root, head);
        Assert.NotNull(headInfo);
        Assert.Contains("Relay-Seal:", headInfo!.Message);
        Assert.Contains("Task: commit-flag", headInfo.Message);

        // 5. Task definition exists in completed/ only — NOT in active tasks.
        Assert.False(File.Exists(Path.Combine(repo.Root, "llm-tasks", "commit-flag.md")),
            "active task file must not exist — retirement kept the move");
        var completedFiles = Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "completed"))
            ? Directory.EnumerateFiles(
                Path.Combine(repo.Root, "llm-tasks", "completed"),
                "DONE-commit-flag.md",
                SearchOption.AllDirectories).ToList()
            : [];
        Assert.Single(completedFiles);

        // 6. NEEDS-REVIEW marker exists (written by FlagAsync).
        Assert.True(File.Exists(Path.Combine(statusDir, "NEEDS-REVIEW")));
        var marker = await File.ReadAllTextAsync(Path.Combine(statusDir, "NEEDS-REVIEW"));
        Assert.Contains("sealed commit is missing", marker);
        Assert.Contains("stage 12", marker);
    }

    [Fact]
    public async Task FlagAtNonCommitStage_PersistsCorrectly()
    {
        // ── Arrange ──────────────────────────────────────────────────
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/app.cs", []);
        repo.WriteTask("stage7-flag", "# Stage 7 flag test\n");

        // FlagAtStageSubagentRunner returns invalid result at stage 7.
        var runner = new FlagAtStageSubagentRunner(flagAtStage: 7);
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        // ── Act ──────────────────────────────────────────────────────
        var outcome = await driver.RunTaskAsync(repo.Root, "stage7-flag");

        // ── Assert ───────────────────────────────────────────────────
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);

        // Persisted status.json records stage 7 Flagged.
        var statusDir = Path.Combine(repo.Root, ".relay", "stage7-flag");
        Assert.True(Directory.Exists(statusDir));
        var statusEntries = StageStatusRecord.Read(statusDir);
        Assert.Contains(statusEntries, e => e.Stage == 7);
        var stage7 = statusEntries.First(e => e.Stage == 7);
        Assert.Equal("Flagged", stage7.Status);

        // Re-read survives.
        var reRead = StageStatusRecord.Read(statusDir);
        var reRead7 = reRead.First(e => e.Stage == 7);
        Assert.Equal("Flagged", reRead7.Status);
    }

    [Fact]
    public async Task PostCommitFlag_NoDuplicateTaskDefinition()
    {
        // ── Arrange ──────────────────────────────────────────────────
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.cs", []);
        repo.WriteTask("no-dup", "batch: 2\n\n# No duplicate test\n");
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/status.cs", "old");
        sim.Commit(repo.Root, "chore: seed repo");

        var wrapper = new PostCommitFlagTriggeringGitInvoker(sim);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new EditingSubagentRunner(),
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink(),
                wrapper),
            RelayDriverOptions.Default);

        // ── Act ──────────────────────────────────────────────────────
        var outcome = await driver.RunTaskAsync(repo.Root, "no-dup");
        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);

        // ── Assert: exactly one tracked copy ─────────────────────────
        // The task must NOT exist in the active tasks folder (retirement
        // moved it, and the rollback must NOT have re-created it).
        var activePath = Path.Combine(repo.Root, "llm-tasks", "no-dup.md");
        Assert.False(File.Exists(activePath),
            "active task must not exist — retirement kept the move, rollback skipped");

        // The task must exist exactly once in completed/.
        var completedDir = Path.Combine(repo.Root, "llm-tasks", "completed");
        var allMdFiles = Directory.Exists(completedDir)
            ? Directory.EnumerateFiles(completedDir, "*.md", SearchOption.AllDirectories).ToList()
            : [];
        var taskCopies = allMdFiles
            .Where(f => Path.GetFileName(f).Contains("no-dup"))
            .ToList();
        Assert.True(taskCopies.Count == 1,
            $"expected exactly 1 completed copy of no-dup, found {taskCopies.Count}: {string.Join(", ", taskCopies)}");

        // And the completed copy is the DONE- prefixed one.
        Assert.EndsWith("DONE-no-dup.md", taskCopies[0]);
    }
}
