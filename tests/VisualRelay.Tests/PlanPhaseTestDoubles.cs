using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers for plan-phase tests: config factory and git repo initializer.
/// </summary>
internal static class PlanPhaseTestHelpers
{
    public static RelayConfig MakeConfig(int maxPlanConcurrency, string testCommand = "dotnet test") =>
        new(
            TasksDir: "llm-tasks",
            TestCommand: testCommand,
            TestFileCommand: "dotnet test {files}",
            LogSources: [],
            TierProfiles: new Dictionary<string, string>(),
            MaxVerifyLoops: 5,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>(),
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2,
            BypassSandbox: false,
            MaxPlanConcurrency: maxPlanConcurrency,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

    /// <summary>
    /// Initializes a fresh git repo with a seed commit so worktree creation
    /// has a valid HEAD reference.
    /// </summary>
    public static void InitGitRepo(string rootPath)
    {
        TestGit.Run(rootPath, "init");
        TestGit.Run(rootPath, "config", "user.email", "test@example.test");
        TestGit.Run(rootPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(rootPath, ".gitkeep"), string.Empty);
        TestGit.Run(rootPath, "add", ".");
        TestGit.Run(rootPath, "commit", "-m", "seed");
    }
}

/// <summary>
/// Wraps a <see cref="ScriptedSubagentRunner"/> and tracks in-flight concurrency
/// with <see cref="Interlocked"/> counters. Records the peak concurrent count
/// so tests can assert a batch limit was enforced.
/// </summary>
internal sealed class CountingConcurrencySubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private int _inFlight;
    private int _peak;

    public int PeakConcurrency => _peak;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var current = Interlocked.Increment(ref _inFlight);
        // Record peak under a brief lock-free spin; Interlocked.CompareExchange
        // would be more precise but this is fine for test observation.
        lock (this)
        {
            if (current > _peak)
                _peak = current;
        }

        try
        {
            // Small delay so concurrent runs overlap observably.
            await Task.Delay(50, cancellationToken);
            return await _inner.RunAsync(invocation, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// Simulates a planning agent that shells a stray file write (e.g. via a
/// sub-shell or editor save) into the working tree. The worktree isolation
/// must contain this write so the main repo tree stays unmodified.
///
/// Writes a marker file into <c>TargetRoot/dirty-marker.txt</c> during
/// stage 2 (Research) — the earliest write-capable planning stage.
/// </summary>
internal sealed class ShellWritingSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private readonly string _markerFileName;

    /// <summary>
    /// The absolute path where the stray write was placed (set after RunAsync).
    /// </summary>
    public string? WrittenFilePath { get; private set; }

    public ShellWritingSubagentRunner(string markerFileName = "dirty-marker.txt")
    {
        _markerFileName = markerFileName;
    }

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Simulate a stray shell write during a planning stage (stage 2 – Research).
        // Stages 2–4 have commands="all" so a write is plausible.
        if (invocation.Stage.Number == 2)
        {
            WrittenFilePath = Path.Combine(invocation.TargetRoot, _markerFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(WrittenFilePath)!);
            await File.WriteAllTextAsync(WrittenFilePath, "stray planning write", cancellationToken);
        }

        return await _inner.RunAsync(invocation, cancellationToken);
    }
}

/// <summary>
/// A subagent runner that seeds distinct manifests for two tasks so the
/// commit-contamination test has clearly separable file sets.
/// </summary>
internal sealed class DualTaskSubagentRunner : ISubagentRunner
{
    private readonly string _taskId;
    private readonly string _codeFile;
    private readonly string _testFile;

    public DualTaskSubagentRunner(string taskId, string codeFile, string testFile)
    {
        _taskId = taskId;
        _codeFile = codeFile;
        _testFile = testFile;
    }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, _testFile), $"// red test for {_taskId}");
        }
        else if (invocation.Stage.Number == 6)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(invocation.TargetRoot, _codeFile))!);
            File.WriteAllText(Path.Combine(invocation.TargetRoot, _codeFile), $"// impl for {_taskId}");
        }

        var json = invocation.Stage.Number switch
        {
            1 => $$"""{"summary":"framed for {{_taskId}}","options":["option-a"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"edit files for {{_taskId}}","manifest":["{{_codeFile}}","{{_testFile}}"]}""",
            5 => $$"""{"testFiles":["{{_testFile}}"],"rationale":"red first for {{_taskId}}"}""",
            6 => $$"""{"summary":"implemented {{_taskId}}"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => $$"""{"summary":"verified","commitMessages":["feat: {{_taskId}} feature","chore: {{_taskId}} cleanup","test: {{_taskId}} coverage"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// An <see cref="IRelayTaskRunner"/> that routes RunTaskAsync to per-task
/// <see cref="RelayDriver"/> instances. Used in queue-controller parallel
/// tests where each planning task needs its own driver with its own
/// worktree root and own event sink.
/// </summary>
internal sealed class PerTaskDriverFactory : IRelayTaskRunner
{
    private readonly string _mainRootPath;
    private readonly Func<string, string, ISubagentRunner> _subagentFactory;
    private readonly ITestRunner _testRunner;
    private readonly RelayDriverOptions _options;

    public PerTaskDriverFactory(
        string mainRootPath,
        Func<string, string, ISubagentRunner> subagentFactory,
        ITestRunner testRunner,
        RelayDriverOptions options)
    {
        _mainRootPath = mainRootPath;
        _subagentFactory = subagentFactory;
        _testRunner = testRunner;
        _options = options;
    }

    public async Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        // The controller passes rootPath which may differ from _mainRootPath
        // (e.g. a worktree path during planning). Use it as-is.
        var subagent = _subagentFactory(taskId, rootPath);
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(subagent, _testRunner, sink),
            _options);
        return await driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}

/// <summary>
/// Tracks which task ids were planned vs executed, and records the root
/// path used for each — enabling assertions that planning happened in
/// worktree paths and execution in the main repo path.
/// </summary>
internal sealed class PhaseTrackingTaskRunner : IRelayTaskRunner
{
    private readonly string _mainRootPath;
    private readonly ISubagentRunner _subagent;
    private readonly ITestRunner _testRunner;

    public PhaseTrackingTaskRunner(string mainRootPath, ISubagentRunner subagent, ITestRunner testRunner)
    {
        _mainRootPath = mainRootPath;
        _subagent = subagent;
        _testRunner = testRunner;
    }

    public List<(string TaskId, string RootPath, RelayDriverOptions Options)> Runs { get; } = [];

    public async Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        var options = rootPath == _mainRootPath
            ? new RelayDriverOptions(CreateGitCommit: true, Resume: true) // serial execute phase
            : new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4); // parallel plan phase

        Runs.Add((taskId, rootPath, options));

        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(_subagent, _testRunner, sink),
            options);
        return await driver.RunTaskAsync(rootPath, taskId, cancellationToken);
    }
}

/// <summary>
/// Wraps an inner <see cref="ScriptedSubagentRunner"/> and publishes trace-level
/// events to an optional <see cref="IRelayEventSink"/> — simulating what
/// SwivalSubagentRunner's trace tailer does when its eventSink is non-null.
/// When <paramref name="traceSink"/> is null, trace events are silently dropped
/// (mirroring the GUI gap where planSubagentFactory doesn't pass an eventSink).
/// </summary>
internal sealed class TraceEmittingSubagentRunner : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner;
    private readonly IRelayEventSink? _traceSink;

    public TraceEmittingSubagentRunner(ScriptedSubagentRunner inner, IRelayEventSink? traceSink = null)
    {
        _inner = inner;
        _traceSink = traceSink;
    }

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (_traceSink is not null)
        {
            await _traceSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow,
                "debug",
                "trace_entry",
                $"run-{invocation.TaskName}",
                invocation.TargetRoot,
                invocation.TaskName,
                invocation.Stage.Number,
                invocation.Tier,
                Data: new Dictionary<string, string>
                {
                    ["content"] = $"trace for {invocation.TaskName} stage {invocation.Stage.Number}",
                    ["attempt"] = "1"
                }), cancellationToken);
        }

        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
