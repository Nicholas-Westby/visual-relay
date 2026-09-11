using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Companion to <see cref="TestCommandDetectorTests"/> — the per-file forms beyond
/// pytest and bun. Without one, a repository's stage-5 gate runs its WHOLE suite
/// and reports it as the targeted run; with the wrong one it runs nothing useful.
/// Each form is offered only when the detected command names that runner.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    /// <summary>
    /// A runner that takes test file paths as trailing arguments keeps the detected
    /// command — its flags are the project's own — and gains the token.
    /// </summary>
    [Theory]
    [InlineData("rspec", "rspec {files}")]
    [InlineData("bundle exec rspec", "bundle exec rspec {files}")]
    [InlineData("mocha", "mocha {files}")]
    [InlineData("npx mocha --require ts-node/register", "npx mocha --require ts-node/register {files}")]
    [InlineData("phpunit", "phpunit {files}")]
    [InlineData("vendor/bin/phpunit --colors=never", "vendor/bin/phpunit --colors=never {files}")]
    [InlineData("mix test", "mix test {files}")]
    public void PerFileForm_ARunnerTakingPaths_KeepsItsCommandAndGainsTheToken(string command, string expected) =>
        Assert.Equal(expected, TestCommandDetector.PerFileForm(command));

    /// <summary>
    /// jest and vitest take paths too, but the detected command is routinely a
    /// wrapper script and, for vitest, a WATCH-mode default that would never return.
    /// Their per-file form is the runner's own one-shot invocation instead.
    /// </summary>
    [Theory]
    [InlineData("vitest", "npx vitest run {files}")]
    [InlineData("vitest run --coverage", "npx vitest run {files}")]
    [InlineData("npx vitest", "npx vitest run {files}")]
    [InlineData("node_modules/.bin/vitest", "npx vitest run {files}")]
    [InlineData("jest", "npx jest {files}")]
    [InlineData("jest --ci", "npx jest {files}")]
    [InlineData("npx jest", "npx jest {files}")]
    public void PerFileForm_JestAndVitest_BecomeTheirOneShotInvocation(string command, string expected) =>
        Assert.Equal(expected, TestCommandDetector.PerFileForm(command));

    /// <summary>
    /// A runner the command does not name gets nothing, and a chain has no trailing
    /// argument list to extend however it ends.
    /// </summary>
    [Theory]
    [InlineData("bundle exec rake test")]
    [InlineData("npm test")]
    [InlineData("mix compile")]
    [InlineData("rspec && rubocop")]
    [InlineData("vitest run | tee out.txt")]
    public void PerFileForm_WithoutANamedRunnerOrATrailingList_IsNull(string command) =>
        Assert.Null(TestCommandDetector.PerFileForm(command));
}
