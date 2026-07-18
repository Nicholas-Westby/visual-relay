using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing real-sleep guard-as-test (the house idiom mirrored from
/// <see cref="ShellScriptSizeGuardTests"/>). The matcher
/// (<see cref="RealSleepGuard.FindViolations"/>) is pure: it Roslyn-parses each
/// source and flags real sleeps — shell <c>sleep N</c> embedded in string literals,
/// the <c>("sleep","30")</c> argv form, every <c>Thread.Sleep</c>, and every
/// <c>Task.Delay</c> lacking a TimeProvider argument — while string-literal-token scoping
/// makes doc-comment / identifier false positives impossible by construction.
///
/// This file carries the matcher's behavioural facts plus the live enumeration
/// gate. Part B of harness-no-real-sleeps-in-tests made the suite sleep-free —
/// the timing-sensitive watchdog tests were rewritten to use a ManualTimeProvider
/// or pure decision-seam assertions, so the live gate is GREEN and stays that way.
///
/// This file is self-exempt by filename in <see cref="RealSleepGuard"/> because it
/// contains sleep fixtures; that exemption is exactly why the live gate can scan
/// the test project without tripping on these strings.
/// </summary>
public sealed class RealSleepGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public RealSleepGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }
    /// <summary>
    /// Gate bites: a shell <c>sleep 30</c> sitting in a C# string literal is reported.
    /// This is the core build-failing behaviour — a real sleep in the source is found.
    /// </summary>
    [Fact]
    public void ShellSleepInsideStringLiteral_IsReported()
    {
        const string source = "class C { const string Cmd = \"sleep 30\"; }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Sleeper.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Sleeper.cs", v.Path);
        Assert.Equal(1, v.Line);
    }

    /// <summary>
    /// The quoted-argv form a process launch takes — <c>new ProcessStartInfo("sleep", "30")</c>
    /// or <c>ArgumentList = { "sleep", "30" }</c> — is reported even though the duration
    /// lives in a separate string token from the verb.
    /// </summary>
    [Fact]
    public void ShellSleepViaArgvStrings_IsReported()
    {
        const string source = "class C { object M() => new ProcessStartInfo(\"sleep\", \"30\"); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Argv.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Argv.cs", v.Path);
    }

    /// <summary>
    /// No false positives on non-calls: <c>sleep 30</c> inside a <c>///</c> doc comment and an
    /// identifier named <c>SleepDuration</c> yield zero violations. The doc-comment immunity is
    /// the whole point of scoping the shell regex to string-literal tokens — comments are trivia,
    /// never literal tokens — and an identifier is not a <c>Thread.Sleep</c>/<c>Task.Delay</c> call.
    /// </summary>
    [Fact]
    public void DocCommentSleep_AndIdentifier_AreNotReported()
    {
        const string source = """
            class C
            {
                /// <summary>waits like <c>sleep 30</c> would, but in-process.</summary>
                int SleepDuration = 50;
            }
            """;

        var violations = RealSleepGuard.FindViolations([("Fixtures/Clean.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Loophole closed: a SHORT, cancellable <c>Task.Delay(50, ct)</c> is now reported — a real
    /// <see cref="CancellationToken"/> no longer exempts a delay (only a TimeProvider does), and
    /// there is no duration floor. This is the exact case the old matcher let through.
    /// </summary>
    [Fact]
    public void ShortCancellableDelay_WithoutTimeProvider_IsNowReported()
    {
        const string source = "class C { Task M(CancellationToken ct) => Task.Delay(50, ct); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Short.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// A bare 1-arg <c>Task.Delay(1)</c> — the old advance-yield idiom — is reported: no
    /// TimeProvider argument, so it is a real (if tiny) wall-clock delay.
    /// </summary>
    [Fact]
    public void BareDelay_NoTimeProvider_IsReported()
    {
        const string source = "class C { Task M() => Task.Delay(1); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Bare.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// <c>Task.Delay(60_000, CancellationToken.None)</c> is reported — no TimeProvider argument.
    /// </summary>
    [Fact]
    public void LongDelay_WithCancellationTokenNone_IsReported()
    {
        const string source = "class C { Task M() => Task.Delay(60_000, CancellationToken.None); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/None.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Every <c>Thread.Sleep(...)</c> is reported regardless of duration — it has no
    /// virtual-clock overload, so it is always a real wall-clock sleep.
    /// </summary>
    [Fact]
    public void ThreadSleep_ShortDuration_IsReported()
    {
        const string source = "class C { void M() => Thread.Sleep(10); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Sleep.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// The sanctioned virtual delay: a 3-arg <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c>
    /// is NOT reported — the only 3-argument overload carries a TimeProvider (the virtual-clock seam).
    /// </summary>
    [Fact]
    public void Delay_WithTimeProviderAndToken_IsNotReported()
    {
        const string source =
            "class C { Task M(TimeProvider tp, CancellationToken ct) => Task.Delay(TimeSpan.FromMilliseconds(50), tp, ct); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Virtual.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// The 2-arg <c>Task.Delay(TimeSpan, TimeProvider.System)</c> form is NOT reported — the
    /// second argument is recognisably a TimeProvider, not a CancellationToken.
    /// </summary>
    [Fact]
    public void Delay_WithTwoArgTimeProviderSystem_IsNotReported()
    {
        const string source =
            "class C { Task M() => Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/VirtualTwoArg.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Slow-integration files are exempt by filename: their real waits run only behind
    /// <c>SlowIntegration.SkipIfNotOptedIn()</c>. A <c>Thread.Sleep</c> in one of them is not
    /// reported (mirrors the guard's own fixture self-exemption).
    /// </summary>
    [Fact]
    public void RealIntegrationExemptFile_IsNotScanned()
    {
        const string source = "class C { void M() => Thread.Sleep(5000); }";

        var violations = RealSleepGuard.FindViolations(
            [("tests/VisualRelay.Tests/ProcessCaptureGracefulStopTests.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// The allow-list marker suppresses: a <c>sleep 30</c> line carrying
    /// <c>// vr-allow-sleep: documented reason</c> is not reported.
    /// </summary>
    [Fact]
    public void ShellSleep_WithAllowMarkerCarryingReason_IsNotReported()
    {
        const string source =
            "class C { const string Cmd = \"sleep 30\"; } // vr-allow-sleep: documented reason";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Allowed.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A bare marker is not a valid suppression: <c>// vr-allow-sleep:</c> with no reason
    /// after the colon does not excuse the sleep — it is still reported.
    /// </summary>
    [Fact]
    public void ShellSleep_WithBareAllowMarkerLackingReason_IsStillReported()
    {
        const string source =
            "class C { const string Cmd = \"sleep 30\"; } // vr-allow-sleep:";

        var violations = RealSleepGuard.FindViolations([("Fixtures/BareMarker.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// The live enforcing gate: every always-on <c>*.cs</c> in the test project (excluding
    /// bin/obj) is free of real sleeps. The matcher now flags EVERY <c>Thread.Sleep</c> and
    /// every <c>Task.Delay</c> lacking a TimeProvider — any duration, cancellable or not — so
    /// the only sanctioned delay is the virtual-clock form <c>Task.Delay(ts, timeProvider, ct)</c>.
    /// Timing-sensitive supervision facts are driven on a ManualTimeProvider (the watchdog loop,
    /// cpu-sample cadence, and trace poll all read the injected clock); advance-yield loops use
    /// <c>Task.Yield()</c> (a pure scheduler yield, not a wall-clock wait). Any reintroduced real
    /// sleep flips the guard to a build failure.
    ///
    /// The genuine OS-semantics facts (real kill escalation, setpgid reap, SIGINT trap, socket
    /// wedge) keep their real processes and windows but run only behind
    /// <c>SlowIntegration.SkipIfNotOptedIn()</c>; their files are exempt by filename via
    /// <c>RealSleepGuard.RealIntegrationExemptFileNames</c> and each has an always-on
    /// virtual-clock sibling asserting the same decision logic.
    /// </summary>
    [Fact]
    public void AllTestProjectCsFiles_AreSleepFree()
    {
        var trees = _trees.AllTrees
            .Where(t => t.RelativePath.StartsWith("tests/VisualRelay.Tests/", StringComparison.Ordinal))
            .ToList();

        var violations = RealSleepGuard.FindViolations(trees);

        Assert.True(violations.Count == 0,
            "real-sleep guard found sleeps in the test suite (make them sleep-free via a " +
            "block-forever child or an injected clock; do not suppress):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}")));
    }

}
