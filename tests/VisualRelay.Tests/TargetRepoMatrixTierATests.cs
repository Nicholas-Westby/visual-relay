using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Tier A of the target-repository matrix: detection-only, free, and fast.
/// Each row materializes nothing but marker files in a temp directory — no
/// scaffolding tool, no package install, no network — and asserts the
/// <see cref="TestCommandDetector.DetectCandidates"/> order plus the config
/// that <see cref="ProjectBootstrapper"/> then writes. This file carries the
/// shared fixture helpers and the .NET, Python and Go rows; the remaining rows
/// live in the sibling partials.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>
    /// Creates an isolated temp repo directory. <paramref name="name"/> becomes
    /// the tail of the final path segment so the non-ASCII rows can control it.
    /// Every caller deletes the returned path in a <c>finally</c> via
    /// <see cref="TestFileSystem.DeleteDirectoryResilient"/>.
    /// </summary>
    private static string NewRepo(string name = "repo")
    {
        var path = Path.Combine(
            Path.GetTempPath(), "vr-tier-a", $"{Guid.NewGuid():N}-{name}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes <paramref name="content"/> at a repo-relative path, creating parents.</summary>
    private static void Write(string root, string relativePath, string content = "")
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Runs the real bootstrap over an in-memory git and an always-green
    /// validation runner, then returns the raw bytes of the written config.
    /// </summary>
    private static async Task<(ProjectBootstrapResult Result, string ConfigText)> BootstrapAsync(string root)
    {
        var result = await ProjectBootstrapper.BootstrapAsync(
            root,
            gitInvoker: new GitSimEngine(),
            validationRunner: new ScriptedTestRunner(new TestRunResult(0, "ok")));
        return (result, File.ReadAllText(result.ConfigPath));
    }

    // ── Row: C# / .NET ──────────────────────────────────────────────────

    /// <summary>
    /// A solution plus a project is one .NET detector hit, not two — the rule is
    /// a single OR over three patterns. The conventional <c>tests/</c> directory
    /// then adds a SECOND candidate from the weak last-resort rule, so a .NET
    /// repo laid out normally offers <c>pytest</c> as its fallback. Harmless
    /// (dotnet ranks first and validates), but it means a repo whose
    /// <c>dotnet test</c> cannot start smoke-runs pytest before giving up. The
    /// written config additionally carries the .NET guard and format commands.
    /// </summary>
    [Fact]
    public async Task Row_DotNet_DetectsDotnetTestAndWritesToolchainConfig()
    {
        var root = NewRepo("dotnet");
        try
        {
            Write(root, "App.slnx", "<Solution/>");
            Write(root, "src/App/App.csproj", "<Project/>");
            Write(root, "tests/AppTests/AppTests.cs", "public class AppTests { }");

            Assert.Equal(["dotnet test", "pytest"], TestCommandDetector.DetectCandidates(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.False(result.UsedPlaceholderTestCommand);
            Assert.Equal("dotnet test", result.TestCommand);
            Assert.Contains("\"testCmd\": \"dotnet test\"", config, StringComparison.Ordinal);
            Assert.Contains("\"testFileCmd\": \"dotnet test\"", config, StringComparison.Ordinal);
            Assert.Contains("\"guardCmd\": \"dotnet format App.slnx --verify-no-changes\"",
                config, StringComparison.Ordinal);
            Assert.Contains("\"formatCmd\": \"dotnet format App.slnx\"", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ── Row: Python (the generated sample repo shape, as control) ───────

    /// <summary>
    /// A pyproject plus a <c>tests/</c> directory produces <c>pytest</c>
    /// TWICE — once from the strong manifest rule and once from the weak
    /// directory rule. The duplicate is harmless for the first candidate but
    /// makes <see cref="ProjectBootstrapper"/> smoke-run an identical failing
    /// command a second time before falling back to the placeholder.
    /// </summary>
    [Fact]
    public async Task Row_Python_StrongAndWeakRulesBothFire_ProducingADuplicateCandidate()
    {
        var root = NewRepo("python");
        try
        {
            Write(root, "pyproject.toml", "[project]\nname = \"sample\"\n");
            Write(root, "tests/test_sample.py", "def test_ok():\n    assert True\n");

            Assert.Equal(["pytest", "pytest"], TestCommandDetector.DetectCandidates(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal("pytest", result.TestCommand);
            Assert.Contains("\"testCmd\": \"pytest\"", config, StringComparison.Ordinal);
            // No Python formatter or guard is inferred — neither detector knows
            // about black/ruff, so both keys are absent from the config.
            Assert.DoesNotContain("formatCmd", config, StringComparison.Ordinal);
            Assert.DoesNotContain("guardCmd", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ── Row: Go ─────────────────────────────────────────────────────────

    /// <summary>
    /// A go.mod repo detects the whole-module command and gains gofmt as its
    /// formatter. <c>_test.go</c> files classify as tests, so <c>{files}</c>
    /// targeting would narrow correctly if configured.
    /// </summary>
    [Fact]
    public async Task Row_Go_DetectsModuleWideTestAndGofmt()
    {
        var root = NewRepo("go");
        try
        {
            Write(root, "go.mod", "module example.com/m\n\ngo 1.22\n");
            Write(root, "main.go", "package main\n");
            Write(root, "main_test.go", "package main\n");

            Assert.Equal(["go test ./..."], TestCommandDetector.DetectCandidates(root));
            Assert.True(TestPathClassifier.IsRunnableTestFile("main_test.go", null));

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal("go test ./...", result.TestCommand);
            Assert.Contains("\"formatCmd\": \"gofmt -w .\"", config, StringComparison.Ordinal);
            Assert.DoesNotContain("guardCmd", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
