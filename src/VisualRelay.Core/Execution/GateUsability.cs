using System.Text.RegularExpressions;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Whether a test run could not execute meaningfully — the command was not found, the
/// dependencies never arrived, the build tool never started, or nothing was collected.
/// A red from any of those says nothing about the tests, so the author-tests gate
/// records it as unproven rather than passing it vacuously.
/// <para>
/// Shared, because bootstrap asks the same question when it proves a per-file command
/// before writing it: a form that runs no tests is exactly as useless there.
/// </para>
/// </summary>
internal static class GateUsability
{
    /// <summary>Restore and dependency resolution failures: NuGet, Maven, Gradle, pip.</summary>
    private static readonly Regex DependencyFetchFailure =
        new(@"Failed to read NuGet\.Config|error NU1301:|Could not resolve dependencies for project"
            + "|Could not resolve all (?:dependencies|files) for configuration|No matching distribution found for",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A build tool that stopped while setting itself up, before any task or test ran.</summary>
    private static readonly Regex BuildToolStartFailure =
        new(@"Gradle could not start your build\.", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Zero-tests pattern: "0 tests" / "0 tests collected" / "ran 0 tests"
    /// but NOT "10 tests" / "230 tests" / "Ran 100 tests".  The regex
    /// requires the zero to be a standalone number (not preceded by another
    /// digit).
    /// </summary>
    private static readonly Regex ZeroTestsPattern =
        new(@"(?<!\d)0\s+tests",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns true when the test runner could not execute meaningfully (command
    /// not found, or zero tests collected), making the red-gate assertion
    /// untrustworthy. Avoids silently passing a gate whose infrastructure is
    /// broken — independent of any specific toolchain.
    /// </summary>
    internal static bool IsUnusable(TestRunResult result)
    {
        // Exit code 127 = command not found (POSIX convention; also followed
        // by many shells and process runners on non-POSIX platforms).
        if (result.ExitCode == 127)
            return true;

        // Zero-tests-collected patterns produced by common runners when the
        // command can start but finds no tests to execute. The heuristic is
        // intentionally loose (case-insensitive) — a false positive here
        // records an unproven gate rather than passing it vacuously.
        var output = result.Output;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        // A run that could not fetch its dependencies never reached the tests, so its exit
        // code says nothing about them. Measured with LiteDB in WSL: the sandbox denied NuGet
        // its user config and the gate took the restore failure for the new test's red. A
        // compile error the new test causes is not among these: that is a legitimate red.
        if (DependencyFetchFailure.IsMatch(output))
            return true;

        // Nor did a build tool that could not start. Measured with Unciv in WSL: gradle could not open
        // its own file hash lock, stopped in 556 ms with nothing compiled, and the gate took it for red.
        if (BuildToolStartFailure.IsMatch(output))
            return true;

        // A run that names its failing tests ran tests, whatever else it printed. Measured on the Windows
        // arm with zeroshot's cargo workspace: members without tests print "running 0 tests", and the
        // zero-tests checks below recorded two genuine failures as an unproven gate.
        if (TestFailureIds.Extract(output).Count > 0)
            return false;

        return output.Contains("no tests found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no tests collected", StringComparison.OrdinalIgnoreCase)
            || ZeroTestsPattern.IsMatch(output)
            || output.Contains("zero tests", StringComparison.OrdinalIgnoreCase);
    }
}
