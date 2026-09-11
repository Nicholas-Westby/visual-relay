using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The <c>authorTests</c> object: absent means the defaults every existing
/// repository keeps working with, and a present object is normalized rather
/// than trusted.
/// </summary>
public sealed class AuthorTestsConfigLoaderTests
{
    private static async Task<RelayConfig> LoadAsync(TestRepository repo, string json)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), json);
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        return result.Config;
    }

    [Fact]
    public async Task Missing_object_yields_the_defaults()
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, """{ "testCmd": "dotnet test", "logSources": [] }""");

        Assert.Equal(AuthorTestsConfig.Default, config.AuthorTests);
        Assert.Empty(config.AuthorTests.DetectedLanguages);
        Assert.Empty(config.AuthorTests.InlineTestExtensions);
        Assert.Equal(AuthorTestsConfig.DiffAuditAuto, config.AuthorTests.DiffAudit);
    }

    [Fact]
    public async Task Full_object_round_trips()
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, """
            {
              "testCmd": "cargo test",
              "authorTests": {
                "detectedLanguages": ["rust", "python"],
                "inlineTestExtensions": [".rs"],
                "diffAudit": "always"
              }
            }
            """);

        Assert.Equal(["rust", "python"], config.AuthorTests.DetectedLanguages);
        Assert.Equal([".rs"], config.AuthorTests.InlineTestExtensions);
        Assert.Equal(AuthorTestsConfig.DiffAuditAlways, config.AuthorTests.DiffAudit);
    }

    [Theory]
    [InlineData("\"sometimes\"")]
    [InlineData("17")]
    [InlineData("null")]
    public async Task Invalid_diff_audit_falls_back_to_auto(string value)
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, $$"""
            { "testCmd": "cargo test", "authorTests": { "diffAudit": {{value}} } }
            """);

        Assert.Equal(AuthorTestsConfig.DiffAuditAuto, config.AuthorTests.DiffAudit);
    }

    [Fact]
    public async Task Off_is_a_valid_diff_audit()
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, """
            { "testCmd": "cargo test", "authorTests": { "diffAudit": "off" } }
            """);

        Assert.Equal(AuthorTestsConfig.DiffAuditOff, config.AuthorTests.DiffAudit);
    }

    [Fact]
    public async Task Inline_extensions_are_normalized_and_sorted()
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, """
            {
              "testCmd": "cargo test",
              "authorTests": { "inlineTestExtensions": ["ZIG", "  .rs  ", ".RS", "rs", ".zig", "", 7] }
            }
            """);

        Assert.Equal([".rs", ".zig"], config.AuthorTests.InlineTestExtensions);
    }

    [Fact]
    public async Task Detected_languages_keep_strings_only()
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, """
            {
              "testCmd": "cargo test",
              "authorTests": { "detectedLanguages": ["rust", 4, null, "python"] }
            }
            """);

        Assert.Equal(["rust", "python"], config.AuthorTests.DetectedLanguages);
    }

    [Fact]
    public async Task Non_object_author_tests_yields_the_defaults()
    {
        using var repo = TestRepository.Create();

        var config = await LoadAsync(repo, """{ "testCmd": "cargo test", "authorTests": "rust" }""");

        Assert.Equal(AuthorTestsConfig.Default, config.AuthorTests);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("always", true)]
    [InlineData("off", true)]
    [InlineData("Auto", false)]
    [InlineData("never", false)]
    [InlineData(null, false)]
    public void IsValidDiffAudit_accepts_exactly_the_three_modes(string? value, bool expected) =>
        Assert.Equal(expected, AuthorTestsConfig.IsValidDiffAudit(value));
}
