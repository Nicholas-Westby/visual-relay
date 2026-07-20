using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags real-git fallbacks inside the default test suite
/// (<c>tests/VisualRelay.Tests/</c>) — the two patterns that spawn a real
/// <c>git</c> process and must not appear in the default (non-opt-in) test run:
/// <list type="number">
///   <item><c>new GitInvoker(</c> object creation — a real process launcher
///         that shells out to the git binary;</item>
///   <item><c>new ProcessStartInfo("git"</c> (or <c>ProcessStartInfo VariableName</c>
///         whose value is <c>"git"</c>) — constructing process-launch info whose file
///         name resolves to git.</item>
/// </list>
/// <para>The only exemptions are the opt-in parity suite and the guard-test files
/// themselves (which exercise synthetic inline sources, never a live git process).
/// After the migration is complete the live-tree test must assert zero violations
/// in every non-exempt file under <c>tests/VisualRelay.Tests/</c>.</para>
/// </summary>
public static class TestRealGitGuard
{
    /// <summary>Describes a single real-git violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>
    /// Files that are allowed to contain <c>new GitInvoker(</c> or
    /// <c>ProcessStartInfo("git")</c> because they are (a) the opt-in parity
    /// suite, (b) guard-as-test files that use synthetic inline sources, or
    /// (c) the guard matcher and its own test file.
    /// </summary>
    private static readonly HashSet<string> ExemptFileNames = new(StringComparer.Ordinal)
    {
        // Opt-in parity suite — the ONLY files where real git is legitimate.
        "RealGitIntegrationTests.cs",
        "RealGitIntegrationDriverTests.cs",
        "ParityHarness.cs",
        // Guard-as-test files that exercise synthetic inline source strings.
        "DiBypassGuardTests.cs",
        "RealGitFallbackGuardTests.cs",
        "RealBuildSubprocessGuardTests.cs",
        "TestSideEffectsGuardTests.cs",
        "GateAsTestSandboxGuardTests.cs",
        "RealSleepGuardTests.cs",
        // Tests for the GitInvoker class itself (not integration tests).
        "GitInvokerTests.cs",
        "GitInvokerProbeCacheTests.cs",
        // Windows-only GitInvoker resolution test.
        "WindowsExecutionTests.cs",
        // GitBootstrapper real-git head probe.
        "GitBootstrapperTests.cs",
        // Pre-commit hook tests that exercise the real bash hook file.
        "PreCommitHookTests.cs",
        "PreCommitHookIdentityStripTests.cs",
        // Gated integration helpers and runners (VR_RUN_SLOW_INTEGRATION=1).
        "RelayDriverGitCommitTestHelpers.cs",
        "RelayDriverGitCommitTests.cs",
        "RelayDriverGitCommitSelfCommitSquashTests.cs",
        "CommitTestRunners.cs",
        "CommitTestRunners.SelfCommit.cs",
        "VerifyWorktreeDeletionOverlayTests.Symlink.cs",
        // Self-exempt (the guard and its own test).
        "TestRealGitGuard.cs",
        "TestRealGitGuardTests.cs",
    };

    private const string TestDir = "tests/VisualRelay.Tests/";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    // ── (Path, Source) overload ────────────────────────────────────────────

    /// <summary>
    /// Returns every real-git violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Only files under
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
    /// Returns every real-git violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Uses pre-parsed
    /// <see cref="SyntaxTree"/> objects. Only trees under
    /// <c>tests/VisualRelay.Tests/</c> are scanned.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(
        IEnumerable<(string Path, SyntaxTree Tree)> trees)
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
        if (ExemptFileNames.Contains(fileName))
            return false;

        return path.StartsWith(TestDir, StringComparison.Ordinal);
    }

    private static void ScanTree(string path, SyntaxTree tree, List<Violation> sink)
    {
        var text = tree.GetText();
        var root = tree.GetRoot();

        // Rule 1 — new GitInvoker( object creation.
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (IsGitInvokerType(creation.Type))
            {
                var line = LineOf(text, creation.SpanStart);
                sink.Add(new Violation(path, line, SnippetOf(text, line),
                    "new GitInvoker() in test source — inject GitSim (in-memory IGitInvoker) instead"));
            }
        }

        // Rule 2 — ProcessStartInfo whose file name is "git".
        foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!IsProcessStartInfoType(creation.Type))
                continue;

            // Check the first argument: "git" literal or a variable that resolves to "git".
            var firstArg = creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (firstArg is LiteralExpressionSyntax lit
                && lit.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression
                && lit.Token.ValueText == "git")
            {
                var line = LineOf(text, creation.SpanStart);
                sink.Add(new Violation(path, line, SnippetOf(text, line),
                    "ProcessStartInfo(\"git\") in test source — inject GitSim (in-memory IGitInvoker) instead"));
            }
        }
    }

    private static bool IsGitInvokerType(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == "GitInvoker",
        QualifiedNameSyntax qn => qn.Right.Identifier.Text == "GitInvoker",
        AliasQualifiedNameSyntax aq => aq.Name.Identifier.Text == "GitInvoker",
        _ => false,
    };

    private static bool IsProcessStartInfoType(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == "ProcessStartInfo",
        QualifiedNameSyntax qn => qn.Right.Identifier.Text == "ProcessStartInfo",
        _ => false,
    };

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
