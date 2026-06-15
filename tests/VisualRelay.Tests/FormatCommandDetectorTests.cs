using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class FormatCommandDetectorTests
{
    [Fact]
    public void Detect_SlnxPresent_ReturnsDotnetFormatWithSolutionName()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "MyProject.slnx"), "");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("dotnet format MyProject.slnx", result);
    }

    [Fact]
    public void Detect_SlnPresent_ReturnsDotnetFormatWithSolutionName()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "MyProject.sln"), "");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("dotnet format MyProject.sln", result);
    }

    [Fact]
    public void Detect_CsprojOnly_ReturnsDotnetFormat()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "MyProject.csproj"), "");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("dotnet format", result);
    }

    [Fact]
    public void Detect_CargoTomlPresent_ReturnsCargoFmt()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Cargo.toml"), "");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("cargo fmt", result);
    }

    [Fact]
    public void Detect_GoModPresent_ReturnsGofmt()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("gofmt -w .", result);
    }

    [Fact]
    public void Detect_PackageJsonWithFormatScript_ReturnsScriptValue()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "format": "biome format --write ." } }""");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("biome format --write .", result);
    }

    [Fact]
    public void Detect_PackageJsonWithoutFormatScript_ReturnsPrettier()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "jest" } }""");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("prettier --write .", result);
    }

    [Fact]
    public void Detect_NoMarkers_ReturnsNull()
    {
        using var repo = TestRepository.Create();

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Null(result);
    }

    [Fact]
    public void Detect_SwiftPackage_ReturnsSwiftformat()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("swiftformat .", result);
    }
}
