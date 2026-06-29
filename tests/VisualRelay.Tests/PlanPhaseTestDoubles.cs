using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers for plan-phase tests: config factory and git repo initializer.
/// </summary>
internal static class PlanPhaseTestHelpers
{
    /// <summary>
    /// One shared hermetic <see cref="IEnvironmentAccessor"/> the orchestrator tests pass
    /// into <see cref="PlanPhaseRunner.RunPlanPhaseAsync"/> / <see cref="VisualRelay.Core.Queue.RelayQueueController"/>
    /// so every internally-built planning driver self-heals the vr-guard sandbox profile
    /// under a process-temp <c>XDG_CONFIG_HOME</c> instead of the real <c>~/.config</c> — the
    /// same pre-seeded <see cref="TempXdgEnvironmentAccessor"/> that backs
    /// <see cref="RelayDriverDependencies.ForTests"/>. Its temp profile is seeded once with the
    /// canonical content, so each <see cref="NonoProfileEnsurer.EnsureAsync"/> finds matching
    /// bytes and skips its write — safe under parallel planning and the always-on vr-guard nono
    /// sandbox (which denies the real-<c>~/.config</c> write). A single shared seam means the
    /// orchestrator call sites get isolation without per-test accessor plumbing.
    /// </summary>
    public static IEnvironmentAccessor TempXdg { get; } = new TempXdgEnvironmentAccessor();

    public static RelayConfig MakeConfig(int maxPlanConcurrency, string testCommand = "dotnet test") =>
        new(
            TasksDir: "llm-tasks",
            TestCommand: testCommand,
            TestFileCommand: "dotnet test {files}",
            LogSources: [],
            TierProfiles: new Dictionary<string, string>(),
            EnableFixVerify: true,
            MaxStageFailures: 3,
            MaxTurns: 200,
            BaselineVerify: true,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 1_200_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>(),
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2,
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

    // ReSharper disable once InconsistentlySynchronizedField — _peak is written
    // under lock during the run; this getter is only read AFTER the awaited plan
    // phase fully completes (all writers joined), so no concurrent access occurs.
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
internal sealed class ShellWritingSubagentRunner(string markerFileName = "dirty-marker.txt") : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();

    /// <summary>
    /// The absolute path where the stray write was placed (set after RunAsync).
    /// </summary>
    public string? WrittenFilePath { get; private set; }

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        // Simulate a stray shell write during a planning stage (stage 2 – Research).
        // Stages 2–4 have commands="all" so a write is plausible.
        if (invocation.Stage.Number == 2)
        {
            WrittenFilePath = Path.Combine(invocation.TargetRoot, markerFileName);
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
internal sealed class DualTaskSubagentRunner(string taskId, string codeFile, string testFile) : ISubagentRunner
{

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, testFile), $"// red test for {taskId}");
        }
        else if (invocation.Stage.Number == 6)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(invocation.TargetRoot, codeFile))!);
            File.WriteAllText(Path.Combine(invocation.TargetRoot, codeFile), $"// impl for {taskId}");
        }

        var json = invocation.Stage.Number switch
        {
            1 => $$"""{"summary":"framed for {{taskId}}","options":["option-a"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"none","excerpts":[],"repro":"none"}""",
            4 => $$"""{"plan":"edit files for {{taskId}}","manifest":["{{codeFile}}","{{testFile}}"]}""",
            5 => $$"""{"testFiles":["{{testFile}}"],"rationale":"red first for {{taskId}}"}""",
            6 => $$"""{"summary":"implemented {{taskId}}"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => $$"""{"summary":"verified","commitMessages":["feat: {{taskId}} feature","chore: {{taskId}} cleanup","test: {{taskId}} coverage"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// Wraps an inner <see cref="ScriptedSubagentRunner"/> and publishes trace-level
/// events to an optional <see cref="IRelayEventSink"/> — simulating what
/// SwivalSubagentRunner's trace tailer does when its eventSink is non-null.
/// When <c>traceSink</c> is null, trace events are silently dropped
/// (mirroring the GUI gap where planSubagentFactory doesn't pass an eventSink).
/// </summary>
internal sealed class TraceEmittingSubagentRunner(
    ScriptedSubagentRunner inner, IRelayEventSink? traceSink = null) : ISubagentRunner
{
    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (traceSink is not null)
        {
            await traceSink.PublishAsync(new RelayEvent(
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

        return await inner.RunAsync(invocation, cancellationToken);
    }
}
