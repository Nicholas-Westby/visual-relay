using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing real-sleep guard-as-test (the house idiom mirrored from
/// <see cref="ShellScriptSizeGuardTests"/>). The matcher
/// (<see cref="RealSleepGuard.FindViolations"/>) is pure: it Roslyn-parses each
/// source and flags real sleeps — shell <c>sleep N</c> embedded in string literals,
/// the <c>("sleep","30")</c> argv form, and long uncancellable
/// <c>Thread.Sleep</c>/<c>Task.Delay</c> calls — while string-literal-token scoping
/// makes doc-comment / identifier false positives impossible by construction.
///
/// For now this file carries only the matcher's behavioural facts plus the live
/// enumeration gate, which is <c>[Fact(Skip)]</c> until Part B of
/// harness-no-real-sleeps-in-tests makes the suite sleep-free (the real watchdog
/// tests still embed real shell sleeps today, so the live gate is RED). Part B
/// un-skips it.
///
/// This file is self-exempt by filename in <see cref="RealSleepGuard"/> because it
/// contains sleep fixtures; that exemption is exactly why the live gate can scan
/// the test project without tripping on these strings.
/// </summary>
public sealed class RealSleepGuardTests
{
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
    /// No false positives: <c>sleep 30</c> inside a <c>///</c> doc comment, an identifier
    /// named <c>SleepDuration</c>, and a short cancellable <c>Task.Delay(50, ct)</c> yield
    /// zero violations. The doc-comment immunity is the whole point of scoping the regex to
    /// string-literal tokens — comments are trivia, never literal tokens.
    /// </summary>
    [Fact]
    public void DocCommentSleep_Identifier_AndShortCancellableDelay_AreNotReported()
    {
        const string source = """
            class C
            {
                /// <summary>waits like <c>sleep 30</c> would, but in-process.</summary>
                int SleepDuration = 50;
                Task M(CancellationToken ct) => Task.Delay(50, ct);
            }
            """;

        var violations = RealSleepGuard.FindViolations([("Fixtures/Clean.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Cancellation carve-out (no real token): a long <c>Task.Delay(60_000, CancellationToken.None)</c>
    /// is reported because <c>.None</c> is not a real token — nothing can cut the wait short.
    /// </summary>
    [Fact]
    public void LongDelay_WithCancellationTokenNone_IsReported()
    {
        const string source = "class C { Task M() => Task.Delay(60_000, CancellationToken.None); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/None.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Cancellation carve-out (real token): the same long <c>Task.Delay(60_000, ct)</c> driven by a
    /// real <see cref="CancellationToken"/> is not reported — the test can cut the wait short.
    /// </summary>
    [Fact]
    public void LongDelay_WithRealCancellationToken_IsNotReported()
    {
        const string source = "class C { Task M(CancellationToken ct) => Task.Delay(60_000, ct); }";

        var violations = RealSleepGuard.FindViolations([("Fixtures/Real.cs", source)]);

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
    /// The live enforcing gate: every <c>*.cs</c> in the test project (excluding bin/obj)
    /// is free of real sleeps. Now ENABLED — Part B of harness-no-real-sleeps-in-tests
    /// made the suite sleep-free: the timing-sensitive watchdog tests were rewritten to
    /// a 0-CPU block-forever child (Class A) or the pure ActivityWatchdog.DecideOutcome
    /// decision driven by simulated time values (Class B), so no test embeds a real
    /// shell sleep as a "won't-stop-on-its-own" stand-in. This fact keeps the suite that
    /// way: any reintroduced real sleep flips the guard to a build failure.
    /// </summary>
    [Fact]
    public void AllTestProjectCsFiles_AreSleepFree()
    {
        var testsDir = Path.Combine(RepoSetup.Root, "tests", "VisualRelay.Tests");

        var files = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(RepoSetup.Root, f), File.ReadAllText(f)))
            .ToList();

        var violations = RealSleepGuard.FindViolations(files);

        Assert.True(violations.Count == 0,
            "real-sleep guard found sleeps in the test suite (make them sleep-free via a " +
            "block-forever child or an injected clock; do not suppress):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}")));
    }

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
