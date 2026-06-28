using VisualRelay.Cli;
using VisualRelay.Cli.Gates;
using static VisualRelay.Tests.NonoIntegration;

namespace VisualRelay.Tests;

/// <summary>
/// Gate-as-test: runs the InspectCode analysis over the full solution and asserts
/// zero findings at the SUGGESTION floor. This is the same check that
/// <c>./visual-relay check</c> step 6 enforces; moving it into the test suite
/// gives the implementing agent a fast red→green cycle for the
/// harness-clear-inspect-code-debt clean-up pass.
///
/// <para><b>Opt-in only</b> (<c>VR_RUN_NONO_INTEGRATION=1</c>). It is SKIPPED in the
/// default suite because the per-task verify runs the whole suite inside the strict
/// nono (<c>vr-guard</c>) sandbox, and InspectCode shells out to JetBrains ReSharper,
/// which the sandbox denies a host write to (no JetBrains cache grant —
/// <c>vr-guard.json write:[]</c>) and a keychain mach-lookup. Left unguarded it
/// FALSE-FAILS an otherwise-green task at verify on a denial intrinsic to VR's own
/// dev gate. <b>Coverage is NOT lost</b>: InspectCode still runs UNSANDBOXED and
/// authoritative as <c>./visual-relay check</c> gate-step 6
/// (<c>CheckCommand.cs</c> → <c>InspectCodeGate.Run</c>). Do not "restore" the
/// unguarded gate — the skip is the fix, and <c>GateAsTestSandboxGuard</c> enforces it.
/// The carve-out matches <c>NonoRealBuildTests</c> via the shared
/// <see cref="NonoIntegration.SkipIfNotOptedIn"/> helper.</para>
///
/// The test fails while findings remain and passes only when the SARIF is empty.
/// Carve-outs are already handled by <c>.editorconfig</c> (9 categories set to
/// <c>none</c>), so any finding counted here is a real defect or a style
/// suggestion that must be fixed or narrowly suppressed.
/// </summary>
public sealed class InspectCodeGateZeroFindingsTests
{
    /// <summary>
    /// Runs the full InspectCode pipeline (tool restore + analysis + SARIF
    /// count) and asserts zero findings. The tool restore is a local-manifest
    /// restore (&lt;5 s when cached) and the analysis is a no-build Roslyn pass
    /// (~15–25 s on this solution), so the whole gate completes in ~20–30 s.
    ///
    /// When findings exist, this test prints the SARIF path to stderr so the
    /// developer can triage each one — same behaviour as the CLI gate. Skipped
    /// unless <c>VR_RUN_NONO_INTEGRATION=1</c> (see the type doc): the sandboxed
    /// verify denies InspectCode's JetBrains cache write, and <c>./visual-relay
    /// check</c> step 6 keeps the unsandboxed gate authoritative.
    /// </summary>
    [Fact]
    public void InspectCode_ReportsZeroFindings()
    {
        SkipIfNotOptedIn(
            "VR_RUN_NONO_INTEGRATION=1 required: the InspectCode gate-as-test shells out to "
            + "JetBrains ReSharper, which the nono verify sandbox denies (JetBrains cache write). "
            + "InspectCode still runs unsandboxed as ./visual-relay check step 6.");

        var paths = RepoPaths.Resolve();

        var exitCode = InspectCodeGate.Run(paths);

        Assert.Equal(0, exitCode);
    }
}
