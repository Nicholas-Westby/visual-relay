using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher enforcing that fake-clock identifiers never appear in production
/// source trees (<c>src/</c> and <c>tools/</c>), every <c>TimeProvider?</c>-typed
/// parameter defaults to <c>null</c> (bodies resolve via <c>?? TimeProvider.System</c>),
/// and no <c>src/</c> csproj references a time-testing package. Fake clocks are a
/// tests-only seam — production must default to real time.
/// </summary>
public static class FakeClockGuard
{
    /// <summary>Describes a single fake-clock violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Filenames whose own bodies legitimately contain fake-clock fixtures.</summary>
    private static readonly string[] SelfExemptFileNames = ["FakeClockGuard.cs", "FakeClockGuardTests.cs"];

    /// <summary>Package names whose presence in a src csproj is a violation.</summary>
    private static readonly string[] TimeTestingPackagePatterns = ["Microsoft.Extensions.Time.Testing"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every fake-clock violation across <paramref name="files"/>, ordered by
    /// path (ordinal) then line. Self-exempt files yield nothing.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var violations = new List<Violation>();

        foreach (var (path, source) in files)
        {
            var fileName = Path.GetFileName(path);
            if (SelfExemptFileNames.Contains(fileName))
                continue;

            ScanSource(path, source, violations);
        }

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    /// <summary>
    /// Returns every fake-clock violation across <paramref name="trees"/>, ordered by
    /// path (ordinal) then line. Self-exempt files yield nothing. Uses pre-parsed
    /// <see cref="SyntaxTree"/> objects instead of re-parsing string sources.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string RelativePath, SyntaxTree Tree)> trees)
    {
        var violations = new List<Violation>();

        foreach (var (path, tree) in trees)
        {
            var fileName = Path.GetFileName(path);
            if (SelfExemptFileNames.Contains(fileName))
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

    /// <summary>
    /// Scans <c>.csproj</c> XML content for <c>PackageReference</c> elements that
    /// reference a time-testing package. Returns violations ordered by path.
    /// </summary>
    public static IReadOnlyList<Violation> FindCsprojViolations(
        IEnumerable<(string Path, string XmlContent)> csprojs)
    {
        var violations = new List<Violation>();

        foreach (var (path, xml) in csprojs)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                foreach (var pr in doc.Descendants(ns + "PackageReference"))
                {
                    var include = pr.Attribute("Include")?.Value;
                    if (include is null)
                        continue;

                    foreach (var pattern in TimeTestingPackagePatterns)
                    {
                        if (include.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add(new Violation(path, 0,
                                $"PackageReference Include=\"{include}\"",
                                $"csproj references time-testing package '{include}' (time-testing packages are a tests-only dependency)"));
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Malformed XML — not our job to flag; skip.
            }
        }

        violations.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return violations;
    }

    private static void ScanSource(string path, string source, List<Violation> sink)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        ScanTree(path, tree, sink);
    }

    private static void ScanTree(string path, SyntaxTree tree, List<Violation> sink)
    {
        var text = tree.GetText();
        var root = tree.GetRoot();

        // Rule 1 — fake-clock identifiers (ManualTimeProvider, FakeTimeProvider)
        // in type references, object creations, parameter types, etc.
        foreach (var ident in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var name = ident.Identifier.Text;
            if (name is "ManualTimeProvider" or "FakeTimeProvider")
            {
                var line = LineOf(text, ident.Identifier.SpanStart);
                sink.Add(new Violation(path, line, SnippetOf(text, line),
                    $"fake-clock identifier '{name}' in production source (fake clocks are a tests-only seam)"));
            }
        }

        // Rule 2 — TimeProvider? parameters with non-null defaults. Every
        // TimeProvider?-typed parameter must default to null so callers that
        // omit it get real time; bodies resolve via ?? TimeProvider.System.
        foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (param.Type is NullableTypeSyntax nts
                && RightmostIdentifier(nts.ElementType) == "TimeProvider"
                && param.Default is not null)
            {
                if (param.Default.Value is not LiteralExpressionSyntax lit
                    || !lit.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    var line = LineOf(text, param.Identifier.SpanStart);
                    sink.Add(new Violation(path, line, SnippetOf(text, line),
                        "TimeProvider? parameter has a non-null default (must default to null; bodies resolve via ?? TimeProvider.System)"));
                }
            }
        }
    }

    private static string RightmostIdentifier(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qn => RightmostIdentifier(qn.Right),
        AliasQualifiedNameSyntax aq => RightmostIdentifier(aq.Name),
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
