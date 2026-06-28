using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags sync-over-async deadlock patterns in C# test sources —
/// the static counterpart of <see cref="RealSleepGuard"/>. It Roslyn-parses each
/// source and flags blocking <c>.Result</c>, <c>.GetAwaiter().GetResult()</c>, or
/// <c>.Wait()</c> calls inside <c>[Fact]</c>/<c>[AvaloniaFact]</c>/<c>[Theory]</c>/
/// <c>[AvaloniaTheory]</c> test method bodies — the classic sync-over-async deadlock
/// on the single-threaded Avalonia headless dispatcher.
///
/// Scoping to test-method bodies is the key design choice: static helpers and
/// non-test methods are not scanned, so legitimate <c>.GetAwaiter().GetResult()</c>
/// uses in process-launch helpers (<c>WindowsLauncherTests.cs</c>) and static
/// record-writer helpers (<c>MainWindowViewModelTests.cs</c>,
/// <c>ObsidianSummaryWriterTests.cs</c>) are clean by construction.
///
/// An inline <c>// vr-allow-sync-over-async: &lt;reason&gt;</c> on the violation's
/// line suppresses it; a bare marker with no reason does not. The matcher
/// self-exempts <c>SyncOverAsyncGuard.cs</c>, <c>SyncOverAsyncGuardTests.cs</c>,
/// <c>RealSleepGuard.cs</c>, <c>RealSleepGuardTests.cs</c>, and
/// <c>WindowsLauncherTests.cs</c> by filename.
/// No I/O, no git — callers supply the (path, source) pairs.
/// </summary>
public static class SyncOverAsyncGuard
{
    /// <summary>Describes a single sync-over-async violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>A same-line suppression — only valid with a non-empty reason after the colon.</summary>
    private static readonly Regex AllowMarkerPattern =
        new(@"//\s*vr-allow-sync-over-async:\s*\S", RegexOptions.Compiled);

    /// <summary>Test attribute names that scope detection to test-method bodies.</summary>
    private static readonly HashSet<string> TestAttributeNames =
        new(StringComparer.Ordinal) { "Fact", "AvaloniaFact", "Theory", "AvaloniaTheory" };

    /// <summary>Filenames whose own bodies legitimately contain sync-over-async fixtures or
    /// legitimate non-test .GetAwaiter().GetResult() usage.</summary>
    private static readonly string[] SelfExemptFileNames =
        ["SyncOverAsyncGuard.cs", "SyncOverAsyncGuardTests.cs", "RealSleepGuard.cs", "RealSleepGuardTests.cs", "WindowsLauncherTests.cs"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every sync-over-async violation across <paramref name="files"/>, ordered by
    /// path (ordinal) then line. Self-exempt files yield nothing.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var violations = new List<Violation>();

        foreach (var (path, source) in files)
        {
            if (SelfExemptFileNames.Contains(Path.GetFileName(path)))
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

    private static void ScanSource(string path, string source, List<Violation> sink)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var text = tree.GetText();
        var root = tree.GetRoot();

        var raw = new List<Violation>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!HasTestAttribute(method))
                continue;

            if (method.Body is null && method.ExpressionBody is null)
                continue;

            // Walk descendant nodes inside the method body, looking for blocking patterns.
            // We scan from the method node so that the line number reflects the violation's
            // position within the file.
            foreach (var node in method.DescendantNodes())
            {
                switch (node)
                {
                    case MemberAccessExpressionSyntax ma
                        when ma.Name is IdentifierNameSyntax id
                             && id.Identifier.Text == "Result":
                        Report(ma.Name.SpanStart, ".Result");
                        break;

                    case InvocationExpressionSyntax inv
                        when inv.Expression is MemberAccessExpressionSyntax ma
                             && ma.Name is IdentifierNameSyntax id
                             && id.Identifier.Text == "Wait":
                        Report(id.SpanStart, ".Wait()");
                        break;

                    case InvocationExpressionSyntax inv
                        when inv.Expression is MemberAccessExpressionSyntax maOuter
                             && maOuter.Name is IdentifierNameSyntax idOuter
                             && idOuter.Identifier.Text == "GetResult"
                             && maOuter.Expression is InvocationExpressionSyntax invInner
                             && invInner.Expression is MemberAccessExpressionSyntax maInner
                             && maInner.Name is IdentifierNameSyntax idInner
                             && idInner.Identifier.Text == "GetAwaiter":
                        Report(idInner.SpanStart, ".GetAwaiter().GetResult()");
                        break;
                }
            }

            continue;

            void Report(int position, string pattern)
            {
                var line = LineOf(text, position);
                raw.Add(new Violation(path, line, SnippetOf(text, line),
                    $"sync-over-async: {pattern} in [Fact]/[AvaloniaFact] test method"));
            }
        }

        // Apply the inline allow-list, then de-duplicate per (line, reason).
        var seen = new HashSet<(int Line, string Reason)>();
        foreach (var v in raw)
        {
            if (AllowMarkerPattern.IsMatch(text.Lines[v.Line - 1].ToString()))
                continue;
            if (seen.Add((v.Line, v.Reason)))
                sink.Add(v);
        }
    }

    /// <summary>
    /// True when <paramref name="method"/> carries a [Fact], [AvaloniaFact], [Theory],
    /// or [AvaloniaTheory] attribute.
    /// </summary>
    private static bool HasTestAttribute(MethodDeclarationSyntax method)
    {
        foreach (var list in method.AttributeLists)
        {
            foreach (var attr in list.Attributes)
            {
                var name = LastIdentifier(attr.Name);
                if (name is not null && TestAttributeNames.Contains(name))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the rightmost identifier text from an attribute name syntax, e.g.
    /// "Fact" from "Xunit.Fact" or "AvaloniaFact" from a simple name.
    /// </summary>
    private static string? LastIdentifier(NameSyntax name) => name switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax q => LastIdentifier(q.Right),
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
