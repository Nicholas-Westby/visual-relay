using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test for <see cref="TestRealGitGuard"/>. The guard flags two patterns
/// under <c>tests/VisualRelay.Tests/</c> (excluding the opt-in parity suite and guard
/// test files themselves):
/// <list type="number">
///   <item><c>new GitInvoker(</c> object creation — a real process launcher;</item>
///   <item><c>new ProcessStartInfo("git")</c> — process-launch info whose file name is git.</item>
/// </list>
/// <para>The live-tree test asserts zero violations in the migrated repo tree. The
/// inline-snippet tests below prove the matcher has teeth against both patterns.</para>
/// </summary>
public sealed class TestRealGitGuardTests
{
    /// <summary>
    /// Teeth: <c>new GitInvoker()</c> in a test file is flagged.
    /// </summary>
    [Fact]
    public void NewGitInvoker_InTestFile_IsFlagged()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/GitTest.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("new GitInvoker()", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teeth: <c>new TestNamespace.GitInvoker()</c> (qualified) is flagged.
    /// </summary>
    [Fact]
    public void QualifiedNewGitInvoker_InTestFile_IsFlagged()
    {
        const string source =
            "class C { void M() { var g = new VisualRelay.Core.Execution.GitInvoker(); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/Qualified.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Teeth: <c>new ProcessStartInfo("git")</c> in a test file is flagged.
    /// </summary>
    [Fact]
    public void ProcessStartInfoGit_InTestFile_IsFlagged()
    {
        const string source =
            "class C { void M() { var p = new ProcessStartInfo(\"git\"); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/PsiTest.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("ProcessStartInfo", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>new ProcessStartInfo("dotnet")</c> is NOT flagged — only "git" triggers.
    /// </summary>
    [Fact]
    public void ProcessStartInfoDotnet_IsNotFlagged()
    {
        const string source =
            "class C { void M() { var p = new ProcessStartInfo(\"dotnet\", \"build\"); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/DotnetPsi.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// <c>new GitInvoker()</c> in a src/ path (not test) is NOT flagged.
    /// </summary>
    [Fact]
    public void NewGitInvoker_InSourcePath_IsNotFlagged()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("src/VisualRelay.Core/Fixtures/Prod.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Opt-in parity suite file is exempt: <c>RealGitIntegrationTests.cs</c>
    /// can contain <c>new GitInvoker()</c>.
    /// </summary>
    [Fact]
    public void RealGitIntegrationTestsFile_IsExempt()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/RealGitIntegrationTests.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// ParityHarness.cs is exempt.
    /// </summary>
    [Fact]
    public void ParityHarnessFile_IsExempt()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/ParityHarness.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// The guard's own test file is self-exempt.
    /// </summary>
    [Fact]
    public void Self_IsExempt()
    {
        const string source = "class C { void M() { var g = new GitInvoker(); } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/TestRealGitGuardTests.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A clean test file with no real-git constructs yields zero violations.
    /// </summary>
    [Fact]
    public void CleanTestFile_IsNotReported()
    {
        const string source = "class C { void M() { var x = 1 + 1; } }";

        var violations = TestRealGitGuard.FindViolations(
            [("tests/VisualRelay.Tests/Fixtures/Clean.cs", source)]);

        Assert.Empty(violations);
    }

    // ── Live-tree test ─────────────────────────────────────────────────────

    private readonly CachedSyntaxTreesFixture _trees;

    public TestRealGitGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// The live enforcing gate: every non-exempt file under
    /// <c>tests/VisualRelay.Tests/</c> must have zero <c>new GitInvoker(</c>
    /// and zero <c>ProcessStartInfo("git")</c>. The opt-in parity suite
    /// (RealGitIntegrationTests.cs, RealGitIntegrationDriverTests.cs,
    /// ParityHarness.cs) and guard-test files are the only exemptions.
    /// </summary>
    [Fact]
    public void LiveTree_HasNoRealGitInDefaultSuite()
    {
        var trees = _trees.AllTrees
            .Where(t => t.RelativePath.StartsWith("tests/", StringComparison.Ordinal))
            .ToList();

        var violations = TestRealGitGuard.FindViolations(trees);

        Assert.True(violations.Count == 0,
            "TestRealGitGuard found real-git fallbacks in the default test suite " +
            "(inject GitSim — in-memory IGitInvoker — instead; only the opt-in parity " +
            "suite RealGitIntegrationTests.cs / ParityHarness.cs is exempt):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Reason} — {v.Snippet}")));
    }
}
