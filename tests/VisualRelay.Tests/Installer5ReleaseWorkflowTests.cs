namespace VisualRelay.Tests;

/// <summary>
/// Tests for <c>.github/workflows/release.yml</c> (CI release workflow).
/// The workflow must trigger on tag push, build self-contained osx-arm64/x64
/// bundles, ad-hoc sign them, assemble tarballs with the launcher + tools/backend,
/// compute sha256, and create a GitHub Release.
/// These must FAIL before the file exists.
/// </summary>
public sealed class Installer5ReleaseWorkflowTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string WorkflowPath => Path.Combine(RepoRoot, ".github", "workflows", "release.yml");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadWorkflow()
    {
        Assert.True(File.Exists(WorkflowPath),
            $"Workflow file not found at {WorkflowPath}. " +
            "It must be created at .github/workflows/release.yml.");
        return File.ReadAllText(WorkflowPath);
    }

    private static string[] ReadWorkflowLines()
    {
        Assert.True(File.Exists(WorkflowPath),
            $"Workflow file not found at {WorkflowPath}.");
        return File.ReadAllLines(WorkflowPath);
    }

    // ── 1. File exists ───────────────────────────────────────────────────

    [Fact]
    public void WorkflowFile_Exists()
    {
        Assert.True(File.Exists(WorkflowPath),
            $".github/workflows/release.yml must exist at {WorkflowPath}");
    }

    // ── 2. Trigger ───────────────────────────────────────────────────────

    [Fact]
    public void Workflow_TriggersOnTagPush()
    {
        var content = ReadWorkflow();

        // Must trigger on push of version tags (v*).
        Assert.Contains("push:", content, StringComparison.Ordinal);
        Assert.Contains("tags:", content, StringComparison.Ordinal);
        Assert.Contains("'v*'", content, StringComparison.Ordinal);
    }

    // ── 3. Build job ─────────────────────────────────────────────────────

    [Fact]
    public void Workflow_HasBuildJob()
    {
        var content = ReadWorkflow();

        // Must have a build job.
        Assert.Contains("build:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_BuildJob_HasArchMatrix()
    {
        var content = ReadWorkflow();

        // Build job must use a matrix strategy for osx-arm64 and osx-x64.
        Assert.Contains("matrix:", content, StringComparison.Ordinal);
        Assert.Contains("osx-arm64", content, StringComparison.Ordinal);
        Assert.Contains("osx-x64", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_BuildJob_RunsOnMacos()
    {
        var content = ReadWorkflow();

        // Build must run on macOS (needed for codesign and native binaries).
        Assert.Contains("macos", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_BuildJob_SetsUpDotnet()
    {
        var content = ReadWorkflow();

        // Must use setup-dotnet action to install .NET SDK for publishing.
        Assert.Contains("setup-dotnet", content, StringComparison.Ordinal);
    }

    // ── 4. Self-contained publish ────────────────────────────────────────

    [Fact]
    public void Workflow_PublishesSelfContained()
    {
        var content = ReadWorkflow();

        // Must use dotnet publish with --self-contained true.
        Assert.Contains("--self-contained", content, StringComparison.Ordinal);
        Assert.Contains("true", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_PublishesForMatrixRid()
    {
        var content = ReadWorkflow();

        // Must use the matrix RID in publish commands.
        Assert.Contains("matrix.rid", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_PublishesVisualRelayApp()
    {
        var content = ReadWorkflow();

        // Must publish the App project.
        Assert.Contains("VisualRelay.App", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_PublishesInit()
    {
        var content = ReadWorkflow();

        // Must publish the Init tool.
        Assert.Contains("VisualRelay.Init", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_PublishesGenBackendConfig()
    {
        var content = ReadWorkflow();

        // Must publish the GenBackendConfig tool.
        Assert.Contains("VisualRelay.GenBackendConfig", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_DoesNotPublishSampleTasks()
    {
        var content = ReadWorkflow();

        // Must NOT publish SampleTasks (dev-only, excluded from brew).
        Assert.DoesNotContain("SampleTasks", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_DoesNotPublishRunTaskOrScreenshots()
    {
        var content = ReadWorkflow();

        // Must NOT publish RunTask or Screenshots (dev-only).
        Assert.DoesNotContain("RunTask", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Screenshots", content, StringComparison.Ordinal);
    }

    // ── 5. Ad-hoc signing ────────────────────────────────────────────────

    [Fact]
    public void Workflow_AdHocSignsAppBinary()
    {
        var content = ReadWorkflow();

        // Must run codesign --force -s - on the published app binary.
        Assert.Contains("codesign", content, StringComparison.Ordinal);
        Assert.Contains("--force", content, StringComparison.Ordinal);
        Assert.Contains("-s -", content, StringComparison.Ordinal);
    }

    // ── 6. Assembly ──────────────────────────────────────────────────────

    [Fact]
    public void Workflow_CopiesLauncherScript()
    {
        var content = ReadWorkflow();

        // Must copy the visual-relay launcher script into the publish dir.
        Assert.Contains("visual-relay", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_CopiesBackendTools()
    {
        var content = ReadWorkflow();

        // Must copy tools/backend/ into the publish layout.
        Assert.Contains("tools/backend", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_CreatesTarball()
    {
        var content = ReadWorkflow();

        // Must create a .tar.gz artifact.
        Assert.Contains("tar", content, StringComparison.Ordinal);
        Assert.Contains(".tar.gz", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_ComputesSha256()
    {
        var content = ReadWorkflow();

        // Must compute sha256 checksum for the tarball.
        Assert.Contains("sha256", content, StringComparison.OrdinalIgnoreCase);
    }

    // ── 7. Release job ───────────────────────────────────────────────────

    [Fact]
    public void Workflow_HasReleaseJob()
    {
        var content = ReadWorkflow();

        // Must have a release job (separate from build).
        Assert.Contains("release:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_ReleaseJob_DependsOnBuild()
    {
        var content = ReadWorkflow();

        // Release job must depend on (needs) build.
        Assert.Contains("needs:", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_ReleaseJob_UsesGhReleaseCreate()
    {
        var content = ReadWorkflow();

        // Must use gh release create or similar to publish the release.
        Assert.Contains("release", content, StringComparison.OrdinalIgnoreCase);
    }

    // ── 8. Smoke step ────────────────────────────────────────────────────

    [Fact]
    public void Workflow_HasSmokeStep_VerifySigning()
    {
        var content = ReadWorkflow();

        // Must have a smoke verification step: codesign -dv to verify
        // the app binary is ad-hoc signed.
        Assert.Contains("codesign", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_HasSmokeStep_LaunchBinary()
    {
        var content = ReadWorkflow();

        // Must test that the published binary launches (e.g. --help).
        Assert.Contains("--help", content, StringComparison.Ordinal);
    }

    // ── 9. Upload artifacts ──────────────────────────────────────────────

    [Fact]
    public void Workflow_UploadsArtifacts()
    {
        var content = ReadWorkflow();

        // Must upload build artifacts for the release job.
        Assert.True(
            content.Contains("upload-artifact", StringComparison.Ordinal) ||
            content.Contains("upload", StringComparison.OrdinalIgnoreCase),
            "Workflow must upload build artifacts");
    }
}
