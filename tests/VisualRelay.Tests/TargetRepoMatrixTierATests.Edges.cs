using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Tier A rows that are about repository SHAPE rather than toolchain: a
/// monorepo with no root manifest, a repo with no detectable toolchain at all,
/// a repo with no git history, and nested <c>.gitignore</c> files.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>
    /// Every detector enumerates <c>SearchOption.TopDirectoryOnly</c>, so a
    /// monorepo whose manifests all live one or more levels down yields ZERO
    /// candidates however many toolchains it actually contains, and bootstraps
    /// onto the placeholder.
    /// </summary>
    [Fact]
    public async Task Row_MonorepoWithNoRootManifest_YieldsZeroCandidates()
    {
        var root = NewRepo("monorepo");
        try
        {
            Write(root, "services/api/pom.xml", "<project/>");
            Write(root, "services/web/package.json", """{ "scripts": { "test": "vitest run" } }""");
            Write(root, "libs/core/Cargo.toml", "[package]\nname = \"core\"\n");
            Write(root, "libs/core/go.mod", "module example.com/core\n");
            Write(root, "README.md", "# monorepo\n");

            Assert.Empty(TestCommandDetector.DetectCandidates(root));

            var (result, _) = await BootstrapAsync(root);

            Assert.True(result.UsedPlaceholderTestCommand);
            Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, result.TestCommand);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// A repo with no toolchain marker at all is still made runnable: the
    /// placeholder is written, the config loads as Loaded rather than
    /// Incomplete, and neither optional command key is emitted.
    /// </summary>
    [Fact]
    public async Task Row_NoDetectableToolchain_BootstrapsToARunnablePlaceholderConfig()
    {
        var root = NewRepo("no-toolchain");
        try
        {
            Write(root, "README.md", "# notes\n");
            Write(root, "docs/design.md", "# design\n");

            Assert.Empty(TestCommandDetector.DetectCandidates(root));
            Assert.Equal(string.Empty, TestCommandDetector.Detect(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.True(result.UsedPlaceholderTestCommand);
            Assert.Null(result.SetupCheck);
            Assert.DoesNotContain("formatCmd", config, StringComparison.Ordinal);
            Assert.DoesNotContain("guardCmd", config, StringComparison.Ordinal);

            var loaded = await RelayConfigLoader.TryLoadAsync(root);
            Assert.Equal(RelayConfigStatus.Loaded, loaded.Status);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// Detection is entirely file-system based and never consults git, so a
    /// folder with no history at all detects normally; bootstrap then creates
    /// the repository and the initial commit that worktrees need.
    /// </summary>
    [Fact]
    public async Task Row_NoGitHistory_DetectsNormallyThenGainsARepository()
    {
        var root = NewRepo("no-history");
        try
        {
            Write(root, "go.mod", "module example.com/m\n");
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));
            Assert.Equal(["go test ./..."], TestCommandDetector.DetectCandidates(root));

            var sim = new GitSimEngine();
            var result = await ProjectBootstrapper.BootstrapAsync(
                root, gitInvoker: sim, validationRunner: new ScriptedTestRunner(new TestRunResult(0, "ok")));

            Assert.True(result.GitInitialized);
            Assert.Equal("go test ./...", result.TestCommand);
            var head = await sim.RunAsync(root, ["rev-parse", "HEAD"], CancellationToken.None);
            Assert.Equal(0, head.ExitCode);
            Assert.NotEmpty(head.Output.Trim());
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// Detection ignores <c>.gitignore</c> entirely: a manifest that git would
    /// not track is still a marker, so an ignored <c>pom.xml</c> still drives
    /// the JVM candidate.
    /// </summary>
    [Fact]
    public void Row_GitignoredManifest_IsStillDetected()
    {
        var root = NewRepo("ignored-manifest");
        try
        {
            Write(root, ".gitignore", "pom.xml\nbuild/\n");
            Write(root, "pom.xml", "<project/>");

            Assert.Equal(["mvn test"], TestCommandDetector.DetectCandidates(root));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// Nested <c>.gitignore</c> files are a structural gap in the in-memory git
    /// simulator: it loads only the repo-root file, so a rule declared in
    /// <c>sub/.gitignore</c> has no effect and a path real git would ignore is
    /// reported as tracked. Pinned so the limitation is explicit wherever GitSim
    /// stands in for git.
    /// </summary>
    [Fact]
    public void Row_NestedGitignore_IsInvisibleToTheGitSimulator()
    {
        var root = NewRepo("nested-gitignore");
        try
        {
            Write(root, ".gitignore", "build/\n");
            Write(root, "sub/.gitignore", "*.log\n");
            Write(root, "sub/app.log", "noise\n");
            Write(root, "build/out.txt", "artifact\n");

            var sim = new GitSimEngine();

            Assert.True(sim.IsIgnored(root, "build/out.txt"));
            Assert.False(sim.IsIgnored(root, "sub/app.log"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
