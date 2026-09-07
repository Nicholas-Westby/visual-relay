using System.Collections.Concurrent;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Deciding whether a red guard is the change's doing or the machine's.
public sealed partial class RelayDriver
{
    /// <summary>
    /// What the repository's guard command did on an UNTOUCHED checkout of one base
    /// commit: <c>true</c> it passed, <c>false</c> it failed, <c>null</c> the question
    /// could not be answered (no git, or the checkout failed). Keyed by repo root, base
    /// commit and guard command, and static so a whole drain pays for the probe once per
    /// distinct answer instead of once per task — the checkout plus a full guard run is
    /// the most expensive thing this decision can cost. A base commit's content never
    /// changes, so a cached verdict can never go stale; the key moves on by itself as
    /// each committed task advances the base.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<bool?>>> BaseGuardVerdicts =
        new(StringComparer.Ordinal);

    /// <summary>
    /// True when a red gate is the ENVIRONMENT's fault rather than the change's.
    /// <para>
    /// The precondition is <see cref="IsEnvironmentSetupFailure"/> — green tests with
    /// only the guard red. That alone is not proof: a lint, format, size or hermeticity
    /// guard tripping on newly written code is precisely what Fix-verify repairs. So the
    /// same guard command is run once against a pristine checkout of the run base. Failing
    /// there too means no edit can make it green (the toolchain, not the task), and the
    /// caller flags. Passing there means the change broke it, and the caller escalates
    /// exactly as it did before. An undeterminable base is attributed to the change:
    /// ending a task early needs positive evidence, and <see cref="BaselineGuardGate"/>
    /// already screens a guard that cannot pass here before any task runs.
    /// </para>
    /// </summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="runId">The current run id, for the verdict event.</param>
    /// <param name="taskId">The task being decided.</param>
    /// <param name="stageNumber">The stage asking (10 at the gate, 11 in the loop).</param>
    /// <param name="runBaseSha">The commit the run started from, when it was captured.</param>
    /// <param name="config">The loaded relay configuration.</param>
    /// <param name="checks">The setup-check breakdown for this attempt.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    private async Task<bool> IsEnvironmentGuardFailureAsync(
        string rootPath, string runId, string taskId, int stageNumber, string? runBaseSha,
        RelayConfig config, SetupCheckResults checks, CancellationToken cancellationToken)
    {
        if (!IsEnvironmentSetupFailure(checks)) return false;
        var guardCommand = config.GuardCommand;
        if (string.IsNullOrWhiteSpace(guardCommand)) return false;

        var baseSha = await ResolveGuardBaseShaAsync(rootPath, runBaseSha, cancellationToken);
        var basePassed = baseSha is null
            ? null
            : await BaseGuardVerdictAsync(rootPath, runId, taskId, baseSha, guardCommand, cancellationToken);

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "guard_attribution", runId, rootPath, taskId,
            stageNumber, Data: new Dictionary<string, string>
            {
                ["command"] = guardCommand,
                ["base"] = baseSha ?? "unknown",
                ["verdict"] = basePassed switch
                {
                    false => "environment",
                    true => "change",
                    _ => "undetermined"
                }
            }), cancellationToken);

        return basePassed == false;
    }

    /// <summary>
    /// The commit to check out for the probe: the run base when the run recorded one,
    /// otherwise the current HEAD resolved to a sha. Always a concrete sha (never the
    /// moving <c>HEAD</c>), so a cached verdict belongs to exactly one tree. Null when
    /// the repo has no resolvable commit, in which case no probe is possible.
    /// </summary>
    private async Task<string?> ResolveGuardBaseShaAsync(
        string rootPath, string? runBaseSha, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(runBaseSha)) return runBaseSha.Trim();
        var head = await _dependencies.GitInvoker.RunAsync(
            rootPath, ["rev-parse", "HEAD"], cancellationToken);
        if (head.ExitCode != 0) return null;
        var sha = head.Output.Trim();
        return sha.Length == 0 ? null : sha;
    }

    /// <summary>
    /// The cached verdict for one (repo, base, guard command), probing at most once.
    /// A probe that faults (cancellation) drops its entry so the next caller re-asks.
    /// </summary>
    private Task<bool?> BaseGuardVerdictAsync(
        string rootPath, string runId, string taskId, string baseSha, string guardCommand,
        CancellationToken cancellationToken)
    {
        var key = string.Join('\n', Path.GetFullPath(rootPath), baseSha, guardCommand);
        var entry = BaseGuardVerdicts.GetOrAdd(key, _ => new Lazy<Task<bool?>>(
            () => ProbeBaseGuardAsync(rootPath, runId, taskId, baseSha, guardCommand, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitVerdictAsync(entry, key);
    }

    private static async Task<bool?> AwaitVerdictAsync(Lazy<Task<bool?>> entry, string key)
    {
        try
        {
            return await entry.Value;
        }
        catch
        {
            BaseGuardVerdicts.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>
    /// Runs the guard command once in a throwaway checkout of <paramref name="baseSha"/>
    /// carrying none of the task's edits. The checkout gets the same git-ignored runtime
    /// overlay the verify snapshot gets, so a guard that needs installed dependencies is
    /// judged on the guard, not on a missing <c>node_modules</c>. Returns null when the
    /// checkout could not be made.
    /// </summary>
    private async Task<bool?> ProbeBaseGuardAsync(
        string rootPath, string runId, string taskId, string baseSha, string guardCommand,
        CancellationToken cancellationToken)
    {
        var worktreeId = $"{taskId}-guard-base";
        string? worktreePath = null;
        try
        {
            // Fresh token, like the teardown below: a half-added worktree is a git
            // admin entry pointing at a directory that was never checked out, and it
            // leaves this method holding no path to remove.
            worktreePath = await PlanningWorktree.CreateAsync(
                rootPath, worktreeId, runId, _dependencies.GitInvoker, CancellationToken.None,
                timeProvider: _dependencies.TimeProvider, commitish: baseSha);
            await OverlayIgnoredEntriesAsync(
                rootPath, worktreePath, worktreeId, runId,
                IgnoredOverlayCopyMaxBytes, cloneOverlay: true, cancellationToken);
            var result = await _dependencies.TestRunner.RunAsync(
                worktreePath, guardCommand, cancellationToken);
            return result is { TimedOut: false, ExitCode: 0 };
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled run is the caller's business, never a verdict
        }
        catch
        {
            return null; // not a git repo, or the checkout failed → cannot attribute
        }
        finally
        {
            if (worktreePath is not null)
                await CleanupVerifyWorktreeAsync(rootPath, worktreePath);
        }
    }
}
