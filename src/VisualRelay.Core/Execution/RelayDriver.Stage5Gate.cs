using System.Text.RegularExpressions;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// The author-test gate. It runs whenever stage 5 declared test files and the
// command can run — there is no "nothing to strip, so skip it" short circuit any
// more — and hands the caller exactly one outcome per run.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Strips the manifest's implementation files that stage 5 did not declare as
    /// tests, runs the targeted command over the declared ones, restores, and
    /// judges the result. Returns the outcome plus the run itself (null when
    /// nothing ran) so the caller can report the duration and the output.
    /// </summary>
    private async Task<(AuthorTestGateOutcome Outcome, TestRunResult? Result)> RunAuthorTestGateAsync(
        string rootPath,
        string runId,
        string taskId,
        RelayStageDefinition stage,
        RelayConfig config,
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> testFiles,
        IReadOnlyList<AuthorTestScopeVerdict> verdicts,
        bool reaskUsed,
        CancellationToken cancellationToken)
    {
        var command = config.TestFileCommand.Replace(
            "{files}", string.Join(' ', testFiles), StringComparison.Ordinal);

        if (testFiles.Count == 0)
            return (AuthorTestGateOutcome.Unproven(
                AuthorTestGateOutcome.NoTestFilesDeclared, command, verdicts), null);

        // The bootstrap placeholder exits 0 having run nothing, so running it
        // would manufacture a green the change never earned.
        if (ProjectBootstrapper.IsPlaceholder(command) || ProjectBootstrapper.IsPlaceholder(config.TestCommand))
        {
            var placeholder = AuthorTestGateOutcome.Unproven(
                AuthorTestGateOutcome.PlaceholderTestCommand, command, verdicts);
            await PublishGateUnusableAsync(rootPath, runId, taskId, stage, placeholder, null, cancellationToken);
            return (placeholder, null);
        }

        var stripSet = RedGate.ComputeStripSet(rootPath, manifest, testFiles);
        var gate = await AuthorTestGate.RunAsync(rootPath, taskId, runId, manifest, testFiles, command,
            _dependencies.TestRunner, _dependencies.GitInvoker, cancellationToken);
        if (gate.Error is not null)
            return (AuthorTestGateOutcome.Flagged(gate.Error, command, verdicts), null);
        if (gate.RestoreResult == RedGateRestoreResult.Conflict)
            return (AuthorTestGateOutcome.Flagged("red gate stash restore conflict", command, verdicts), null);

        var result = gate.TestResult;
        if (result.TimedOut)
            return (AuthorTestGateOutcome.Flagged(
                ErrorHintClassifier.WithHint(result.Output), command, verdicts), null);

        var unusable = GateUsability.IsUnusable(result);
        var outcome = AuthorTestGateOutcome.ForRun(
            command, result, unusable, gate.StashedImplementation ? stripSet : [], verdicts, reaskUsed);
        if (unusable)
            await PublishGateUnusableAsync(rootPath, runId, taskId, stage, outcome, result, cancellationToken);
        return (outcome, result);
    }

    /// <summary>
    /// Publishes the stage-5 <c>verify_result</c>: the same structured record
    /// stages 9-11 emit, carrying the targeted command instead of the full suite
    /// plus what was stripped and how each declared file was classified.
    /// </summary>
    private Task PublishAuthorTestVerifyResultAsync(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayStageDefinition stage,
        int attempt,
        RelayConfig config,
        AuthorTestGateOutcome outcome,
        TestRunResult? result,
        IReadOnlyList<string> manifest,
        CancellationToken cancellationToken)
    {
        var extraData = new Dictionary<string, string>
        {
            ["strippedFiles"] = string.Join(',', outcome.StrippedFiles),
            ["scope"] = AuthorTestScope.Describe(outcome.Verdicts)
        };
        // A red's reason is distilled from the command's own output by the shared
        // publisher; every other outcome states why it could prove nothing.
        if (outcome.Reason is not null)
            extraData["reason"] = outcome.Reason;

        return PublishVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage, attempt, config,
            result ?? new TestRunResult(0, string.Empty), manifest, cancellationToken,
            overrideCheck: outcome.CheckName,
            commandOverride: outcome.Command,
            extraData: extraData,
            includeExitCode: outcome.ExitCode is not null);
    }

    private Task PublishGateUnusableAsync(
        string rootPath,
        string runId,
        string taskId,
        RelayStageDefinition stage,
        AuthorTestGateOutcome outcome,
        TestRunResult? result,
        CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>
        {
            ["command"] = outcome.Command,
            ["reason"] = outcome.Reason ?? string.Empty
        };
        if (result is not null)
        {
            data["exitCode"] = result.ExitCode.ToString();
            data["outputTail"] = result.Output.Length > 200 ? result.Output[^200..] : result.Output;
        }

        return PublishStage5EventAsync("warn", "author_test_gate_unusable",
            rootPath, runId, taskId, stage, data, cancellationToken);
    }

}
