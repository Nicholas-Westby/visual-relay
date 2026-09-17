using VisualRelay.Core.Costs;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Setup checks that fail while the tests pass, and the flag that ends the task there.
public sealed partial class RelayDriver
{
    /// <summary>How much of the failing command's own output rides in the flag reason.</summary>
    private const int EnvironmentFailureTailChars = 160;

    /// <summary>What a reason built from an operating-system refusal opens with.</summary>
    private const string RefusedBeforeTestsPrefix = "the run failed before any test started: ";

    /// <summary>
    /// The operating system's own refusals, as the tools that hit them print them. On i18next
    /// vitest could not create a file under the snapshot's dependency folder, printed EACCES and
    /// exited 1 naming no test; "verify failed" then sent Fix-verify hunting for a test to repair,
    /// and it changed the real checkout's permissions instead.
    /// </summary>
    private static readonly string[] EnvironmentRefusals =
        ["EACCES", "permission denied", "operation not permitted", "read-only file system"];

    /// <summary>
    /// The reason a red run that named no failing test deserves: the refusal line itself when the
    /// operating system turned the run away before the suite started, else null (the caller's
    /// "verify failed"). A run that named a test is never one of these.
    /// </summary>
    private static string? RefusedBeforeTestsReason(TestRunResult result)
    {
        if (result.ExitCode == 0 || result.TimedOut || string.IsNullOrEmpty(result.Output)
            || TestFailureIds.Extract(result.Output).Count > 0)
            return null;
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (EnvironmentRefusals.Any(refusal => line.Contains(refusal, StringComparison.OrdinalIgnoreCase)))
                return RefusedBeforeTestsPrefix + FlagReason.OneLine(line);
        }

        return null;
    }

    /// <summary>
    /// The stage's failure output with that refusal named ahead of it, so Fix-verify's input says
    /// what the flag says instead of leaving the agent to find the line itself.
    /// </summary>
    private static string WithRefusedBeforeTests(TestRunResult result, string failureOutput) =>
        RefusedBeforeTestsReason(result) is { } reason ? reason + "\n\n" + failureOutput : failureOutput;

    /// <summary>
    /// The stage 10 flag reason. A whole-run reason stands on its own; anything else is the list
    /// of failing tests the base does not have.
    /// </summary>
    private static string VerifyFlagReason(string? newFailures) =>
        newFailures is null or "verify failed"
            || newFailures.StartsWith(RefusedBeforeTestsPrefix, StringComparison.Ordinal)
            ? newFailures ?? "verify failed"
            : $"new test failures: {newFailures}";

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
    private static bool IsEnvironmentSetupFailure(SetupCheckResults checks) =>
        checks.TestCheck != "red"
        && checks.NewGuardProbeCheck != "red"
        && checks.BootstrapCheck != "red"
        && checks.GuardCheck == "red";

    /// <summary>
    /// The flag reason for such a failure: names the check, quotes the command
    /// verbatim, and carries the tail of what it printed so the operator can act on
    /// it without opening the artifacts.
    /// </summary>
    private static string BuildEnvironmentFailureReason(SetupCheckResults checks)
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
