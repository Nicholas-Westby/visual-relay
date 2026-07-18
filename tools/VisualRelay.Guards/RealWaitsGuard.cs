using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that combines two complementary diagnostics for the
/// <c>./visual-relay audit real-waits</c> rule:
///
/// <list type="number">
///   <item><b>Active violations:</b> delegates to
///         <see cref="RealSleepGuard.FindViolations(IEnumerable{ValueTuple{string, string}})"/>
///         across all non-self-exempt files, tagging findings with a
///         <c>"real-waits: "</c> prefix so the audit renderer can distinguish them.
///         Self-exempts its own fixture carriers AND
///         <see cref="RealSleepGuard"/>'s self-exempt/re-integration-exempt files.</item>
///   <item><b>Suppression inventory:</b> scans every source's text lines for
///         <c>// vr-allow-sleep:</c> markers (with or without a reason) and reports
///         them via <see cref="FindSuppressions"/> so stale exemptions get
///         re-reviewed instead of living forever. Each entry includes the file,
///         line, and the reason text after the colon.</item>
/// </list>
///
/// <para>Self-exempts the matcher's own fixture carriers AND everything
/// <see cref="RealSleepGuard"/> exempts. No I/O, no git — callers supply the
/// (path, source) pairs.</para>
/// </summary>
public static class RealWaitsGuard
{
    /// <summary>Describes a real-wait finding (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Describes a <c>// vr-allow-sleep:</c> suppression marker.</summary>
    public sealed record Suppression(string Path, int Line, string Reason);

    /// <summary>Matches a <c>// vr-allow-sleep:</c> line capturing the optional reason after the colon.</summary>
    private static readonly Regex AllowSleepMarker =
        new(@"//\s*vr-allow-sleep:\s*(.*)", RegexOptions.Compiled);

    private static readonly string[] SelfExemptFileNames =
        ["RealWaitsGuard.cs", "RealWaitsGuardTests.cs"];

    /// <summary>Plus all of RealSleepGuard's self-exempt and real-integration-exempt files.</summary>
    private static readonly string[] RealSleepExemptFileNames =
        ["RealSleepGuard.cs", "RealSleepGuardTests.cs"];

    private static readonly string[] RealIntegrationExemptFileNames =
    [
        "ProcessCaptureGracefulStopTests.cs",
        "SandboxedTestRunnerReapTests.cs",
        "ActivityWatchdogSocketWedgeTests.cs",
        "FdLeakTests.cs",
        "WindowsExecutionTests.cs",
    ];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static bool IsExempt(string fileName) =>
        SelfExemptFileNames.Contains(fileName)
        || RealSleepExemptFileNames.Contains(fileName)
        || RealIntegrationExemptFileNames.Contains(fileName);

    /// <summary>
    /// Returns every real-wait violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Delegates to
    /// <see cref="RealSleepGuard.FindViolations(IEnumerable{ValueTuple{string, string}})"/>
    /// and re-tags findings with a <c>"real-waits: "</c> prefix.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var list = files as IReadOnlyCollection<(string Path, string Source)> ?? files.ToList();

        // Filter out exempt files.
        var filtered = list
            .Where(f => !IsExempt(Path.GetFileName(f.Path)))
            .ToList();

        if (filtered.Count == 0)
            return [];

        var sleepViolations = RealSleepGuard.FindViolations(filtered);

        var violations = new List<Violation>(sleepViolations.Count);
        foreach (var sv in sleepViolations)
        {
            violations.Add(new Violation(sv.Path, sv.Line, sv.Snippet,
                $"real-waits: {sv.Reason}"));
        }

        return violations;
    }

    /// <summary>
    /// Returns every real-wait violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Uses pre-parsed <see cref="SyntaxTree"/>
    /// objects.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, SyntaxTree Tree)> trees)
    {
        var list = trees as IReadOnlyCollection<(string Path, SyntaxTree Tree)> ?? trees.ToList();

        var filtered = list
            .Where(f => !IsExempt(Path.GetFileName(f.Path)))
            .ToList();

        if (filtered.Count == 0)
            return [];

        var sleepViolations = RealSleepGuard.FindViolations(filtered);

        var violations = new List<Violation>(sleepViolations.Count);
        foreach (var sv in sleepViolations)
        {
            violations.Add(new Violation(sv.Path, sv.Line, sv.Snippet,
                $"real-waits: {sv.Reason}"));
        }

        return violations;
    }

    /// <summary>
    /// Returns every <c>// vr-allow-sleep:</c> suppression marker across
    /// <paramref name="files"/>, ordered by path (ordinal) then line.
    /// Non-exempt files only.
    /// </summary>
    public static IReadOnlyList<Suppression> FindSuppressions(IEnumerable<(string Path, string Source)> files)
    {
        var suppressions = new List<Suppression>();

        foreach (var (path, source) in files)
        {
            if (IsExempt(Path.GetFileName(path)))
                continue;

            var lines = source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var match = AllowSleepMarker.Match(lines[i]);
                if (!match.Success)
                    continue;

                var reason = match.Groups[1].Value.Trim();
                if (reason.Length == 0)
                    reason = "(no reason given)";

                suppressions.Add(new Suppression(path, i + 1, reason));
            }
        }

        suppressions.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return suppressions;
    }

    /// <summary>
    /// Returns every <c>// vr-allow-sleep:</c> suppression marker across
    /// <paramref name="trees"/>, ordered by path (ordinal) then line.
    /// Uses pre-parsed <see cref="SyntaxTree"/> objects. Non-exempt files only.
    /// </summary>
    public static IReadOnlyList<Suppression> FindSuppressions(IEnumerable<(string Path, SyntaxTree Tree)> trees)
    {
        var suppressions = new List<Suppression>();

        foreach (var (path, tree) in trees)
        {
            if (IsExempt(Path.GetFileName(path)))
                continue;

            var text = tree.GetText();
            foreach (var textLine in text.Lines)
            {
                var lineText = textLine.ToString();
                var match = AllowSleepMarker.Match(lineText);
                if (!match.Success)
                    continue;

                var reason = match.Groups[1].Value.Trim();
                if (reason.Length == 0)
                    reason = "(no reason given)";

                suppressions.Add(new Suppression(path, textLine.LineNumber + 1, reason));
            }
        }

        suppressions.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return suppressions;
    }
}
