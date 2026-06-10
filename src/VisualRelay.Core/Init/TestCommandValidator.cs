using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

/// <summary>
/// Smoke-runs a test command candidate to prove it can start on this machine
/// before the command is persisted to config.
/// </summary>
public sealed class TestCommandValidator
{
    private readonly ITestRunner _runner;
    private readonly TimeSpan _timeout;

    public TestCommandValidator(ITestRunner runner, TimeSpan? timeout = null)
    {
        _runner = runner;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

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
        var runResult = await _runner.RunAsync(rootPath, command, cancellationToken);
        return Classify(runResult);
    }

    /// <summary>
    /// Pure classification of a test-run result:
    ///   - Exit 0                          → accept
    ///   - Non-zero + test-style output    → accept (runner proven, tests may fail)
    ///   - TimedOut                        → reject
    ///   - Exit 127 + no output            → reject (command not found)
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

        // Non-zero with output — runner is proven (tests may legitimately fail).
        if (hasOutput)
        {
            return ValidationResult.Accept(runResult);
        }

        // Exit 127 with no output — command not found (ENOENT).
        if (runResult.ExitCode == 127)
        {
            return ValidationResult.Reject(
                "command not found — the test runner is not installed or not on PATH",
                runResult);
        }

        // Non-zero, no output — likely a usage error (command exists but arguments
        // are wrong, or the binary printed to a stream we didn't capture).
        return ValidationResult.Reject(
            $"command exited with code {runResult.ExitCode} and produced no test output",
            runResult);
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
