using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    // Combined result from the stage-9 pre-agent mechanical test gate.
    private sealed record Stage9PreAgentData(
        TestRunResult TestResult,
        double TestDurationSeconds,
        bool BootstrapFailed,
        string? BootstrapFailureOutput,
        string? BootstrapCmd,
        string? NewGuardOutput,
        bool GuardFailed,
        string? GuardOutput);

    /// <summary>
    /// Runs the mechanical verify gate (bootstrap, guard probe, guard, test suite)
    /// BEFORE the Verify agent runs, so the agent receives captured output instead
    /// of re-running the suite itself. Returns (null, errorHint) on timeout,
    /// or (data, null) on success. Caller passes errorHint to FlagAsync.
    /// </summary>
    // ReSharper disable UnusedParameter.Local — kept for future use in pre-agent logic
    private async Task<(Stage9PreAgentData? Data, string? ErrorHint)> RunStage9PreAgentAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayConfig config, IReadOnlyList<string> manifest, StringBuilder ledger,
        List<StageStatusEntry> statusEntries, CancellationToken cancellationToken)
    // ReSharper restore UnusedParameter.Local
    {
        var (shouldRunBootstrap, bootstrapCmd) = ResolveBootstrapCheck(config, manifest);
        var bootstrapCmdStr = shouldRunBootstrap ? bootstrapCmd : null;
        var bootstrapFailed = false;
        string? bootstrapFailureOutput = null;

        if (shouldRunBootstrap)
        {
            var bootstrapResult = await _dependencies.TestRunner.RunAsync(rootPath, bootstrapCmd, cancellationToken);
            if (bootstrapResult.TimedOut)
                return (null, ErrorHintClassifier.WithHint(bootstrapResult.Output));
            if (bootstrapResult.ExitCode != 0)
            {
                bootstrapFailed = true;
                bootstrapFailureOutput = bootstrapResult.Output;
            }
        }

        var (newGuardOutput, probeTimedOut) = await NewGuardProbeAsync(
            rootPath, manifest, config.NewGuardPatterns, cancellationToken);
        if (probeTimedOut)
            return (null, ErrorHintClassifier.WithHint(newGuardOutput ?? "new guard timed out"));

        var (guardFailed, guardOutput, guardTimedOut) = await IntegrateGuardAsync(
            rootPath, taskId, runId, config, ledger, cancellationToken);
        if (guardTimedOut)
            return (null, ErrorHintClassifier.WithHint(guardOutput ?? "guard timed out"));

        var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(
            rootPath, config, stageNumber: 9, attempt: 1, runId, taskId, cancellationToken);
        await EmitMutatedTreeAdvisoryAsync(rootPath, runId, taskId,
            RelayStages.All[8], verifyMutations, cancellationToken);

        if (testResult.TimedOut)
            return (null, ErrorHintClassifier.WithHint(testResult.Output));

        return (new Stage9PreAgentData(
            testResult, testResult.Elapsed.TotalSeconds,
            bootstrapFailed, bootstrapFailureOutput, bootstrapCmdStr,
            newGuardOutput, guardFailed, guardOutput), null);
    }
}
