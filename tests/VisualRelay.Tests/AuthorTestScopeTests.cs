using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Stage 5's scope check: every file the model called a test is either a real
/// separate test file, a file whose language keeps tests inline, or neither.
/// </summary>
public sealed class AuthorTestScopeTests
{
    private static RelayConfig Config(string[]? inlineExtensions = null, string[]? testPaths = null) =>
        RelayConfigLoader.Defaults() with
        {
            AuthorTests = new AuthorTestsConfig([], inlineExtensions ?? [], AuthorTestsConfig.DiffAuditAuto),
            TestPaths = testPaths ?? []
        };

    [Fact]
    public void A_classifier_match_is_separate_even_when_its_extension_is_inline()
    {
        var verdicts = AuthorTestScope.Classify(["tests/color.rs"], Config([".rs"]));

        Assert.Equal([new AuthorTestScopeVerdict("tests/color.rs", AuthorTestScopeKind.Separate)], verdicts);
    }

    [Fact]
    public void An_inline_extension_outside_a_test_path_is_inline_capable()
    {
        var verdicts = AuthorTestScope.Classify(["src/control.rs"], Config([".rs"]));

        Assert.Equal(AuthorTestScopeKind.InlineCapable, verdicts[0].Kind);
    }

    [Fact]
    public void Anything_else_is_suspect()
    {
        var verdicts = AuthorTestScope.Classify(["src/mux.go"], Config([".rs"]));

        Assert.Equal(AuthorTestScopeKind.Suspect, verdicts[0].Kind);
    }

    [Fact]
    public void The_inline_extension_match_ignores_case()
    {
        var verdicts = AuthorTestScope.Classify(["src/Control.RS"], Config([".rs"]));

        Assert.Equal(AuthorTestScopeKind.InlineCapable, verdicts[0].Kind);
    }

    [Fact]
    public void A_configured_test_glob_makes_a_file_separate()
    {
        var verdicts = AuthorTestScope.Classify(["examples/smoke.go"], Config(testPaths: ["examples/**"]));

        Assert.Equal(AuthorTestScopeKind.Separate, verdicts[0].Kind);
    }

    [Fact]
    public void Verdicts_keep_the_order_the_model_listed()
    {
        var verdicts = AuthorTestScope.Classify(
            ["tests/a_test.go", "src/x.rs", "src/y.go"], Config([".rs"]));

        Assert.Equal(
            [AuthorTestScopeKind.Separate, AuthorTestScopeKind.InlineCapable, AuthorTestScopeKind.Suspect],
            verdicts.Select(v => v.Kind));
        Assert.Equal(["tests/a_test.go", "src/x.rs", "src/y.go"], verdicts.Select(v => v.Path));
    }

    [Fact]
    public void Describe_names_every_file_and_its_verdict()
    {
        var verdicts = AuthorTestScope.Classify(
            ["tests/a_test.go", "src/x.rs", "src/y.go"], Config([".rs"]));

        Assert.Equal("tests/a_test.go=separate;src/x.rs=inline-capable;src/y.go=suspect",
            AuthorTestScope.Describe(verdicts));
    }

    [Fact]
    public void Describe_of_nothing_is_empty()
    {
        Assert.Equal(string.Empty, AuthorTestScope.Describe(AuthorTestScope.Classify([], Config())));
    }

    [Fact]
    public void An_extensionless_file_is_suspect()
    {
        var verdicts = AuthorTestScope.Classify(["Makefile"], Config([".rs"]));

        Assert.Equal(AuthorTestScopeKind.Suspect, verdicts[0].Kind);
    }
}
