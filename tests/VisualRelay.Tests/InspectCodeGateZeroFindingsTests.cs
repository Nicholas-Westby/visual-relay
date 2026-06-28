using VisualRelay.Cli;
using VisualRelay.Cli.Gates;

namespace VisualRelay.Tests;

/// <summary>
/// Gate-as-test: runs the InspectCode analysis over the full solution and asserts
/// zero findings at the SUGGESTION floor. This is the same check that
/// <c>./visual-relay check</c> step 6 enforces; moving it into the test suite
/// gives the implementing agent a fast red→green cycle for the
/// harness-clear-inspect-code-debt clean-up pass.
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
    /// developer can triage each one — same behaviour as the CLI gate.
    /// </summary>
    [Fact]
    public void InspectCode_ReportsZeroFindings()
    {
        var paths = RepoPaths.Resolve();

        var exitCode = InspectCodeGate.Run(paths);

        Assert.Equal(0, exitCode);
    }
}
