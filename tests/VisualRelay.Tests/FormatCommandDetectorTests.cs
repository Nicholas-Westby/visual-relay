using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed partial class FormatCommandDetectorTests
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
    public void Detect_PackageJsonWithFormatScript_RunsItThroughNpm()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "format": "biome format --write ." } }""");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("npm run format", result);
    }

    /// <summary>
    /// Prettier is third-party: a package.json that neither runs it nor configures it is
    /// no evidence the repo uses it, and writing the command anyway reformats a whole
    /// tree the project never asked to be reformatted.
    /// </summary>
    [Fact]
    public void Detect_PackageJsonWithoutFormatScriptOrPrettier_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "jest" } }""");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Null(result);
    }

    /// <summary>Any of prettier's own config file names corroborates it.</summary>
    /// <param name="configFile">The prettier config file to materialize.</param>
    [Theory]
    [InlineData(".prettierrc")]
    [InlineData(".prettierrc.json")]
    [InlineData(".prettierrc.yaml")]
    [InlineData("prettier.config.js")]
    public void Detect_PackageJsonWithPrettierConfigFile_ReturnsPrettier(string configFile)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "jest" } }""");
        File.WriteAllText(Path.Combine(repo.Root, configFile), "{}");

        Assert.Equal("node_modules/.bin/prettier --write .", FormatCommandDetector.Detect(repo.Root));
    }

    /// <summary>A prettier key in package.json is its config too.</summary>
    [Fact]
    public void Detect_PackageJsonWithPrettierKey_ReturnsPrettier()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "prettier": { "semi": false }, "scripts": { "test": "jest" } }""");

        Assert.Equal("node_modules/.bin/prettier --write .", FormatCommandDetector.Detect(repo.Root));
    }

    /// <summary>So is depending on it.</summary>
    [Fact]
    public void Detect_PackageJsonWithPrettierDevDependency_ReturnsPrettier()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "devDependencies": { "prettier": "^3.3.0" } }""");

        Assert.Equal("node_modules/.bin/prettier --write .", FormatCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_NoMarkers_ReturnsNull()
    {
        using var repo = TestRepository.Create();

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Null(result);
    }

    /// <summary>
    /// swiftformat reads a <c>.swiftformat</c> file; a repo that carries one has chosen
    /// it, so the command is written.
    /// </summary>
    [Fact]
    public void Detect_SwiftPackageWithSwiftformatConfig_ReturnsSwiftformat()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");
        File.WriteAllText(Path.Combine(repo.Root, ".swiftformat"), "--indent 4\n");

        var result = FormatCommandDetector.Detect(repo.Root);

        Assert.Equal("swiftformat .", result);
    }

    /// <summary>
    /// A Swift repo carrying Apple's <c>.swift-format</c> uses a DIFFERENT tool, and
    /// swiftformat merely being on PATH is no evidence about the repo. Configuring it
    /// would have rewritten all 56 files of swift-argument-parser at the first format
    /// step.
    /// </summary>
    [Fact]
    public void Detect_SwiftPackageWithAppleSwiftFormatConfig_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");
        File.WriteAllText(Path.Combine(repo.Root, ".swift-format"), """{ "version": 1 }""");

        Assert.Null(FormatCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// The toolchain's own formatters need no corroboration: they ship with the
    /// toolchain the marker already proves is in use, and a config file is optional.
    /// </summary>
    [Fact]
    public void Detect_ToolchainNativeFormatters_NeedNoConfigFile()
    {
        using var rust = TestRepository.Create();
        File.WriteAllText(Path.Combine(rust.Root, "Cargo.toml"), "");
        Assert.Equal("cargo fmt", FormatCommandDetector.Detect(rust.Root));

        using var go = TestRepository.Create();
        File.WriteAllText(Path.Combine(go.Root, "go.mod"), "");
        Assert.Equal("gofmt -w .", FormatCommandDetector.Detect(go.Root));
    }
}
