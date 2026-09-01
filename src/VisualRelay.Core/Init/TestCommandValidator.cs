using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

/// <summary>
/// Smoke-runs a test command candidate to prove it can start on this machine
/// before the command is persisted to config.
/// </summary>
/// <remarks>
/// The validation timeout is owned by the supplied <see cref="ITestRunner"/>
/// (e.g. <see cref="DirectExecTestRunner"/>'s constructor timeout), which
/// time-boxes the process and surfaces <see cref="TestRunResult.TimedOut"/> for
/// <see cref="Classify"/> to reject. The validator therefore holds no timeout of
/// its own — adding one here would double-enforce and throw instead of producing
/// the TimedOut result the classification contract expects.
/// </remarks>
public sealed class TestCommandValidator(ITestRunner runner)
{
    /// <summary>
    /// Runs <paramref name="command"/> in <paramref name="rootPath"/> and
    /// classifies the result. Cancellation is forwarded to the runner.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(
        string rootPath,
        string command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runResult = await runner.RunAsync(rootPath, command, cancellationToken);
        return Classify(runResult);
    }

    /// <summary>
    /// Pure classification of a test-run result:
    ///   - Exit 0                          → accept
    ///   - Non-zero + test-style output    → accept (runner proven, tests may fail)
    ///   - TimedOut                        → reject
    ///   - Exit 127                        → reject (command not found)
    ///   - Non-zero + other output         → reject (missing script or usage error)
    ///   - Non-zero + no output            → reject (usage error)
    /// </summary>
    public static ValidationResult Classify(TestRunResult runResult)
    {
        // Timeout before meaningful output — reject.
        if (runResult.TimedOut)
        {
            return ValidationResult.Reject(
                $"test command timed out (timeout, exit code {runResult.ExitCode})",
                runResult);
        }

        var hasOutput = !string.IsNullOrWhiteSpace(runResult.Output);

        // Exit 0 — accept regardless of output shape.
        if (runResult.ExitCode == 0)
        {
            return ValidationResult.Accept(runResult);
        }

        // Exit 127 — command not found (ENOENT), whatever it printed.
        if (runResult.ExitCode == 127)
        {
            return ValidationResult.Reject(
                "command not found — the test runner is not installed or not on PATH",
                runResult);
        }

        // Non-zero with TEST-STYLE output — the runner is proven and its tests
        // merely failed. Any output at all is not enough: a repo with no test
        // script answers `npm test` with an npm error, and accepting that
        // persisted a command that can never pass as the repo's test command.
        if (hasOutput && LooksLikeTestOutput(runResult.Output))
        {
            return ValidationResult.Accept(runResult);
        }

        if (hasOutput)
        {
            return ValidationResult.Reject(
                $"command exited with code {runResult.ExitCode} and its output does not look "
                + "like a test run — it is probably a missing script or a usage error",
                runResult);
        }

        // Non-zero, no output — likely a usage error (command exists but arguments
        // are wrong, or the binary printed to a stream we didn't capture).
        return ValidationResult.Reject(
            $"command exited with code {runResult.ExitCode} and produced no test output",
            runResult);
    }

    /// <summary>
    /// Whether output plausibly came from a test runner rather than from a shell
    /// or package manager refusing to run one.
    /// <para>
    /// The refusals are the discriminating half: <c>npm</c> answers a missing
    /// script with "Missing script", a shell answers a missing binary with
    /// "command not found", and both were previously accepted as proof that a
    /// runner existed.
    /// </para>
    /// </summary>
    /// <param name="output">The captured output.</param>
    /// <returns>True when the output looks like a test run.</returns>
    private static bool LooksLikeTestOutput(string output)
    {
        foreach (var refusal in (string[])
                 ["missing script", "command not found", "no such file or directory",
                  "is not recognized as an internal or external command",
                  "could not determine executable to run", "unknown command",
                  "npm error", "no test specified"])
            if (output.Contains(refusal, StringComparison.OrdinalIgnoreCase))
                return false;

        foreach (var marker in (string[])
                 ["pass", "fail", "test", "spec", "assert", "ok ", "error:",
                  "expected", "✓", "✗"])
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}

/// <summary>
/// Result of smoke-validating a test command candidate.
/// </summary>
public sealed record ValidationResult(
    bool Accepted,
    string? RejectionReason,
    TestRunResult RunResult)
{
    public static ValidationResult Accept(TestRunResult runResult) =>
        new(true, null, runResult);

    public static ValidationResult Reject(string reason, TestRunResult runResult) =>
        new(false, reason, runResult);
}
