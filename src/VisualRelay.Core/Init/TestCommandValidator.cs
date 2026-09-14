using System.Text.RegularExpressions;
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
        var result = Classify(runResult);
        return !result.Accepted && runResult.TimedOut && RanTestsUntilTheLimit(command, runResult.Output)
            ? ValidationResult.Accept(runResult)
            : result;
    }

    /// <summary>A package manager running a JavaScript script, whose test runner may be in watch mode.</summary>
    private static readonly Regex PackageScript = new(
        @"^\s*(?:\S+=\S*\s+)*(?:npm|npx|yarn|pnpm|bunx?)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// A test count or progress line a runner prints while running: pytest, rspec and minitest counts,
    /// pytest's percent progress, Maven's "Tests run:", go's package lines, cargo's result, dotnet's summary.
    /// </summary>
    private static readonly Regex TestRunProgress = new(
        @"\b\d+ (?:passed|failed|errors?|skipped|tests?|examples?|runs?|specs?)\b|\[\s*\d+%\]|\bTests run: \d+"
        + @"|^(?:ok|FAIL|---\s(?:PASS|FAIL):)\s|\btest result: |^\s*(?:Passed|Failed)!\s",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// A build tool's progress while it compiles before any test runs: cargo's "Compiling crate v1.2.3",
    /// Gradle's "> Task :core:compileKotlin", Maven's "[INFO] Compiling 264 source files", dotnet's
    /// "Restored x.csproj (in 939 ms)." and "LiteDB -> x.dll", CMake's "[ 20%] Building CXX object".
    /// </summary>
    private static readonly Regex BuildProgress = new(
        @"^\s+Compiling \S+ v\d|^> Task :\S+|^\[INFO\] Compiling \d+ source files?\b"
        + @"|^\s+Restored \S.*\.(?:cs|fs|vb)proj \(in \d+ m?s\)\.|^\s+\S+ -> \S.*\.dll\r?$|^\[\s*\d+%\] Building \S+ object ",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// A terminal control sequence, such as the colour Maven forces with <c>-Djansi.mode=force</c>: it
    /// sits between "Tests run: " and the count, so the counts are read with these taken out.
    /// </summary>
    private static readonly Regex AnsiSequence = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a command stopped at the check's time limit was running tests, or still building them,
    /// when it was stopped. A suite longer than the check is not a broken command: measured with
    /// openai-agents-python, pytest printed "1558 passed, 6 skipped in 59.19s" at the 60 s limit and was
    /// rejected, which left bootstrap's placeholder. Nor is a first build longer than the check: a fresh
    /// zeroshot clone's cargo test was still compiling dependencies, was rejected, and bootstrap took a
    /// 4-test tooling script instead. A JavaScript package script stays rejected, since a watch mode
    /// prints results and then never exits, and so does output with neither a test count nor a build
    /// step, which a hung command can print too. The runner's own "timed out" line is not the command's output.
    /// </summary>
    private static bool RanTestsUntilTheLimit(string command, string output)
    {
        if (PackageScript.IsMatch(command))
            return false;
        var firstLineEnd = output.IndexOf('\n');
        var printed = AnsiSequence.Replace(output.StartsWith("test command timed out", StringComparison.Ordinal)
            ? firstLineEnd < 0 ? string.Empty : output[(firstLineEnd + 1)..]
            : output, string.Empty);
        return (TestRunProgress.IsMatch(printed) && LooksLikeTestOutput(printed))
            || (BuildProgress.IsMatch(printed) && !Refuses(printed));
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

        // Exit 0 — accept regardless of output shape, except ctest saying the build registered no
        // tests: a suite behind a disabled CMake option (quill's QUILL_BUILD_TESTS) ran nothing.
        if (runResult.ExitCode == 0)
        {
            return hasOutput && runResult.Output.Contains("No tests were found", StringComparison.Ordinal)
                ? ValidationResult.Reject("ctest found no tests: the build registered none, so the command ran nothing", runResult)
                : ValidationResult.Accept(runResult);
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
        // A build tool refusing an unknown task names it, and the task is called test, so
        // these must be caught before the markers: grape's `rake test` otherwise passed.
        if (Refuses(output))
            return false;

        foreach (var marker in (string[])
                 ["pass", "fail", "test", "spec", "assert", "ok ", "error:",
                  "expected", "✓", "✗"])
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Whether a shell or build tool refused to run the command at all.</summary>
    private static bool Refuses(string output) =>
        ((string[])
        ["missing script", "command not found", "no such file or directory",
         "is not recognized as an internal or external command",
         "could not determine executable to run", "unknown command",
         "npm error", "no test specified", "don't know how to build task",
         "no rule to make target", "not found in root project"])
        .Any(refusal => output.Contains(refusal, StringComparison.OrdinalIgnoreCase));
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
