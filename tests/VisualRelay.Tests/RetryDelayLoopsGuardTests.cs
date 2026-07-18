using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="RetryDelayLoopsGuard"/> — detects for/while loops
/// containing both a delay call <c>Task.Delay</c>/<c>Thread.Sleep</c> AND an
/// invocation whose receiver or arguments involve <c>IGitInvoker</c>,
/// <c>RunAsync</c>, or process types. This is the <c>RunGitAsync</c> bug shape:
/// retrying failures that can never succeed.
/// </summary>
public sealed class RetryDelayLoopsGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public RetryDelayLoopsGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// The canonical RunGitAsync shape: a for-loop with Task.Delay backoff AND
    /// a gitInvoker.RunAsync invocation inside. Must be reported.
    /// </summary>
    [Fact]
    public void ForLoop_WithTaskDelay_AndGitInvokerRunAsync_IsReported()
    {
        const string source = """
            class C {
                async Task M(IGitInvoker gi, CancellationToken ct, TimeProvider tp) {
                    for (int attempt = 1; attempt <= 3; attempt++) {
                        var r = await gi.RunAsync("repo", new[]{"status"}, ct);
                        if (r.ExitCode == 0) return;
                        var delay = TimeSpan.FromMilliseconds(250);
                        await Task.Delay(delay, tp, ct);
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/Looper.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// A while-loop with Thread.Sleep AND Process.Start is reported.
    /// </summary>
    [Fact]
    public void WhileLoop_WithThreadSleep_AndProcessStart_IsReported()
    {
        const string source = """
            class C {
                void M() {
                    while (true) {
                        var p = Process.Start("git", "status");
                        Thread.Sleep(500);
                        if (p.ExitCode == 0) break;
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/WhileLooper.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// A for-loop with Task.Delay but NO git/process invocation is NOT reported
    /// (only a delay, no retry of anything hermeticity-sensitive).
    /// </summary>
    [Fact]
    public void ForLoop_WithDelay_WithoutGitOrProcess_IsNotReported()
    {
        const string source = """
            class C {
                async Task M(CancellationToken ct) {
                    for (int i = 0; i < 3; i++) {
                        await Task.Delay(100, ct);
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/DelayOnly.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A for-loop with a git invocation but NO delay call is NOT reported
    /// (no retry backoff — just a single attempt).
    /// </summary>
    [Fact]
    public void ForLoop_WithGitInvocation_WithoutDelay_IsNotReported()
    {
        const string source = """
            class C {
                async Task M(IGitInvoker gi, CancellationToken ct) {
                    for (int attempt = 1; attempt <= 3; attempt++) {
                        var r = await gi.RunAsync("repo", new[]{"log"}, ct);
                        if (r.ExitCode == 0) return;
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/NoDelay.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A delay-only loop (no invocation) is NOT reported.
    /// </summary>
    [Fact]
    public void SimpleRetry_WithoutDelay_IsNotReported()
    {
        const string source = """
            class C {
                void M() {
                    for (int i = 0; i < 3; i++) {
                        var result = SomeCall();
                        if (result) return;
                    }
                }
                bool SomeCall() => false;
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/SimpleRetry.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A loop with delay and invocation where a failure classifier identifier
    /// (isSuccess) is present in the loop body — still reported (the classifier
    /// presence is noted but doesn't exempt the loop).
    /// </summary>
    [Fact]
    public void Loop_WithClassifier_IsStillReported()
    {
        const string source = """
            class C {
                async Task M(IGitInvoker gi, CancellationToken ct, TimeProvider tp) {
                    for (int attempt = 1; attempt <= 3; attempt++) {
                        var r = await gi.RunAsync("repo", new[]{"status"}, ct);
                        bool isSuccess = r.ExitCode == 0;
                        if (isSuccess) return;
                        await Task.Delay(250, tp, ct);
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/Classified.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Task.Delay with TimeProvider (virtual-clock seam) inside a retry loop
    /// with git invocation is STILL reported — the bug is the retry of a
    /// deterministic failure, not the real-vs-virtual delay.
    /// </summary>
    [Fact]
    public void VirtualDelay_InsideRetryLoop_IsStillReported()
    {
        const string source = """
            class C {
                async Task M(IGitInvoker gi, CancellationToken ct, TimeProvider tp) {
                    for (int attempt = 1; attempt <= 3; attempt++) {
                        var r = await gi.RunAsync("repo", new[]{"status"}, ct);
                        if (r.ExitCode == 0) return;
                        await Task.Delay(TimeSpan.FromMilliseconds(250), tp, ct);
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations([("Fixtures/VirtualDelay.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Self-exempt file is not scanned.
    /// </summary>
    [Fact]
    public void SelfExemptFile_IsNotScanned()
    {
        const string source = """
            class C {
                async Task M(IGitInvoker gi, CancellationToken ct, TimeProvider tp) {
                    for (int attempt = 1; attempt <= 3; attempt++) {
                        await gi.RunAsync("repo", new[]{"status"}, ct);
                        await Task.Delay(250, tp, ct);
                    }
                }
            }
            """;

        var violations = RetryDelayLoopsGuard.FindViolations(
            [("tools/VisualRelay.Guards/RetryDelayLoopsGuard.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Live-tree smoke: all four matchers complete without throwing over the
    /// full tree. No count assertions — findings are informational.
    /// </summary>
    [Fact]
    public void AllTrees_CompleteWithoutThrowing()
    {
        var violations = RetryDelayLoopsGuard.FindViolations(_trees.AllTrees);

        Assert.NotNull(violations);
    }
}
