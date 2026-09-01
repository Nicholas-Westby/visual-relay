using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// The headline rows of the Tier A matrix: Java and Kotlin. Before the JVM
/// detectors existed, a Maven or Gradle repo fell through all eight rules and
/// bootstrapped onto <see cref="ProjectBootstrapper.PlaceholderTestCommand"/>
/// while <see cref="TestPathClassifier"/> was already treating its
/// <c>.java</c>/<c>.kt</c> files as tests — detection and classification
/// disagreed for the whole ecosystem. These rows assert they now agree.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>
    /// Java/Maven: a standard-layout Maven repo with a wrapper detects
    /// <c>./mvnw test</c>, adopts it at bootstrap rather than the placeholder,
    /// and its <c>src/test/java</c> sources classify as tests.
    /// </summary>
    [Fact]
    public async Task Row_Java_MavenRepoDetectsWrapperCommandInsteadOfThePlaceholder()
    {
        var root = NewRepo("java-maven");
        try
        {
            Write(root, "pom.xml", "<project><artifactId>sample</artifactId></project>");
            Write(root, "mvnw", "#!/bin/sh\nexit 0\n");
            Write(root, "src/main/java/com/example/App.java", "package com.example;\n");
            Write(root, "src/test/java/com/example/AppTest.java", "package com.example;\n");

            Assert.Equal(["./mvnw test"], TestCommandDetector.DetectCandidates(root));
            Assert.True(TestPathClassifier
                .IsRunnableTestFile("src/test/java/com/example/AppTest.java", null));

            var (result, config) = await BootstrapAsync(root);

            Assert.False(result.UsedPlaceholderTestCommand);
            Assert.NotEqual(ProjectBootstrapper.PlaceholderTestCommand, result.TestCommand);
            Assert.Contains("\"testCmd\": \"./mvnw test\"", config, StringComparison.Ordinal);
            // Deliberately no formatCmd/guardCmd: every JVM format or lint check
            // is plugin-dependent, and invoking an unapplied goal is a hard failure.
            Assert.DoesNotContain("formatCmd", config, StringComparison.Ordinal);
            Assert.DoesNotContain("guardCmd", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// Kotlin/Gradle: the Kotlin-DSL manifests plus a wrapper detect
    /// <c>./gradlew test</c>, and <c>src/test/kotlin</c> sources classify as
    /// tests through both the directory rule and the PascalCase <c>.kt</c> rule.
    /// </summary>
    [Fact]
    public async Task Row_Kotlin_GradleRepoDetectsWrapperCommandInsteadOfThePlaceholder()
    {
        var root = NewRepo("kotlin-gradle");
        try
        {
            Write(root, "settings.gradle.kts", "rootProject.name = \"sample\"\n");
            Write(root, "build.gradle.kts", "plugins { kotlin(\"jvm\") }\n");
            Write(root, "gradlew", "#!/bin/sh\nexit 0\n");
            Write(root, "src/main/kotlin/App.kt", "fun main() {}\n");
            Write(root, "src/test/kotlin/AppTest.kt", "class AppTest\n");

            Assert.Equal(["./gradlew test"], TestCommandDetector.DetectCandidates(root));
            Assert.True(TestPathClassifier.IsRunnableTestFile("src/test/kotlin/AppTest.kt", null));

            var (result, config) = await BootstrapAsync(root);

            Assert.False(result.UsedPlaceholderTestCommand);
            Assert.Contains("\"testCmd\": \"./gradlew test\"", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// The pre-fix behaviour, kept as the control: a JVM source tree with no
    /// root manifest still detects nothing and still lands on the placeholder.
    /// It is the marker file, not the language, that drives detection.
    /// </summary>
    [Fact]
    public async Task Row_Jvm_SourcesWithoutARootManifest_StillHitThePlaceholder()
    {
        var root = NewRepo("jvm-no-manifest");
        try
        {
            Write(root, "src/main/java/com/example/App.java", "package com.example;\n");
            Write(root, "src/test/java/com/example/AppTest.java", "package com.example;\n");

            Assert.Empty(TestCommandDetector.DetectCandidates(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.True(result.UsedPlaceholderTestCommand);
            Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, result.TestCommand);
            Assert.Contains("visual-relay placeholder test command", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>Writes an executable stub script at a repo-relative path.</summary>
    private static void WriteExecutable(string root, string relativePath, string body)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// The wrapper preference is only worth having if init can actually run it:
    /// <c>./mvnw</c> is a repo-relative path and init validates by direct exec,
    /// not through a shell. This failed before <c>DirectExecTestRunner</c> began
    /// anchoring a relative program to the repo root — .NET resolves a relative
    /// <c>ProcessStartInfo.FileName</c> against the CALLING process's current
    /// directory, not against <c>WorkingDirectory</c>, so every wrapper command
    /// was rejected with exit 127 even though the pipeline's
    /// <c>/bin/sh -lc</c> would have run it.
    /// </summary>
    [Fact]
    public async Task Row_Java_RelativeWrapperPathValidatesFromTheRepoRoot()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix executable-bit wrapper script");

        var root = NewRepo("mvnw-exec");
        try
        {
            WriteExecutable(root, "mvnw", "echo 'Tests run: 1, Failures: 0'\nexit 0\n");
            Write(root, "pom.xml", "<project/>");

            Assert.Equal(["./mvnw test"], TestCommandDetector.DetectCandidates(root));

            var validation = await new TestCommandValidator(new DirectExecTestRunner(TimeSpan.FromSeconds(30)))
                .ValidateAsync(root, "./mvnw test");

            Assert.True(validation.Accepted, validation.RejectionReason);
            Assert.Equal(0, validation.RunResult.ExitCode);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// The same anchoring covers any repo-local test script, not just JVM
    /// wrappers: a nested <c>./scripts/test.sh</c> — a shape init previously
    /// always rejected — now smoke-validates, while a bare program name is
    /// still left to PATH lookup and an absent one still reports 127.
    /// </summary>
    [Fact]
    public async Task RepoLocalTestScript_ValidatesWhileBareNamesStillUsePath()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix executable-bit wrapper script");

        var root = NewRepo("local-script");
        try
        {
            WriteExecutable(root, "scripts/test.sh", "echo '2 tests passed'\nexit 0\n");
            var runner = new DirectExecTestRunner(TimeSpan.FromSeconds(30));

            var local = await runner.RunAsync(root, "./scripts/test.sh");
            Assert.Equal(0, local.ExitCode);
            Assert.Contains("2 tests passed", local.Output, StringComparison.Ordinal);

            // A bare name is never anchored — it must still be resolved on PATH.
            var onPath = await runner.RunAsync(root, "true");
            Assert.Equal(0, onPath.ExitCode);

            // An anchored path that does not exist still surfaces as 127.
            var missing = await runner.RunAsync(root, "./scripts/absent.sh");
            Assert.Equal(127, missing.ExitCode);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
