using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The classification half: what counts as proof that a test runner exists.
/// Accepting any output at all persisted commands that can never pass.
/// </summary>
public sealed partial class TestCommandValidatorTests
{
    /// <summary>
    /// A repo with no test script answers `npm test` with an npm error. That is
    /// not a test run, and accepting it persisted a command that can never pass
    /// as the repository's test command.
    /// </summary>
    /// <param name="output">The refusal a runner-less repo actually prints.</param>
    /// <remarks>
    /// A build tool refusing an unknown task names the task, and the task is called test,
    /// so the refusal read as test output: measured with ruby-grape/grape, bootstrap kept
    /// <c>bundle exec rake test</c>, which answers "Don't know how to build task 'test'".
    /// </remarks>
    [Theory]
    [InlineData("npm error Missing script: \"test\"\nnpm error\nnpm error Did you mean one of these?")]
    [InlineData("sh: gradle: command not found")]
    [InlineData("bash: pytest: No such file or directory")]
    [InlineData("Error: Missing script: test")]
    [InlineData("npm ERR! missing script: test")]
    [InlineData("rake aborted!\nDon't know how to build task 'test' (See the list of available tasks with `rake --tasks`)")]
    [InlineData("make: *** No rule to make target 'test'.  Stop.")]
    [InlineData("FAILURE: Build failed with an exception.\n\n* What went wrong:\nTask 'test' not found in root project 'demo'.")]
    public void Classify_NonZeroExitWithARefusal_Rejects(string output)
    {
        var result = TestCommandValidator.Classify(new TestRunResult(1, output));

        Assert.False(result.Accepted);
        Assert.NotNull(result.RejectionReason);
    }

    /// <summary>
    /// Exit 127 is a missing binary whatever it printed, so it rejects even when
    /// the shell was chatty about it.
    /// </summary>
    [Fact]
    public void Classify_ExitOneTwentySevenWithOutput_Rejects()
    {
        var result = TestCommandValidator.Classify(
            new TestRunResult(127, "some shells print a hint here"));

        Assert.False(result.Accepted);
        Assert.Contains("not installed", result.RejectionReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Output that resembles neither a test run nor a known refusal is rejected:
    /// the runner has not been proven, and guessing costs a whole run.
    /// </summary>
    [Fact]
    public void Classify_NonZeroExitWithUnrelatedOutput_Rejects()
    {
        var result = TestCommandValidator.Classify(
            new TestRunResult(2, "usage: mytool [-v] <path>"));

        Assert.False(result.Accepted);
        Assert.Contains("does not look", result.RejectionReason!, StringComparison.Ordinal);
    }

    /// <summary>Genuine failing-test output from several runners still accepts.</summary>
    /// <param name="output">Real output from a runner whose tests failed.</param>
    [Theory]
    [InlineData("FAIL: 3 tests failed\n  1) add works")]
    [InlineData("Tests run: 12, Failures: 1, Errors: 0")]
    [InlineData("=== 2 failed, 5 passed in 0.42s ===")]
    [InlineData("Failed! - Failed: 1, Passed: 1857, Skipped: 0")]
    [InlineData("✗ multiply returns a product")]
    public void Classify_NonZeroExitWithRealTestOutput_Accepts(string output)
    {
        var result = TestCommandValidator.Classify(new TestRunResult(1, output));

        Assert.True(result.Accepted, result.RejectionReason);
    }

    /// <summary>
    /// ctest exits 0 when the build registered no tests, so a CMake project whose suite is behind a
    /// disabled option looked like a passing run. Found bootstrapping odygrd/quill on the Mac.
    /// </summary>
    [Fact]
    public void Classify_ExitZeroWithCtestFindingNoTests_Rejects()
    {
        var result = TestCommandValidator.Classify(new TestRunResult(0,
            "Test project /repo/build\nNo tests were found!!!\n"));

        Assert.False(result.Accepted);
        Assert.Contains("no tests", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }
}
