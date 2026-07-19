using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test for <see cref="RealGitFallbackGuard"/>. The guard flags two
/// patterns under <c>src/VisualRelay.Core/Execution</c>:
/// <list type="number">
///   <item><c>new GitInvoker(</c> object creation — a private real process launcher
///         inside a layer that must receive the invoker through its injection seam;</item>
///   <item>an <c>IGitInvoker</c> (or <c>IGitInvoker?</c>) parameter that declares a
///         default value — the <c>= null</c> that enables call-site omission.</item>
/// </list>
/// <para>The allowlist is empty. After the fix the live-tree test must assert zero
/// violations in <c>src/VisualRelay.Core/Execution</c>. The inline-snippet tests
/// below prove the matcher has teeth against both patterns.</para>
/// </summary>
public sealed class RealGitFallbackGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public RealGitFallbackGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    // ── Inline-snippet unit tests ──────────────────────────────────────────

    /// <summary>
    /// Teeth: <c>new GitInvoker()</c> inside a source file under
    /// <c>src/VisualRelay.Core/Execution</c> is flagged.
    /// </summary>
    [Fact]
    public void NewGitInvoker_InExecutionLayer_IsFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C { void M() { var gi = new GitInvoker(); } }
            """;

        var violations = RealGitFallbackGuard.FindViolations(
            [("src/VisualRelay.Core/Execution/Foo.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("new GitInvoker()", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teeth: an <c>IGitInvoker?</c> parameter with <c>= null</c> default
    /// inside the execution layer is flagged.
    /// </summary>
    [Fact]
    public void OptionalIGitInvokerParam_InExecutionLayer_IsFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C { void M(IGitInvoker? gitInvoker = null) { } }
            """;

        var violations = RealGitFallbackGuard.FindViolations(
            [("src/VisualRelay.Core/Execution/Foo.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("default value", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>new GitInvoker()</c> outside <c>src/VisualRelay.Core/Execution</c>
    /// (e.g. in <c>src/VisualRelay.Core/Init</c>) is NOT flagged — the guard
    /// only scans the execution layer.
    /// </summary>
    [Fact]
    public void NewGitInvoker_OutsideExecutionLayer_IsNotFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C { void M() { var gi = new GitInvoker(); } }
            """;

        var violations = RealGitFallbackGuard.FindViolations(
            [("src/VisualRelay.Core/Init/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// An optional <c>IGitInvoker?</c> parameter outside the execution layer
    /// (e.g. in Init) is NOT flagged — only the execution layer is guarded.
    /// </summary>
    [Fact]
    public void OptionalIGitInvokerParam_OutsideExecutionLayer_IsNotFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C { void M(IGitInvoker? gitInvoker = null) { } }
            """;

        var violations = RealGitFallbackGuard.FindViolations(
            [("src/VisualRelay.Core/Init/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A required <c>IGitInvoker</c> parameter (no default) is NOT flagged —
    /// only optional defaults are violations.
    /// </summary>
    [Fact]
    public void RequiredIGitInvokerParam_IsNotFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C { void M(IGitInvoker gitInvoker) { } }
            """;

        var violations = RealGitFallbackGuard.FindViolations(
            [("src/VisualRelay.Core/Execution/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Happy path: clean code with no <c>new GitInvoker(</c> and no optional
    /// IGitInvoker params produces zero violations.
    /// </summary>
    [Fact]
    public void CleanCode_WithNoViolations_ReportsZero()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C
            {
                readonly IGitInvoker _git;
                C(IGitInvoker git) => _git = git;
                void M() { _git.RunAsync("", [], default); }
            }
            """;

        var violations = RealGitFallbackGuard.FindViolations(
            [("src/VisualRelay.Core/Execution/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    // ── Live-tree test ─────────────────────────────────────────────────────

    /// <summary>
    /// The live enforcing gate: every file under <c>src/VisualRelay.Core/Execution</c>
    /// must have zero <c>new GitInvoker(</c> and zero optional-IGitInvoker defaults.
    /// This is the in-suite mirror of <c>./visual-relay audit di-bypass</c> for the
    /// execution layer specifically.
    /// </summary>
    [Fact]
    public void LiveTree_HasNoRealGitFallbacks()
    {
        var trees = _trees.AllTrees
            .Where(t => t.RelativePath.StartsWith("src/", StringComparison.Ordinal))
            .ToList();

        var violations = RealGitFallbackGuard.FindViolations(trees);

        Assert.True(violations.Count == 0,
            "RealGitFallbackGuard found new GitInvoker() or optional IGitInvoker params " +
            "in src/VisualRelay.Core/Execution (inject IGitInvoker instead, make it required):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Reason} — {v.Snippet}")));
    }
}
