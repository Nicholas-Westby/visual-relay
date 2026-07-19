using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags real-git-invoker fallbacks inside
/// <c>src/VisualRelay.Core/Execution</c> — the two patterns that let a caller
/// omit <see cref="VisualRelay.Core.Execution.IGitInvoker"/> and silently spawn a
/// real <c>git</c> process:
/// <list type="number">
///   <item><c>new GitInvoker(</c> object creation — a private real process launcher
///         inside a layer that must receive the invoker through its injection seam;</item>
///   <item>an <c>IGitInvoker</c> (or <c>IGitInvoker?</c>) parameter that declares a
///         default value — the <c>= null</c> that enables call-site omission and
///         triggers the runtime <c>?? new GitInvoker()</c> coalesce.</item>
/// </list>
/// <para>Allowlist is empty — after the fix there must be zero of either pattern
/// under the execution layer. The guard stays a guard-as-test; it is excluded from
/// the <c>check</c> gate and the standalone guards CLI subcommand list. Files
/// outside <c>src/VisualRelay.Core/Execution</c> (in particular <c>Init</c>,
/// the App, and the CLI) are not scanned.</para>
/// </summary>
public static class RealGitFallbackGuard
{
    /// <summary>Describes a single real-git-fallback violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    private static readonly string[] SelfExemptFileNames =
        ["RealGitFallbackGuard.cs", "RealGitFallbackGuardTests.cs"];

    private const string ExecutionDir = "src/VisualRelay.Core/Execution/";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    // ── (Path, Source) overload ────────────────────────────────────────────

    /// <summary>
    /// Returns every real-git-fallback violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Only files whose path starts with
    /// <c>src/VisualRelay.Core/Execution/</c> are scanned.
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
    /// Returns every real-git-fallback violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Only trees whose path starts with
    /// <c>src/VisualRelay.Core/Execution/</c> are scanned. Uses pre-parsed
    /// <see cref="SyntaxTree"/> objects instead of re-parsing string sources.
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
        if (SelfExemptFileNames.Contains(fileName))
            return false;

        return path.StartsWith(ExecutionDir, StringComparison.Ordinal);
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
                    "new GitInvoker() object creation inside the execution layer (inject IGitInvoker instead)"));
            }
        }

        // Rule 2 — IGitInvoker / IGitInvoker? parameter with a default value.
        foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (param.Default is not null && IsIGitInvokerType(param.Type))
            {
                var line = LineOf(text, param.SpanStart);
                sink.Add(new Violation(path, line, SnippetOf(text, line),
                    "IGitInvoker parameter with a default value (make it required, non-optional)"));
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

    private static bool IsIGitInvokerType(TypeSyntax? type) => type switch
    {
        null => false,
        IdentifierNameSyntax id => id.Identifier.Text is "IGitInvoker",
        NullableTypeSyntax n => n.ElementType is IdentifierNameSyntax id && id.Identifier.Text is "IGitInvoker",
        QualifiedNameSyntax qn => qn.Right.Identifier.Text is "IGitInvoker",
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
