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

        var stripSet = RedGate.ComputeStripSet(manifest, testFiles);
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

        var unusable = IsGateUnusable(result);
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

    /// <summary>Restore and dependency resolution failures: NuGet, Maven, Gradle, pip.</summary>
    private static readonly Regex DependencyFetchFailure =
        new(@"Failed to read NuGet\.Config|error NU1301:|Could not resolve dependencies for project"
            + "|Could not resolve all (?:dependencies|files) for configuration|No matching distribution found for",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A build tool that stopped while setting itself up, before any task or test ran.</summary>
    private static readonly Regex BuildToolStartFailure =
        new(@"Gradle could not start your build\.", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Zero-tests pattern: "0 tests" / "0 tests collected" / "ran 0 tests"
    /// but NOT "10 tests" / "230 tests" / "Ran 100 tests".  The regex
    /// requires the zero to be a standalone number (not preceded by another
    /// digit).
    /// </summary>
    private static readonly Regex ZeroTestsPattern =
        new(@"(?<!\d)0\s+tests",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the test runner could not execute meaningfully (command
    /// not found, or zero tests collected), making the red-gate assertion
    /// untrustworthy. Avoids silently passing a gate whose infrastructure is
    /// broken — independent of any specific toolchain.
    /// </summary>
    private static bool IsGateUnusable(TestRunResult result)
    {
        // Exit code 127 = command not found (POSIX convention; also followed
        // by many shells and process runners on non-POSIX platforms).
        if (result.ExitCode == 127)
            return true;

        // Zero-tests-collected patterns produced by common runners when the
        // command can start but finds no tests to execute. The heuristic is
        // intentionally loose (case-insensitive) — a false positive here
        // records an unproven gate rather than passing it vacuously.
        var output = result.Output;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        // A run that could not fetch its dependencies never reached the tests, so its exit
        // code says nothing about them. Measured with LiteDB in WSL: the sandbox denied NuGet
        // its user config and the gate took the restore failure for the new test's red. A
        // compile error the new test causes is not among these: that is a legitimate red.
        if (DependencyFetchFailure.IsMatch(output))
            return true;

        // Nor did a build tool that could not start. Measured with Unciv in WSL: gradle could not open
        // its own file hash lock, stopped in 556 ms with nothing compiled, and the gate took it for red.
        if (BuildToolStartFailure.IsMatch(output))
            return true;

        // A run that names its failing tests ran tests, whatever else it printed. Measured on the Windows
        // arm with zeroshot's cargo workspace: members without tests print "running 0 tests", and the
        // zero-tests checks below recorded two genuine failures as an unproven gate.
        if (TestFailureIds.Extract(output).Count > 0)
            return false;

        return output.Contains("no tests found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no tests collected", StringComparison.OrdinalIgnoreCase)
            || ZeroTestsPattern.IsMatch(output)
            || output.Contains("zero tests", StringComparison.OrdinalIgnoreCase);
    }
}
