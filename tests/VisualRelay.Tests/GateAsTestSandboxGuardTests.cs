using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing gate-as-test sandbox guard (a VR-specific sibling of
/// <see cref="RealBuildSubprocessGuardTests"/>). The matcher
/// (<see cref="GateAsTestSandboxGuard.FindViolations"/>) is pure: it Roslyn-parses
/// each source and flags a test that invokes a shell-out dev-gate entry point
/// — <c>…Gate.Run(…)</c>, e.g. <c>InspectCodeGate.Run(paths)</c> — without the
/// <c>VR_RUN_NONO_INTEGRATION</c> opt-in skip-guard. Such a gate-as-test re-runs a
/// whole dev gate end-to-end inside the test suite; under the verify's nono
/// sandbox it false-fails because the external tool is denied a host write /
/// keychain lookup (the InspectCode regression this guard prevents recurring).
/// The build-subprocess guard structurally cannot see it: the spawn is indirect
/// (a variable program inside the gate, with a non-build verb), so a dedicated
/// gate-aware guard is required.
///
/// This is dev-gate test-infra for VR's OWN repo (it keys on VR's <c>…Gate</c>
/// convention), not the general-purpose relay engine — so a VR-specific symbol is
/// appropriate here. The carve-out (the env-gated opt-in skip) is shared with the
/// build-subprocess guard via <c>SandboxSkipScan</c>, so both guards accept the
/// one well-known marker. This file is self-exempt by filename because it carries
/// the gate-call fixtures.
/// </summary>
public sealed class GateAsTestSandboxGuardTests
{
    /// <summary>Gate bites: an unguarded <c>InspectCodeGate.Run(paths)</c> is reported.</summary>
    [Fact]
    public void UnguardedGateRun_IsReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var paths = RepoPaths.Resolve();
                    var rc = InspectCodeGate.Run(paths);
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Gate.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Gate.cs", v.Path);
        Assert.Contains("InspectCodeGate.Run", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>A fully-qualified receiver (<c>Gates.InspectCodeGate.Run</c>) is reported too.</summary>
    [Fact]
    public void FullyQualifiedGateRun_IsReported()
    {
        const string source =
            "class C { void M() { var rc = Gates.InspectCodeGate.Run(paths); } }";

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Q.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Skip-gate carve-out: a <c>…Gate.Run</c> whose method opts out via a
    /// <c>SkipIfNotOptedIn()</c> helper is not reported — it skips in the sandboxed
    /// default run, so it cannot false-fail.
    /// </summary>
    [Fact]
    public void GateRun_GatedBySkipHelper_IsNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    SkipIfNotOptedIn();
                    var rc = InspectCodeGate.Run(paths);
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Skip.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Skip-gate carve-out: the qualified <c>NonoIntegration.SkipIfNotOptedIn()</c>
    /// form (the opt-in helper called WITHOUT <c>using static</c>) is recognised the
    /// same as the bare form — the invoked method's rightmost name is the known opt-in
    /// helper — so the gate is not reported.
    /// </summary>
    [Fact]
    public void GateRun_GatedByQualifiedSkipHelper_IsNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    NonoIntegration.SkipIfNotOptedIn();
                    var rc = InspectCodeGate.Run(paths);
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Qualified.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Precision: an unrelated <c>Skip…()</c> call that is NOT the opt-in helper
    /// (e.g. <c>SkipWhitespace()</c>, with no env read) does NOT excuse the gate — only
    /// the known <c>SkipIfNotOptedIn</c> opt-in helper counts as a sandbox opt-out, so
    /// the gate is still reported.
    /// </summary>
    [Fact]
    public void GateRun_WithUnrelatedSkipPrefixedCall_IsStillReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    SkipWhitespace();
                    var rc = InspectCodeGate.Run(paths);
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/SkipPrefix.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Skip-gate carve-out: an <c>Assert.Skip(…)</c> alongside an env-var read in the
    /// same method (the VR_RUN_NONO_INTEGRATION idiom) is not reported.
    /// </summary>
    [Fact]
    public void GateRun_GatedByEnvGatedAssertSkip_IsNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    if (Environment.GetEnvironmentVariable("VR_RUN_NONO_INTEGRATION") != "1")
                        Assert.Skip("opt-in required");
                    var rc = InspectCodeGate.Run(paths);
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Env.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// No false positives: a <c>.Run</c> on a receiver that is not a <c>…Gate</c>
    /// (e.g. <c>TestGit.Run</c>) is never reported — only the shell-out gate
    /// convention is in scope.
    /// </summary>
    [Fact]
    public void NonGateRun_IsNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    TestGit.Run(repo.Root, "status");
                    helper.Run();
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/NonGate.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Precision: an UNRELATED platform skip (<c>OperatingSystem.IsMacOS</c> → bare
    /// <c>Assert.Skip</c>) with no env-var opt-in does NOT excuse the gate — it still
    /// runs under nono on the macOS verify host, so it is still reported.
    /// </summary>
    [Fact]
    public void GateRun_WithUnrelatedPlatformSkip_IsStillReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    if (!OperatingSystem.IsMacOS()) Assert.Skip("macOS only");
                    var rc = InspectCodeGate.Run(paths);
                }
            }
            """;

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Platform.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Allow-marker carve-out: a <c>…Gate.Run</c> line carrying
    /// <c>// vr-allow-gate-as-test: &lt;reason&gt;</c> is not reported.
    /// </summary>
    [Fact]
    public void GateRun_WithAllowMarkerCarryingReason_IsNotReported()
    {
        const string source =
            "class C { void M() { var rc = InspectCodeGate.Run(paths); } } // vr-allow-gate-as-test: documented reason";

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Allowed.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A bare marker is not a valid suppression: <c>// vr-allow-gate-as-test:</c> with
    /// no reason after the colon does not excuse the gate — it is still reported.
    /// </summary>
    [Fact]
    public void GateRun_WithBareAllowMarkerLackingReason_IsStillReported()
    {
        const string source =
            "class C { void M() { var rc = InspectCodeGate.Run(paths); } } // vr-allow-gate-as-test:";

        var violations = GateAsTestSandboxGuard.FindViolations([("Fixtures/Bare.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// The live enforcing gate: every <c>*.cs</c> in the test project (excluding
    /// bin/obj) is free of un-skip-guarded shell-out gate-as-tests. This keeps the
    /// full-suite verify GREEN inside the strict nono sandbox — any reintroduced
    /// unguarded <c>…Gate.Run</c> gate-as-test flips the guard to a build failure.
    /// Carry the <c>VR_RUN_NONO_INTEGRATION</c> opt-in skip-guard or annotate with
    /// <c>// vr-allow-gate-as-test: &lt;reason&gt;</c>.
    /// </summary>
    [Fact]
    public void AllTestProjectCsFiles_AreGateAsTestSandboxSafe()
    {
        var testsDir = Path.Combine(RepoSetup.Root, "tests", "VisualRelay.Tests");

        var files = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(RepoSetup.Root, f), File.ReadAllText(f)))
            .ToList();

        var violations = GateAsTestSandboxGuard.FindViolations(files);

        Assert.True(violations.Count == 0,
            "gate-as-test guard found un-skip-guarded shell-out gate-as-tests in the test suite " +
            "(carry the VR_RUN_NONO_INTEGRATION opt-in skip-guard, or annotate with " +
            "// vr-allow-gate-as-test: <reason>):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}")));
    }

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
