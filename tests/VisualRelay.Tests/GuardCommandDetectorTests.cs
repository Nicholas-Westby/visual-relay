using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Behaviour of <see cref="GuardCommandDetector"/>, the sibling detector that
/// had no tests at all. It chains <c>tools/guards/*.sh</c> and then appends
/// toolchain verification steps, returning <c>null</c> rather than blocking
/// init when nothing is recognized.
/// </summary>
public sealed class GuardCommandDetectorTests
{
    private static void WriteGuard(string root, string name)
    {
        var dir = Path.Combine(root, "tools", "guards");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "#!/usr/bin/env bash\nexit 0\n");
    }

    /// <summary>An empty folder has neither guards nor a toolchain — null.</summary>
    [Fact]
    public void Detect_NoMarkers_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        Assert.Null(GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// Guard scripts are emitted as repo-relative paths, chained with
    /// <c> &amp;&amp; </c> and ordered by ordinal full path.
    /// </summary>
    [Fact]
    public void Detect_GuardScripts_ChainedInOrdinalOrder()
    {
        using var repo = TestRepository.Create();
        WriteGuard(repo.Root, "guard-zeta.sh");
        WriteGuard(repo.Root, "guard-alpha.sh");

        Assert.Equal("tools/guards/guard-alpha.sh && tools/guards/guard-zeta.sh",
            GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>Only <c>*.sh</c> files under tools/guards are collected.</summary>
    [Fact]
    public void Detect_NonShellFilesInGuardsDir_AreIgnored()
    {
        using var repo = TestRepository.Create();
        WriteGuard(repo.Root, "guard-one.sh");
        File.WriteAllText(Path.Combine(repo.Root, "tools", "guards", "README.md"), "notes");

        Assert.Equal("tools/guards/guard-one.sh", GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>An existing but empty guards directory contributes nothing.</summary>
    [Fact]
    public void Detect_EmptyGuardsDirectory_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, "tools", "guards"));
        Assert.Null(GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A .NET solution appends a format verification step named after the
    /// solution file, for both solution formats.
    /// </summary>
    /// <param name="solutionFile">The solution file name to materialize.</param>
    [Theory]
    [InlineData("MyApp.slnx")]
    [InlineData("MyApp.sln")]
    public void Detect_SolutionFile_AppendsDotnetFormatVerify(string solutionFile)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, solutionFile), "");

        Assert.Equal($"dotnet format {solutionFile} --verify-no-changes",
            GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>When both solution formats exist the newer .slnx wins.</summary>
    [Fact]
    public void Detect_BothSolutionFormats_PrefersSlnx()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "MyApp.sln"), "");
        File.WriteAllText(Path.Combine(repo.Root, "MyApp.slnx"), "");

        Assert.Equal("dotnet format MyApp.slnx --verify-no-changes",
            GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A project-only .NET repo yields no guard. Unlike
    /// <see cref="FormatCommandDetector"/>, which falls back to a bare
    /// <c>dotnet format</c> for a lone <c>*.csproj</c>, the guard needs a
    /// solution file to name and so stays silent.
    /// </summary>
    [Fact]
    public void Detect_CsprojWithoutSolution_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");

        Assert.Null(GuardCommandDetector.Detect(repo.Root));
        Assert.Equal("dotnet format", FormatCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A repo whose package.json declares a lint script already owns a policy gate that
    /// its CI runs as a separate required step. VR's own gate was only the test command,
    /// while the stage prompts promised the model a full check/lint/format gate at
    /// Verify, so lint errors sailed through into committed code.
    /// </summary>
    [Fact]
    public void Detect_PackageJsonWithLintScript_AppendsNpmRunLint()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "mocha", "lint": "eslint ." } }""");

        Assert.Equal("npm run lint", GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A check script usually wraps lint, types and formatting, so it is the broader
    /// gate and wins when a repo declares both.
    /// </summary>
    [Fact]
    public void Detect_PackageJsonWithLintAndCheckScripts_PrefersCheck()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "lint": "eslint .", "check": "eslint . && tsc --noEmit" } }""");

        Assert.Equal("npm run check", GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// Corroboration cuts both ways: a package.json that declares neither script yields
    /// no guard rather than a guessed one that would fail on every commit.
    /// </summary>
    [Fact]
    public void Detect_PackageJsonWithoutLintOrCheckScript_ReturnsNull()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "mocha" } }""");

        Assert.Null(GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A guard checks; it must not rewrite what it checks. Measured bootstrapping markedjs/marked on
    /// the Mac: its lint script is <c>eslint --fix</c>, so the guard would have edited the task's files
    /// after review and could never fail on a problem it knows how to fix.
    /// </summary>
    /// <param name="lint">A lint script that rewrites files.</param>
    [Theory]
    [InlineData("eslint --fix")]
    [InlineData("prettier --write . && eslint .")]
    [InlineData("biome check --write=true ./src")]
    public void Detect_LintScriptThatRewritesFiles_IsNotAGuard(string lint)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            $$"""{ "scripts": { "test": "mocha", "lint": "{{lint}}" } }""");

        Assert.Null(GuardCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_CheckScriptThatRewritesFiles_FallsBackToAReadOnlyLint()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "check": "biome check --write", "lint": "eslint --fix-dry-run ." } }""");

        Assert.Equal("npm run lint", GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>The repo's own script chains after its guard scripts, before .NET.</summary>
    [Fact]
    public void Detect_GuardsAndNodeAndSolution_ChainsNodeScriptInTheMiddle()
    {
        using var repo = TestRepository.Create();
        WriteGuard(repo.Root, "guard-one.sh");
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "lint": "eslint ." } }""");
        File.WriteAllText(Path.Combine(repo.Root, "MyApp.slnx"), "");

        Assert.Equal(
            "tools/guards/guard-one.sh && npm run lint && dotnet format MyApp.slnx --verify-no-changes",
            GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>A SwiftPM manifest appends a compile check.</summary>
    [Fact]
    public void Detect_PackageSwift_AppendsSwiftBuild()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");

        Assert.Equal("swift build", GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// Guards first, then dotnet format, then swift build — the toolchain
    /// checks are appended to the script chain rather than replacing it.
    /// </summary>
    [Fact]
    public void Detect_GuardsAndBothToolchains_ChainsInFixedOrder()
    {
        using var repo = TestRepository.Create();
        WriteGuard(repo.Root, "guard-one.sh");
        File.WriteAllText(Path.Combine(repo.Root, "MyApp.slnx"), "");
        File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "");

        Assert.Equal(
            "tools/guards/guard-one.sh && dotnet format MyApp.slnx --verify-no-changes && swift build",
            GuardCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A JVM repo yields no guard command, deliberately. Every JVM lint or
    /// format check (spotless, checkstyle, ktlint, spotbugs) exists only if
    /// the project applies that plugin, and invoking an unapplied goal or task
    /// is a hard build failure — a guessed guardCmd would then block every
    /// commit in the pipeline. The blanket alternatives are worse:
    /// <c>gradle check</c> and <c>mvn verify</c> both re-run the test suite
    /// that testCmd already runs.
    /// </summary>
    /// <param name="manifest">The JVM manifest file name to materialize.</param>
    [Theory]
    [InlineData("pom.xml")]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    public void Detect_JvmProject_ReturnsNull(string manifest)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, manifest), "");

        Assert.Null(GuardCommandDetector.Detect(repo.Root));
        Assert.Null(FormatCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// The <c>tools/guards/</c> branch is dead in this repository: the shell
    /// guards were ported to C# (<c>tools/VisualRelay.Guards</c>) and the
    /// directory no longer exists, so detection on the live tree contributes
    /// only the .NET format check. Pinned so the dead branch is visible rather
    /// than quietly carried.
    /// </summary>
    [Fact]
    public void Detect_ThisRepository_HasNoShellGuardsLeft()
    {
        Assert.False(Directory.Exists(Path.Combine(RepoSetup.Root, "tools", "guards")));
        Assert.Equal("dotnet format VisualRelay.slnx --verify-no-changes",
            GuardCommandDetector.Detect(RepoSetup.Root));
    }
}
