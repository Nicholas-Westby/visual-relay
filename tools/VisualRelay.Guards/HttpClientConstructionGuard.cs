using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher enforcing that outbound HTTP has exactly one composition root per
/// assembly. Everything above the network takes the
/// <c>VisualRelay.Core.Llm.IProviderTransport</c> seam; only the allowlisted files
/// below may construct a client, an invoker or a real handler, so the fast suite
/// can install a connect-refusing <c>SocketsHttpHandler</c> in one place and know
/// nothing routes around it.
/// <para>
/// Four type names are banned. <c>HttpClient</c> and <c>HttpMessageInvoker</c> are
/// the only two types that can dispatch a request, and banning the client alone
/// would leave <c>new HttpMessageInvoker(handler).SendAsync(...)</c> as an open
/// door. <c>HttpClientHandler</c> and <c>SocketsHttpHandler</c> are banned as well
/// because a handler is what carries <c>ConnectCallback</c>: an un-gated handler
/// handed to the allowlisted transport is exactly how a test would slip past the
/// hermetic gate. A hand-written <see cref="System.Net.Http.HttpMessageHandler"/>
/// subclass (a test double that answers from memory) is deliberately NOT flagged —
/// it opens no socket, and subclassing is not visible to a syntax-only matcher.
/// </para>
/// <para>Only <c>src/</c>, <c>tools/</c> and <c>tests/</c> are scanned. Both
/// explicit <c>new HttpClient(...)</c> and target-typed <c>new()</c> in a field,
/// local or property declaration are detected; a target-typed <c>new()</c> in a
/// position whose type is not syntactically recoverable (a return statement, an
/// argument) is out of reach of a matcher with no semantic model.</para>
/// <para>The guard is a Tier-2 guard-as-test only: it is not wired into
/// <c>./visual-relay check</c> or the guards CLI subcommand list.</para>
/// </summary>
public static class HttpClientConstructionGuard
{
    /// <summary>Describes a single banned-construction violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Filenames whose own bodies legitimately contain the banned constructions.</summary>
    private static readonly string[] SelfExemptFileNames =
        ["HttpClientConstructionGuard.cs", "HttpClientConstructionGuardTests.cs"];

    /// <summary>Repo-relative roots this guard scans; anything else is ignored.</summary>
    private static readonly string[] ScannedRoots = ["src/", "tools/", "tests/"];

    /// <summary>Type names whose construction is banned outside the allowlist.</summary>
    private static readonly string[] BannedTypeNames =
        ["HttpClient", "HttpMessageInvoker", "HttpClientHandler", "SocketsHttpHandler"];

    /// <summary>
    /// The allowlist, keyed by bare filename, each entry carrying the reason it is
    /// allowed. Two entries are permanent composition roots; one is a dated,
    /// explicitly temporary carry-over that pre-dates the transport seam. Widening
    /// this map without a reason string is the thing the guard exists to prevent.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedFileNames = new(StringComparer.Ordinal)
    {
        // ── Permanent composition roots ────────────────────────────────────
        ["LiveProviderTransport.cs"] =
            "the one production implementation that opens a socket; its handler is constructor-injected",
        ["HermeticHttpHandler.cs"] =
            "the test assembly's one HTTP composition root; every handler it builds refuses a non-loopback connect",

        // ── Dated temporary carry-over (pre-dates the IProviderTransport seam) ──
        // 2026-08-31: one-off installer download of the mxc archive. Not an LLM
        // call, already gated behind an injectable download delegate in tests.
        ["MxcInstaller.cs"] = "TEMPORARY 2026-08-31 — one-off installer download, injectable delegate in tests",
    };

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    // ── (Path, Source) overload ────────────────────────────────────────────

    /// <summary>
    /// Returns every banned-construction violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Self-exempt and allowlisted files, and
    /// paths outside <c>src/</c>, <c>tools/</c> and <c>tests/</c>, yield nothing.
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

        return Sorted(violations);
    }

    // ── (Path, SyntaxTree) overload ────────────────────────────────────────

    /// <summary>
    /// Returns every banned-construction violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Uses pre-parsed
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

        return Sorted(violations);
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private static IReadOnlyList<Violation> Sorted(List<Violation> violations)
    {
        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    private static bool ShouldScan(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        if (SelfExemptFileNames.Contains(fileName) || AllowedFileNames.ContainsKey(fileName))
            return false;

        return ScannedRoots.Any(root => normalized.StartsWith(root, StringComparison.Ordinal));
    }

    private static void ScanTree(string path, SyntaxTree tree, List<Violation> sink)
    {
        var text = tree.GetText();
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            var typeName = node switch
            {
                ObjectCreationExpressionSyntax oc => RightmostIdentifier(oc.Type),
                ImplicitObjectCreationExpressionSyntax ioc => TargetTypeNameOf(ioc),
                _ => null,
            };

            if (typeName is null || !BannedTypeNames.Contains(typeName))
                continue;

            var line = LineOf(text, node.SpanStart);
            sink.Add(new Violation(path, line, SnippetOf(text, line),
                $"constructs '{typeName}' outside the allowlisted composition roots "
                + "(take IProviderTransport instead; the only allowed roots are "
                + "LiveProviderTransport.cs in src and HermeticHttpHandler.cs in tests)"));
        }
    }

    /// <summary>
    /// Recovers the target type of a <c>new()</c> expression from the surrounding
    /// declaration. Returns <c>null</c> when the target type is not syntactically
    /// recoverable, which a matcher without a semantic model cannot resolve.
    /// </summary>
    private static string? TargetTypeNameOf(ImplicitObjectCreationExpressionSyntax node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case EqualsValueClauseSyntax:
                case VariableDeclaratorSyntax:
                    continue;
                case VariableDeclarationSyntax declaration:
                    return RightmostIdentifier(declaration.Type);
                case PropertyDeclarationSyntax property:
                    return RightmostIdentifier(property.Type);
                default:
                    return null;
            }
        }

        return null;
    }

    private static string? RightmostIdentifier(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qn => RightmostIdentifier(qn.Right),
        AliasQualifiedNameSyntax aq => RightmostIdentifier(aq.Name),
        NullableTypeSyntax nt => RightmostIdentifier(nt.ElementType),
        _ => null,
    };

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
