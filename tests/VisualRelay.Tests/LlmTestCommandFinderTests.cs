using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class LlmTestCommandFinderTests
{
    [Fact]
    public void ExtractCommand_StripsCodeFencesAndQuotes()
    {
        Assert.Equal("pytest", LlmTestCommandFinder.ExtractCommand("```\npytest\n```"));
        Assert.Equal("npm test", LlmTestCommandFinder.ExtractCommand("\"npm test\""));
        Assert.Equal("dotnet test", LlmTestCommandFinder.ExtractCommand("dotnet test\n"));
    }

    [Fact]
    public void BuildPrompt_IncludesTopLevelFileNames()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Cargo.toml"), "[package]");
        var prompt = LlmTestCommandFinder.BuildPrompt(repo.Root);
        Assert.Contains("Cargo.toml", prompt);
    }

    [Fact]
    public async Task FindAsync_ReturnsCleanedCommandFromCompleter()
    {
        using var repo = TestRepository.Create();
        var finder = new LlmTestCommandFinder((_, _) => Task.FromResult("```sh\ncargo test\n```"));
        Assert.Equal("cargo test", await finder.FindAsync(repo.Root));
    }
}
