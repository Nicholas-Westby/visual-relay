using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher enforcing that every direct invocation of
/// <c>GitCommitter.CommitAsync(</c> inside <c>tests/VisualRelay.Tests/</c>
/// passes a <c>timeProvider:</c> named argument. Omitting the argument lets
/// the call silently use <see cref="System.TimeProvider.System"/> and burn
/// real wall-clock time in tests — the slip that this guard prevents.
/// <para>Driver-level tests that reach commits through
/// <see cref="VisualRelay.Core.Execution.RelayDriverDependencies.ForTests"/>
/// are out of scope (that seam-wide migration is a separate effort); the guard
/// covers only direct <c>GitCommitter.CommitAsync(</c> calls.</para>
/// <para>Allowlist is empty. The guard is excluded from the <c>check</c> gate
/// and the standalone guards CLI subcommand list.</para>
/// </summary>
public static class TestClockInjectionGuard
{
    /// <summary>Describes a single missing-timeProvider violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    private static readonly string[] SelfExemptFileNames =
        ["TestClockInjectionGuard.cs", "TestClockInjectionGuardTests.cs"];

    private const string TestsDir = "tests/VisualRelay.Tests/";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    // ── (Path, Source) overload ────────────────────────────────────────────

    /// <summary>
    /// Returns every missing-timeProvider violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Only files whose path starts with
    /// <c>tests/VisualRelay.Tests/</c> are scanned.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(
        IEnumerable<(string Path, string Source)> files)
    {
        var violations = new List<Violation>();

        foreach (var (path, source) in files)
        {
            if (!ShouldScan(path))
                continue;

            var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path);
            ScanTree(path, tree, violations);
        }

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    // ── (Path, SyntaxTree) overload ────────────────────────────────────────

    /// <summary>
    /// Returns every missing-timeProvider violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Only trees whose path starts with
    /// <c>tests/VisualRelay.Tests/</c> are scanned. Uses pre-parsed
    /// <see cref="SyntaxTree"/> objects instead of re-parsing string sources.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(
        IEnumerable<(string RelativePath, SyntaxTree Tree)> trees)
    {
        var violations = new List<Violation>();

        foreach (var (path, tree) in trees)
        {
            if (!ShouldScan(path))
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

    // ── Internals ──────────────────────────────────────────────────────────

    private static bool ShouldScan(string path)
    {
        var fileName = Path.GetFileName(path);
        if (SelfExemptFileNames.Contains(fileName))
            return false;

        return path.StartsWith(TestsDir, StringComparison.Ordinal);
    }

    private static void ScanTree(string path, SyntaxTree tree, List<Violation> sink)
    {
        var text = tree.GetText();
        var root = tree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsGitCommitterCommitAsyncCall(invocation))
                continue;

            if (HasTimeProviderArgument(invocation))
                continue;

            var line = LineOf(text, invocation.SpanStart);
            sink.Add(new Violation(path, line, SnippetOf(text, line),
                "GitCommitter.CommitAsync( call missing timeProvider: named argument (inject ManualTimeProvider for tests)"));
        }
    }

    private static bool IsGitCommitterCommitAsyncCall(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.Text: "GitCommitter" },
            Name.Identifier.Text: "CommitAsync",
        };
    }

    private static bool HasTimeProviderArgument(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList is null)
            return true; // No argument list — can't be a real CommitAsync call

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.NameColon?.Name.Identifier.Text == "timeProvider")
                return true;
        }

        return false;
    }

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
