using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A suite longer than bootstrap's check is not a broken command. Measured on the Mac with
/// openai/openai-agents-python: <c>.venv/bin/pytest -q</c> printed "1558 passed, 6 skipped in
/// 59.19s" when the 60 s check stopped it, was rejected, and bootstrap left the placeholder, a test
/// command that passes having run nothing. Windows saw the same with apache/commons-lang.
/// </summary>
public sealed partial class TestCommandValidatorTests
{
    private const string PytestStoppedAtTheLimit =
        "test command timed out after 60000ms\n\n"
        + "........................................................................ [ 15%]\n"
        + "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!! KeyboardInterrupt !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!\n"
        + "1558 passed, 6 skipped in 59.19s\n";

    [Fact]
    public async Task ValidateAsync_ASuiteStillRunningTestsAtTheLimit_IsAccepted()
    {
        var validator = new TestCommandValidator(
            new ScriptedTestRunner(new TestRunResult(-1, PytestStoppedAtTheLimit, TimedOut: true)));

        var result = await validator.ValidateAsync("/tmp/repo", ".venv/bin/pytest -q");

        Assert.True(result.Accepted, result.RejectionReason);
    }

    /// <summary>A watch mode prints its results and then waits forever, so a JavaScript script stays rejected.</summary>
    /// <param name="command">A package-manager test script.</param>
    [Theory]
    [InlineData("npm test")]
    [InlineData("pnpm run test")]
    [InlineData("CI=1 yarn test")]
    public async Task ValidateAsync_AJavaScriptScriptAtTheLimit_StaysRejected(string command)
    {
        const string output = "test command timed out after 60000ms\n\n"
            + " ✓ src/math.test.ts (3 tests) 4ms\n Test Files  1 passed (1)\n Watching for file changes...\n";
        var validator = new TestCommandValidator(new ScriptedTestRunner(new TestRunResult(-1, output, TimedOut: true)));

        Assert.False((await validator.ValidateAsync("/tmp/repo", command)).Accepted);
    }

    /// <summary>
    /// Output that merely mentions a failure is not a test run: the setup check keeps rejecting a
    /// command that printed no test count or progress before the limit stopped it.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ACommandThatPrintedNoTestCountBeforeTheLimit_StaysRejected()
    {
        var validator = new TestCommandValidator(new ScriptedTestRunner(
            new TestRunResult(1, "some stderr\nfrom failing command\n", TimedOut: true)));

        Assert.False((await validator.ValidateAsync("/tmp/repo", "go test ./...")).Accepted);
    }

    [Theory]
    [InlineData("Tests run: 412, Failures: 0, Errors: 0, Skipped: 3, Time elapsed: 58.2 s\n")]
    [InlineData("ok  \tgithub.com/example/pkg\t12.041s\n")]
    [InlineData("test result: ok. 118 passed; 0 failed; 0 ignored\n")]
    [InlineData("Passed!  - Failed:     0, Passed:   920, Skipped:     0, Total:   920\n")]
    [InlineData("412 examples, 0 failures\n")]
    public async Task ValidateAsync_ATestCountFromAnotherRunnerAtTheLimit_IsAccepted(string output)
    {
        var validator = new TestCommandValidator(new ScriptedTestRunner(new TestRunResult(-1, output, TimedOut: true)));

        Assert.True((await validator.ValidateAsync("/tmp/repo", "make check")).Accepted);
    }

    [Fact]
    public async Task ValidateAsync_ACommandThatPrintedNothingBeforeTheLimit_StaysRejected()
    {
        var validator = new TestCommandValidator(new ScriptedTestRunner(
            new TestRunResult(-1, "test command timed out after 60000ms\n\n", TimedOut: true)));

        Assert.False((await validator.ValidateAsync("/tmp/repo", "make test")).Accepted);
    }
}
