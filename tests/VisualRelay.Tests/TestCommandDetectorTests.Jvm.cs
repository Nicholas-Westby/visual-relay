using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// JVM half of the detector contract: Maven and Gradle markers, wrapper
/// preference, and where the two rank against the existing eight detectors.
/// Before these existed a Maven or Gradle repo fell through every rule and
/// landed on <see cref="ProjectBootstrapper.PlaceholderTestCommand"/>, while
/// <c>TestPathClassifier</c> was already classifying <c>.java</c>/<c>.kt</c>
/// files as tests — detection and classification disagreed for the whole
/// ecosystem.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    // ── Maven ───────────────────────────────────────────────────────────

    /// <summary>A bare <c>pom.xml</c> with no wrapper detects as <c>mvn test</c>.</summary>
    [Fact]
    public void Detect_MavenPom_ReturnsMvnTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pom.xml"), "<project/>");
        Assert.Equal("mvn test", TestCommandDetector.Detect(repo.Root));
    }

    /// <summary>A checked-in <c>mvnw</c> wrapper is preferred over the bare tool.</summary>
    [Fact]
    public void Detect_MavenWrapper_PrefersMvnw()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pom.xml"), "<project/>");
        File.WriteAllText(Path.Combine(repo.Root, "mvnw"), "#!/bin/sh\n");
        Assert.Equal("./mvnw test", TestCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// An <c>mvnw</c> without a <c>pom.xml</c> is not a Maven project — the
    /// wrapper alone must not create a candidate.
    /// </summary>
    [Fact]
    public void Detect_MvnwWithoutPom_IsNotAMavenProject()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "mvnw"), "#!/bin/sh\n");
        Assert.Empty(TestCommandDetector.DetectCandidates(repo.Root));
    }

    // ── Gradle ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every root Gradle manifest is a marker: Groovy and Kotlin DSL, build
    /// script and settings file alike.
    /// </summary>
    /// <param name="manifest">The Gradle manifest file name to materialize.</param>
    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    [InlineData("settings.gradle.kts")]
    public void Detect_GradleManifest_ReturnsGradleTest(string manifest)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, manifest), "");
        Assert.Equal("gradle test", TestCommandDetector.Detect(repo.Root));
    }

    /// <summary>A checked-in <c>gradlew</c> wrapper is preferred over the bare tool.</summary>
    [Fact]
    public void Detect_GradleWrapper_PrefersGradlew()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "build.gradle.kts"), "");
        File.WriteAllText(Path.Combine(repo.Root, "gradlew"), "#!/bin/sh\n");
        Assert.Equal("./gradlew test", TestCommandDetector.Detect(repo.Root));
    }

    /// <summary>
    /// A <c>gradlew</c> without any Gradle manifest is not a Gradle project.
    /// </summary>
    [Fact]
    public void Detect_GradlewWithoutManifest_IsNotAGradleProject()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "gradlew"), "#!/bin/sh\n");
        Assert.Empty(TestCommandDetector.DetectCandidates(repo.Root));
    }

    // ── Priority ────────────────────────────────────────────────────────

    /// <summary>
    /// A repo carrying both build systems (a half-finished migration) yields
    /// both candidates with Maven first, since <c>pom.xml</c> is required by a
    /// Maven build while a Gradle file can be an auxiliary/included build.
    /// </summary>
    [Fact]
    public void DetectCandidates_MavenAndGradle_MavenFirst()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pom.xml"), "<project/>");
        File.WriteAllText(Path.Combine(repo.Root, "settings.gradle"), "");
        Assert.Equal(["mvn test", "gradle test"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    /// <summary>
    /// The defect this closes: a JVM repo with a root <c>test/</c> directory
    /// used to detect as <c>pytest</c> and nothing else. Both JVM detectors
    /// must outrank the weak directory fallback.
    /// </summary>
    /// <param name="manifest">The JVM manifest file name to materialize.</param>
    /// <param name="expected">The command that must now rank ahead of pytest.</param>
    [Theory]
    [InlineData("pom.xml", "mvn test")]
    [InlineData("build.gradle", "gradle test")]
    public void DetectCandidates_JvmManifestWithTestDir_BeatsWeakPytest(string manifest, string expected)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, manifest), "");
        Directory.CreateDirectory(Path.Combine(repo.Root, "test"));
        Assert.Equal([expected, "pytest"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    /// <summary>
    /// A Spring-style repo whose front-end tooling adds a <c>package.json</c>
    /// still offers both commands, with the explicit npm script first — the
    /// file's existing rule that a declared project script outranks an
    /// inferred runner.
    /// </summary>
    [Fact]
    public void DetectCandidates_MavenAndPackageJson_NodeScriptFirst()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pom.xml"), "<project/>");
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "vitest run" } }""");
        Assert.Equal(["npm test", "mvn test"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    /// <summary>
    /// JVM markers are <c>TopDirectoryOnly</c> like every other detector: a
    /// <c>pom.xml</c> one level down is not a root marker.
    /// </summary>
    [Fact]
    public void DetectCandidates_NestedPom_IsNotDetected()
    {
        using var repo = TestRepository.Create();
        var module = Path.Combine(repo.Root, "service");
        Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, "pom.xml"), "<project/>");
        Assert.Empty(TestCommandDetector.DetectCandidates(repo.Root));
    }
}
