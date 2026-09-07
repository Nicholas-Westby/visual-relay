using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Setup checks that fail while the tests pass, and the flag that ends the task there.
public sealed partial class RelayDriver
{
    /// <summary>How much of the failing command's own output rides in the flag reason.</summary>
    private const int EnvironmentFailureTailChars = 160;

    /// <summary>
    /// True when the authoritative gate is red ONLY because the repo guard failed,
    /// while the project's tests passed.
    /// <para>
    /// A guard that fails with green tests is the machine's verdict on the toolchain,
    /// not on the change: <c>GuardCommandDetector</c> appends build/format checks
    /// (<c>swift build</c>, <c>dotnet format --verify-no-changes</c>) to whatever
    /// policy scripts a repo has, and when one of those cannot run here no edit makes
    /// it green. Escalating Fix-verify through it spends the entire tier ladder
    /// (balanced → frontier → frontier, 200 → 400 → 800 turns) on the environment.
    /// The task is flagged with the guard's own output instead — never committed.
    /// </para>
    /// Two checks are deliberately excluded, because both only ever run BECAUSE of
    /// the change and are therefore attributable to it: the new-guard probe (it runs
    /// the guard scripts the task itself added) and the bootstrap check (it runs only
    /// when the manifest touches a bootstrap file — see ResolveBootstrapCheck).
    /// </summary>
    internal static bool IsEnvironmentSetupFailure(SetupCheckResults checks) =>
        checks.TestCheck != "red"
        && checks.NewGuardProbeCheck != "red"
        && checks.BootstrapCheck != "red"
        && checks.GuardCheck == "red";

    /// <summary>
    /// The flag reason for such a failure: names the check, quotes the command
    /// verbatim, and carries the tail of what it printed so the operator can act on
    /// it without opening the artifacts.
    /// </summary>
    internal static string BuildEnvironmentFailureReason(SetupCheckResults checks)
    {
        var command = checks.GuardCommand ?? "guard command";
        var reason = $"environment failure: guard '{command}' failed while the tests passed";
        var tail = OutputTail(checks.GuardOutput);
        return tail.Length == 0 ? reason : $"{reason} — {tail}";
    }

    /// <summary>The end of <paramref name="output"/> on one line, or empty.</summary>
    private static string OutputTail(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        var flat = string.Join(' ', output.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= EnvironmentFailureTailChars
            ? flat
            : "…" + flat[^EnvironmentFailureTailChars..];
    }

    /// <summary>
    /// Flags the task for an environment failure and announces it as one: a
    /// <c>warn</c>/<c>environment_failure</c> run-log event so an operator watching the
    /// drain sees the machine is at fault, not the task.
    /// </summary>
    private async Task<RelayTaskOutcome> FlagEnvironmentFailureAsync(
        string rootPath, string runId, string taskId, string taskDirectory, int stageNumber,
        SetupCheckResults checks, string? failureOutput,
        List<StageStatusEntry> statusEntries, CancellationToken cancellationToken,
        RelayCostEstimate? cost = null, TimeSpan? elapsed = null,
        double sessionCostUsd = 0, int unknownCostStageCount = 0)
    {
        var reason = BuildEnvironmentFailureReason(checks);
        var data = new Dictionary<string, string>
        {
            ["check"] = "guard",
            ["command"] = checks.GuardCommand ?? string.Empty,
            ["reason"] = reason
        };
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "environment_failure", runId, rootPath, taskId,
            stageNumber, Data: data), cancellationToken);

        return await FlagAsync(rootPath, runId, taskId, taskDirectory, stageNumber, reason,
            checks.ToSummaryLines() + "\n\n" + failureOutput, statusEntries, cancellationToken,
            cost, elapsed, sessionCostUsd, unknownCostStageCount);
    }
}
