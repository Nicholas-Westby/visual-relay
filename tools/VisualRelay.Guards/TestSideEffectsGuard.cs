using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags test sources referencing real-side-effect constructs.
/// Four patterns are detected:
///
/// <list type="number">
///   <item><c>new GitInvoker(</c> — a real <c>GitInvoker</c> allocation in test
///         code; suggest <c>NullGitInvoker</c> (returns exit-128) or
///         <c>GitSim</c> (in-memory <c>IGitInvoker</c>).</item>
///   <item><c>Process.Start</c> — a real process launch in a test; suggest
///         <c>ScriptedTestRunner</c> or an in-process equivalent.</item>
///   <item><c>new ProcessStartInfo(</c> — configuring a real process; suggest
///         <c>ScriptedTestRunner</c>.</item>
///   <item><c>Environment.SetEnvironmentVariable</c> — mutating the OS environment
///         from a test; suggest <c>DictionaryEnvironmentAccessor</c>.</item>
/// </list>
///
/// <para>Only flags files whose path contains <c>/tests/</c> (non-test sources are
/// not reported). Honors <see cref="RealSleepGuard"/>'s slow-integration exemption
/// list (<see cref="RealIntegrationExemptFileNames"/>). Self-exempts the matcher's
/// own fixture carriers. No I/O, no git — callers supply the (path, source) pairs.</para>
/// </summary>
public static class TestSideEffectsGuard
{
    /// <summary>Describes a test-side-effect violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    private static readonly string[] SelfExemptFileNames =
        ["TestSideEffectsGuard.cs", "TestSideEffectsGuardTests.cs"];

    /// <summary>Slow-integration files whose real side-effects are legitimate
    /// (only run behind <c>SlowIntegration.SkipIfNotOptedIn()</c>).</summary>
    private static readonly string[] RealIntegrationExemptFileNames =
    [
        "ProcessCaptureGracefulStopTests.cs",
        "SandboxedTestRunnerReapTests.cs",
        "ActivityWatchdogSocketWedgeTests.cs",
        "FdLeakTests.cs",
        "WindowsExecutionTests.cs",
    ];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every test-side-effect violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Only flags files whose path contains
    /// <c>/tests/</c>.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var violations = new List<Violation>();

        foreach (var (path, source) in files)
        {
            var fileName = Path.GetFileName(path);
            if (SelfExemptFileNames.Contains(fileName) || RealIntegrationExemptFileNames.Contains(fileName))
                continue;

            if (!path.StartsWith("tests/", StringComparison.Ordinal)
                && !path.Contains("/tests/", StringComparison.Ordinal))
                continue;

            var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
            ScanTree(path, tree, violations);
        }

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    /// <summary>
    /// Returns every test-side-effect violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Uses pre-parsed <see cref="SyntaxTree"/>
    /// objects.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, SyntaxTree Tree)> trees)
    {
        var violations = new List<Violation>();

        foreach (var (path, tree) in trees)
        {
            var fileName = Path.GetFileName(path);
            if (SelfExemptFileNames.Contains(fileName) || RealIntegrationExemptFileNames.Contains(fileName))
                continue;

            if (!path.StartsWith("tests/", StringComparison.Ordinal)
                && !path.Contains("/tests/", StringComparison.Ordinal))
                continue;

            ScanTree(path, tree, violations);
        }

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    private static void ScanTree(string path, SyntaxTree tree, List<Violation> sink)
    {
        var text = tree.GetText();
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                // new GitInvoker()
                case ObjectCreationExpressionSyntax creation
                    when creation.Type is IdentifierNameSyntax id
                         && id.Identifier.Text == "GitInvoker":
                    Report(creation.SpanStart, "new GitInvoker()",
                        "prefer NullGitInvoker (returns exit-128) or GitSim (in-memory IGitInvoker)");
                    break;

                // Process.Start(...)
                case InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax ma
                         && RightmostIdentifier(ma.Expression) == "Process"
                         && ma.Name.Identifier.Text == "Start":
                    Report(inv.SpanStart, "Process.Start(...)",
                        "prefer ScriptedTestRunner or an in-process equivalent");
                    break;

                // new ProcessStartInfo(...)
                case ObjectCreationExpressionSyntax creation
                    when creation.Type is IdentifierNameSyntax psiId
                         && psiId.Identifier.Text == "ProcessStartInfo":
                    Report(creation.SpanStart, "new ProcessStartInfo(...)",
                        "prefer ScriptedTestRunner or an in-process equivalent");
                    break;

                // Environment.SetEnvironmentVariable
                case InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax envMa
                         && RightmostIdentifier(envMa.Expression) == "Environment"
                         && envMa.Name.Identifier.Text == "SetEnvironmentVariable":
                    Report(inv.SpanStart, "Environment.SetEnvironmentVariable(...)",
                        "prefer DictionaryEnvironmentAccessor (in-memory IEnvironmentAccessor)");
                    break;
            }

            continue;

            void Report(int position, string pattern, string direction)
            {
                var line = LineOf(text, position);
                sink.Add(new Violation(path, line, SnippetOf(text, line),
                    $"test-side-effects: {pattern} in test source — {direction}"));
            }
        }

        // De-duplicate per (line, reason).
        var seen = new HashSet<(int Line, string Reason)>();
        var deduped = new List<Violation>();
        foreach (var v in sink.OrderBy(v => v.Line))
        {
            if (seen.Add((v.Line, v.Reason)))
                deduped.Add(v);
        }

        sink.Clear();
        sink.AddRange(deduped);
    }

    private static string RightmostIdentifier(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => string.Empty,
    };

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
