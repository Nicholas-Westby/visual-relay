using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Combined verify-failure assembly for the fix-verify loop. Split out of
// RelayDriver.RepoGuards.cs (file-size split) so the distilled-tail vs complete-log
// pair lives in one focused place: BuildFailureOutput feeds the in-prompt tail,
// BuildFullFailureOutput feeds the persisted "complete log" artifact, and both share
// BuildCombinedFailure so their section order and wording can never drift apart.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Builds the DISTILLED combined failure output handed to the fix-verify agent as the
    /// in-prompt tail — test (failure reason only), guard, new-guard-probe, and bootstrap
    /// failures. The test portion is the distilled <see cref="SwivalSubagentRunner.ExtractFailureReason"/>
    /// so the tail stays legible; the FULL version persisted to the artifact is
    /// <see cref="BuildFullFailureOutput"/>.
    /// </summary>
    private static string BuildFailureOutput(
        TestRunResult testResult,
        string? guardOutput,
        bool bootstrapFailed,
        string? bootstrapFailureOutput,
        string? newGuardOutput = null) =>
        BuildCombinedFailure(
            testResult.ExitCode != 0 ? SwivalSubagentRunner.ExtractFailureReason(testResult.Output) : null,
            guardOutput, bootstrapFailed, bootstrapFailureOutput, newGuardOutput);

    /// <summary>
    /// Builds the COMPLETE combined failure log persisted to the verify-output artifact:
    /// the same sources <see cref="BuildFailureOutput"/> summarizes, but with the FULL
    /// untrimmed test output instead of the distilled reason. The persisted file is then the
    /// full version of the trimmed tail the prompt shows — so "read it for the complete log"
    /// genuinely yields more than the tail, and a guard/bootstrap failure (where the test
    /// command itself passed) is present in the file rather than missing.
    /// </summary>
    private static string BuildFullFailureOutput(
        TestRunResult testResult,
        string? guardOutput,
        bool bootstrapFailed,
        string? bootstrapFailureOutput,
        string? newGuardOutput = null) =>
        BuildCombinedFailure(
            testResult.ExitCode != 0 ? testResult.Output : null,
            guardOutput, bootstrapFailed, bootstrapFailureOutput, newGuardOutput);

    /// <summary>
    /// Shared assembly for <see cref="BuildFailureOutput"/> (distilled tail) and
    /// <see cref="BuildFullFailureOutput"/> (complete persisted log): joins the optional
    /// test-failure text with the guard / new-guard-probe / bootstrap sections in a stable
    /// order. <paramref name="testFailureText"/> is null when the test command itself passed
    /// (a guard/bootstrap-only failure), which also selects the bootstrap section's wording.
    /// </summary>
    private static string BuildCombinedFailure(
        string? testFailureText,
        string? guardOutput,
        bool bootstrapFailed,
        string? bootstrapFailureOutput,
        string? newGuardOutput)
    {
        var parts = new List<string>();
        if (testFailureText is not null)
            parts.Add(testFailureText);
        if (guardOutput is not null)
            parts.Add("--- Guard check output ---\n" + guardOutput);
        if (newGuardOutput is not null)
            parts.Add("--- New guard probe ---\n" + newGuardOutput);
        if (bootstrapFailed && bootstrapFailureOutput is not null)
        {
            if (testFailureText is not null)
                parts.Add("--- Bootstrap check output ---\n" + bootstrapFailureOutput);
            else
                parts.Add("Bootstrap check failed:\n" + bootstrapFailureOutput);
        }
        return string.Join("\n\n", parts);
    }
}
