using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="TestSideEffectsGuard"/> — flags test sources
/// referencing real-side-effect constructs: <c>new GitInvoker(</c>,
/// <c>Process.Start</c>, <c>ProcessStartInfo</c>,
/// <c>Environment.SetEnvironmentVariable</c>. Honors
/// <see cref="RealSleepGuard"/>'s slow-integration exemption list.
/// Explains per finding which hermetic double to prefer.
/// </summary>
public sealed class TestSideEffectsGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public TestSideEffectsGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// <c>new GitInvoker()</c> in a test file path is reported.
    /// </summary>
    [Fact]
    public void NewGitInvoker_InTestPath_IsReported()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/GitTest.cs", source)]);

        Assert.NotEmpty(violations);
        Assert.Contains(violations, v => v.Reason.Contains("GitInvoker"));
    }

    /// <summary>
    /// <c>Process.Start</c> in a test file path is reported.
    /// </summary>
    [Fact]
    public void ProcessStart_InTestPath_IsReported()
    {
        const string source = "class C { void M() { Process.Start(\"git\"); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/ProcTest.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// <c>new ProcessStartInfo(...)</c> in a test file path is reported.
    /// </summary>
    [Fact]
    public void ProcessStartInfo_InTestPath_IsReported()
    {
        const string source = "class C { void M() { var p = new ProcessStartInfo(\"dotnet\", \"build\"); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/PsiTest.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// <c>Environment.SetEnvironmentVariable</c> in a test file path is reported.
    /// </summary>
    [Fact]
    public void SetEnvironmentVariable_InTestPath_IsReported()
    {
        const string source =
            "class C { void M() { Environment.SetEnvironmentVariable(\"KEY\", \"val\"); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/EnvTest.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Same construct in a src/ path is NOT flagged (only test sources).
    /// </summary>
    [Fact]
    public void NewGitInvoker_InSourcePath_IsNotReported()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("src/VisualRelay.Core/Fixtures/Prod.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Slow-integration exempt file (ProcessCaptureGracefulStopTests.cs) is
    /// not scanned.
    /// </summary>
    [Fact]
    public void RealIntegrationExemptFile_IsNotScanned()
    {
        const string source = "class C { void M() { Process.Start(\"git\"); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tests/VisualRelay.Tests/ProcessCaptureGracefulStopTests.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Self-exempt file is not scanned.
    /// </summary>
    [Fact]
    public void SelfExemptFile_IsNotScanned()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tools/VisualRelay.Guards/TestSideEffectsGuard.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A test file with no side-effect constructs yields zero violations.
    /// </summary>
    [Fact]
    public void CleanTestFile_IsNotReported()
    {
        const string source = "class C { void M() { var x = 1 + 1; } }";

        var violations = TestSideEffectsGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/Clean.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Live-tree smoke: matcher completes without throwing over the full tree.
    /// </summary>
    [Fact]
    public void AllTrees_CompleteWithoutThrowing()
    {
        var violations = TestSideEffectsGuard.FindViolations(_trees.AllTrees);

        Assert.NotNull(violations);
    }
}
