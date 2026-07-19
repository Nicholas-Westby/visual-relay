using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test for <see cref="TestClockInjectionGuard"/>. The guard flags
/// direct <c>GitCommitter.CommitAsync(</c> calls inside
/// <c>tests/VisualRelay.Tests/</c> that omit the <c>timeProvider:</c> named
/// argument — a slip that lets the call silently use real wall-clock time.
/// </summary>
public sealed class TestClockInjectionGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public TestClockInjectionGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    // ── Inline-snippet unit tests ──────────────────────────────────────────

    /// <summary>
    /// Teeth: a bare <c>GitCommitter.CommitAsync(...)</c> call with no
    /// <c>timeProvider:</c> in a test file is flagged.
    /// </summary>
    [Fact]
    public void CommitAsync_WithoutTimeProvider_IsFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C {
                async Task M() {
                    await GitCommitter.CommitAsync("root", "t", "h", [], [], [],
                        null, null, null, new StubInvoker(), CancellationToken.None);
                }
            }
            """;

        var violations = TestClockInjectionGuard.FindViolations(
            [("tests/VisualRelay.Tests/Foo.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("missing timeProvider:", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>GitCommitter.CommitAsync(...)</c> call WITH <c>timeProvider:</c>
    /// is NOT flagged — the injection seam is satisfied.
    /// </summary>
    [Fact]
    public void CommitAsync_WithTimeProvider_IsNotFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C {
                async Task M() {
                    await GitCommitter.CommitAsync("root", "t", "h", [], [], [],
                        null, null, null, new StubInvoker(), CancellationToken.None,
                        timeProvider: TimeProvider.System);
                }
            }
            """;

        var violations = TestClockInjectionGuard.FindViolations(
            [("tests/VisualRelay.Tests/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A bare <c>GitCommitter.CommitAsync(...)</c> call outside
    /// <c>tests/VisualRelay.Tests/</c> (e.g. in <c>src/</c>) is NOT flagged —
    /// the guard only scans the test project.
    /// </summary>
    [Fact]
    public void CommitAsync_OutsideTestProject_IsNotFlagged()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C {
                async Task M() {
                    await GitCommitter.CommitAsync("root", "t", "h", [], [], [],
                        null, null, null, new StubInvoker(), CancellationToken.None);
                }
            }
            """;

        var violations = TestClockInjectionGuard.FindViolations(
            [("src/VisualRelay.Core/Execution/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Happy path: clean code with no <c>GitCommitter.CommitAsync(</c> calls
    /// produces zero violations.
    /// </summary>
    [Fact]
    public void CleanCode_WithNoCommitAsyncCalls_ReportsZero()
    {
        const string source = """
            using VisualRelay.Core.Execution;
            class C {
                async Task M() {
                    await Task.CompletedTask;
                }
            }
            """;

        var violations = TestClockInjectionGuard.FindViolations(
            [("tests/VisualRelay.Tests/Foo.cs", source)]);

        Assert.Empty(violations);
    }

    // ── Live-tree test ─────────────────────────────────────────────────────

    /// <summary>
    /// The live enforcing gate: every C# file under <c>tests/VisualRelay.Tests/</c>
    /// must pass <c>timeProvider:</c> to every direct <c>GitCommitter.CommitAsync(</c>
    /// call. Any omission flips the guard to a build failure.
    /// </summary>
    [Fact]
    public void LiveTree_HasNoMissingTimeProvider()
    {
        var trees = _trees.AllTrees
            .Where(t => t.RelativePath.StartsWith("tests/", StringComparison.Ordinal))
            .ToList();

        var violations = TestClockInjectionGuard.FindViolations(trees);

        Assert.True(violations.Count == 0,
            "TestClockInjectionGuard found GitCommitter.CommitAsync( calls missing timeProvider: " +
            "in tests/VisualRelay.Tests/ (inject ManualTimeProvider or pass TimeProvider.System):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Reason} — {v.Snippet}")));
    }
}
