using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class TestCommandDetectorTests
{
    [Fact]
    public void Detect_DotnetProject_ReturnsDotnetTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");
        Assert.Equal("dotnet test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_PythonProject_ReturnsPytest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");
        Assert.Equal("pytest", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_NodeProject_ReturnsNpmTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");
        Assert.Equal("npm test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_UnknownProject_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        Assert.Equal(string.Empty, TestCommandDetector.Detect(repo.Root));
    }
}
