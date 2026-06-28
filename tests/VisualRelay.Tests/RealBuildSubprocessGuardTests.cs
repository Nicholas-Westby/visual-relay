using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing build-subprocess guard-as-test (the house idiom mirrored from
/// <see cref="RealSleepGuardTests"/>). The matcher
/// (<see cref="RealBuildSubprocessGuard.FindViolations"/>) is pure: it
/// Roslyn-parses each source and flags a test that launches a REAL heavy build
/// subprocess — <c>dotnet build|publish|run</c>, <c>cargo build</c>,
/// <c>npm install</c>, … — without a timeout, a skip-gate, or a suppression.
/// Such a child can wedge under the verify's nono sandbox (a macOS <c>dotnet</c>
/// child intermittently blocks on the denied <c>com.apple.SecurityServer</c>
/// mach-lookup), tripping the test host's blame-hang collector and aborting the
/// whole run. Reading the program/verb from string LITERALS only makes
/// native-tool (variable <c>FileName</c>) and assertion-string false positives
/// impossible by construction.
///
/// This file is self-exempt by filename in <see cref="RealBuildSubprocessGuard"/>
/// because it carries the spawn fixtures; that exemption is exactly why the live
/// gate can scan the test project without tripping on these strings.
/// </summary>
public sealed class RealBuildSubprocessGuardTests
{
    /// <summary>
    /// Gate bites: an unbounded <c>dotnet build</c> launched via a
    /// <c>ProcessStartInfo</c> initializer (the PackagingTool shape) is reported.
    /// </summary>
    [Fact]
    public void UnboundedDotnetBuild_ViaProcessStartInfo_IsReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        ArgumentList = { "build", "--nologo" },
                    };
                    using var p = Process.Start(psi);
                    p.WaitForExit();
                }
            }
            """;

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Spawn.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Spawn.cs", v.Path);
        Assert.Contains("dotnet build", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>new ProcessStartInfo("dotnet", "publish …")</c> ctor form is reported
    /// even though program and verb live in separate constructor arguments.
    /// </summary>
    [Fact]
    public void UnboundedDotnetPublish_ViaCtorArgs_IsReported()
    {
        const string source =
            "class C { void M() { var p = Process.Start(new ProcessStartInfo(\"dotnet\", \"publish .\")); p.WaitForExit(); } }";

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Ctor.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// The bare <c>Process.Start("dotnet", "run")</c> overload is reported too.
    /// </summary>
    [Fact]
    public void UnboundedDotnetRun_ViaProcessStartOverload_IsReported()
    {
        const string source =
            "class C { void M() { var p = Process.Start(\"dotnet\", \"run --project x\"); p.WaitForExit(); } }";

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Overload.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>Generality: a non-.NET toolchain (<c>cargo build</c>) is flagged the same way.</summary>
    [Fact]
    public void UnboundedCargoBuild_IsReported()
    {
        const string source =
            "class C { void M() { var p = Process.Start(new ProcessStartInfo(\"cargo\", \"build\")); p.WaitForExit(); } }";

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Cargo.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// Timeout carve-out: a <c>dotnet build</c> bounded by <c>WaitForExit(60_000)</c>
    /// is not reported — a wedge degrades to a kill instead of hanging the host.
    /// </summary>
    [Fact]
    public void DotnetBuild_BoundedByWaitForExitTimeout_IsNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    var p = Process.Start(new ProcessStartInfo("dotnet", "build"));
                    if (!p.WaitForExit(60_000)) { p.Kill(true); }
                }
            }
            """;

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Timeout.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Skip-gate carve-out: a <c>dotnet build</c> whose method opts out via an
    /// <c>Assert.Skip(...)</c> or a <c>SkipIfNotOptedIn()</c> helper is not reported —
    /// the test skips in the sandboxed default run, so it cannot wedge.
    /// </summary>
    [Fact]
    public void DotnetBuild_GatedBySkipHelper_IsNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    SkipIfNotOptedIn();
                    var p = Process.Start(new ProcessStartInfo("dotnet", "build"));
                    p.WaitForExit();
                }
            }
            """;

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Skip.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Allow-marker carve-out: a <c>dotnet build</c> line carrying
    /// <c>// vr-allow-subprocess: documented reason</c> is not reported.
    /// </summary>
    [Fact]
    public void DotnetBuild_WithAllowMarkerCarryingReason_IsNotReported()
    {
        const string source =
            "class C { void M() { var p = Process.Start(new ProcessStartInfo(\"dotnet\", \"build\")); p.WaitForExit(); } } // vr-allow-subprocess: documented reason";

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Allowed.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A bare marker is not a valid suppression: <c>// vr-allow-subprocess:</c> with no
    /// reason after the colon does not excuse the spawn — it is still reported.
    /// </summary>
    [Fact]
    public void DotnetBuild_WithBareAllowMarkerLackingReason_IsStillReported()
    {
        const string source =
            "class C { void M() { var p = Process.Start(new ProcessStartInfo(\"dotnet\", \"build\")); p.WaitForExit(); } } // vr-allow-subprocess:";

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Bare.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// No false positives: a variable <c>FileName</c> (a PATH-resolved native tool like
    /// <c>magick</c>/<c>iconutil</c>), an assertion string that merely mentions
    /// "dotnet build", and a lightweight scaffolding verb (<c>dotnet new</c>) all yield
    /// zero violations. Literal-only program/verb reading is what makes this immune.
    /// </summary>
    [Fact]
    public void VariableFileName_AssertionString_AndScaffoldVerb_AreNotReported()
    {
        const string source = """
            class C
            {
                void Native(string magickPath)
                {
                    var p = Process.Start(new ProcessStartInfo { FileName = magickPath, ArgumentList = { "build" } });
                    p.WaitForExit();
                }
                void Assertion() => Assert.Contains("regenerate with dotnet build", log);
                void Scaffold()
                {
                    var p = Process.Start(new ProcessStartInfo("dotnet", "new xunit --no-restore"));
                    p.WaitForExit();
                }
            }
            """;

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Clean.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Git and shell spawns are never build tools, so they are not reported even when
    /// unbounded — only the known build/package toolchains are in scope.
    /// </summary>
    [Fact]
    public void GitAndShellSpawns_AreNotReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    Process.Start(new ProcessStartInfo("git", "status")).WaitForExit();
                    Process.Start(new ProcessStartInfo("/bin/sh", "-c \"echo hi\"")).WaitForExit();
                }
            }
            """;

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/Git.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Precision: an UNRELATED platform skip (<c>OperatingSystem.IsMacOS</c>) with no
    /// env-var opt-in and no timeout does NOT excuse the spawn — the build child still
    /// runs on the macOS verify host, so it is still reported. Only an env-gated
    /// <c>Assert.Skip</c> (or a <c>Skip…()</c> helper) counts as a sandbox opt-out.
    /// </summary>
    [Fact]
    public void DotnetBuild_WithUnrelatedPlatformSkip_NoTimeout_IsStillReported()
    {
        const string source = """
            class C
            {
                void M()
                {
                    if (!OperatingSystem.IsMacOS()) Assert.Skip("macOS only");
                    var p = Process.Start(new ProcessStartInfo("dotnet", "build"));
                    p.WaitForExit();
                }
            }
            """;

        var violations = RealBuildSubprocessGuard.FindViolations([("Fixtures/PlatformSkip.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// The live enforcing gate: every <c>*.cs</c> in the test project (excluding bin/obj)
    /// is free of unbounded real build subprocesses. This keeps the full-suite verify
    /// able to complete inside the strict nono sandbox — any reintroduced unguarded
    /// <c>dotnet</c>/<c>cargo</c>/<c>npm</c>/… build spawn flips the guard to a build
    /// failure. Add a timeout, carry the opt-in skip-guard, or annotate with
    /// <c>// vr-allow-subprocess: &lt;reason&gt;</c>.
    /// </summary>
    [Fact]
    public void AllTestProjectCsFiles_AreSandboxBuildSafe()
    {
        var testsDir = Path.Combine(RepoSetup.Root, "tests", "VisualRelay.Tests");

        var files = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(RepoSetup.Root, f), File.ReadAllText(f)))
            .ToList();

        var violations = RealBuildSubprocessGuard.FindViolations(files);

        Assert.True(violations.Count == 0,
            "build-subprocess guard found unbounded real build spawns in the test suite " +
            "(add a timeout, carry the VR_RUN_NONO_INTEGRATION skip-guard, or annotate with " +
            "// vr-allow-subprocess: <reason>):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}")));
    }

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
