using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags real sleeps in C# sources — the static counterpart of
/// <see cref="ShellSizeGuard"/>. It Roslyn-parses each source and applies the
/// shell-sleep regexes ONLY to string-literal token text (regular, verbatim, raw
/// single/multi-line, interpolated text, and UTF-8 variants). Scoping the match to
/// literal tokens is the key design choice: comments and identifiers are trivia /
/// non-literal tokens, so doc-comment (<c>&lt;c&gt;sleep 30&lt;/c&gt;</c>) and
/// identifier (<c>SleepDuration</c>) false positives are impossible by construction.
///
/// Three rules:
/// <list type="number">
///   <item>shell <c>sleep N</c> (any duration, incl. <c>infinity</c>) inside a string literal;</item>
///   <item>the quoted-argv form <c>"sleep","30"</c> (e.g. <c>new ProcessStartInfo("sleep","30")</c>,
///         <c>ArgumentList = { "sleep", "30" }</c>) which spans two literal tokens;</item>
///   <item>C# <c>Thread.Sleep(...)</c> / <c>Task.Delay(...)</c> whose first argument statically
///         evaluates to a literal duration &gt;= 1000 ms with no real
///         <see cref="System.Threading.CancellationToken"/> (<c>.None</c> / <c>default</c> do not count).</item>
/// </list>
///
/// An inline <c>// vr-allow-sleep: &lt;reason&gt;</c> on the violation's line suppresses it; a bare
/// marker with no reason does not. The matcher self-exempts <c>RealSleepGuard.cs</c> and
/// <c>RealSleepGuardTests.cs</c> by filename because they carry sleep fixtures.
/// No I/O, no git — callers supply the (path, source) pairs.
/// </summary>
public static class RealSleepGuard
{
    /// <summary>Describes a single real-sleep violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Shell sleep of any duration inside one literal: <c>sleep 30</c>, <c>sleep 0.5</c>, <c>sleep infinity</c>.</summary>
    private static readonly Regex ShellSleepPattern =
        new(@"\bsleep\s+(\d+(\.\d+)?|infinity)\b", RegexOptions.Compiled);

    /// <summary>Quoted-argv form spanning two string tokens: <c>"sleep","30"</c> / <c>'sleep', '30'</c>.</summary>
    private static readonly Regex SleepArgvPattern =
        new("[\"']sleep[\"']\\s*,\\s*[\"']?\\d", RegexOptions.Compiled);

    /// <summary>A same-line suppression — only valid with a non-empty reason after the colon.</summary>
    private static readonly Regex AllowMarkerPattern =
        new(@"//\s*vr-allow-sleep:\s*\S", RegexOptions.Compiled);

    /// <summary>The C#-delay floor: shorter literal delays are polling, not "won't stop on its own".</summary>
    private const int CSharpDelayThresholdMs = 1000;

    /// <summary>Filenames whose own bodies legitimately contain sleep fixtures.</summary>
    private static readonly string[] SelfExemptFileNames = ["RealSleepGuard.cs", "RealSleepGuardTests.cs"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every real-sleep violation across <paramref name="files"/>, ordered by
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

        foreach (var token in root.DescendantTokens())
        {
            // Rule 1 — shell `sleep N` inside any string-literal token.
            if (IsStringContentToken(token))
            {
                foreach (Match m in ShellSleepPattern.Matches(token.Text))
                {
                    var line = LineOf(text, token.SpanStart + m.Index);
                    raw.Add(new Violation(path, line, SnippetOf(text, line),
                        "shell sleep embedded in a string literal"));
                }
            }

            // Rule 2 — argv form `"sleep","30"`. The regex spans two literal tokens and the
            // comma between them, so it cannot live inside a single token; anchor on the real
            // "sleep" string token (comments never produce string tokens) and run the regex on
            // the enclosing argument-list / initializer text.
            if (token.IsKind(SyntaxKind.StringLiteralToken) && token.ValueText == "sleep")
            {
                var argv = token.Parent?
                    .FirstAncestorOrSelf<SyntaxNode>(n => n is ArgumentListSyntax or InitializerExpressionSyntax);
                if (argv is not null && SleepArgvPattern.IsMatch(argv.ToString()))
                {
                    var line = LineOf(text, token.SpanStart);
                    raw.Add(new Violation(path, line, SnippetOf(text, line),
                        "shell sleep launched via \"sleep\",<duration> argv"));
                }
            }
        }

        // Rule 3 — long, uncancellable Thread.Sleep / Task.Delay.
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma)
                continue;

            var method = ma.Name.Identifier.Text;
            var owner = RightmostIdentifier(ma.Expression);
            var isThreadSleep = method == "Sleep" && owner == "Thread";
            var isTaskDelay = method == "Delay" && owner == "Task";
            if (!isThreadSleep && !isTaskDelay)
                continue;

            var args = inv.ArgumentList.Arguments;
            if (args.Count == 0)
                continue;

            var ms = EvaluateDurationMs(args[0].Expression);
            if (ms is null || ms.Value < CSharpDelayThresholdMs)
                continue;

            // Only Task.Delay has a CancellationToken overload; a *real* token (not .None /
            // default) means a regressed watchdog can still cut the wait short, so don't flag.
            if (isTaskDelay && args.Count >= 2 && IsRealCancellationToken(args[1].Expression))
                continue;

            var line = LineOf(text, ma.Name.Identifier.SpanStart);
            raw.Add(new Violation(path, line, SnippetOf(text, line),
                $"{owner}.{method}({Format(ms.Value)} ms) with no real CancellationToken"));
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

    private static bool IsStringContentToken(SyntaxToken token) => token.Kind() switch
    {
        SyntaxKind.StringLiteralToken => true,              // regular "..." and verbatim @"..."
        SyntaxKind.SingleLineRawStringLiteralToken => true, // """..."""
        SyntaxKind.MultiLineRawStringLiteralToken => true,  // multi-line """ ... """
        SyntaxKind.InterpolatedStringTextToken => true,     // text chunks of $"..." / $$"""..."""
        SyntaxKind.Utf8StringLiteralToken => true,          // "..."u8
        SyntaxKind.Utf8SingleLineRawStringLiteralToken => true,
        SyntaxKind.Utf8MultiLineRawStringLiteralToken => true,
        _ => false,
    };

    private static bool IsRealCancellationToken(ExpressionSyntax expr)
    {
        var compact = expr.ToString().Replace(" ", string.Empty);
        // .None and default / default(CancellationToken) are not real tokens.
        if (compact is "default")
            return false;
        if (compact.EndsWith("CancellationToken.None", StringComparison.Ordinal)
            || compact.EndsWith("default(CancellationToken)", StringComparison.Ordinal))
            return false;
        return true;
    }

    private static string RightmostIdentifier(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => string.Empty,
    };

    /// <summary>
    /// Statically evaluates a duration expression to milliseconds, or null when it is not a
    /// literal we can read (favouring false-negatives on the C# arm, per the design).
    /// Handles bare numeric literals (ms) and <c>TimeSpan.FromSeconds/FromMilliseconds(literal)</c>.
    /// </summary>
    private static double? EvaluateDurationMs(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax p:
                return EvaluateDurationMs(p.Expression);

            // ReSharper disable once MergeIntoPattern — IsKind is a method, cannot use property pattern
            case LiteralExpressionSyntax lit when lit.Token.IsKind(SyntaxKind.NumericLiteralToken):
                return AsDouble(lit.Token.Value);

            // ReSharper disable MergeIntoPattern — RightmostIdentifier needs m.Expression; IsKind is a method
            case InvocationExpressionSyntax i
                when i.Expression is MemberAccessExpressionSyntax m
                     && RightmostIdentifier(m.Expression) == "TimeSpan"
                     && i.ArgumentList.Arguments.Count == 1
                     && i.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax inner
                     && inner.Token.IsKind(SyntaxKind.NumericLiteralToken):
                // ReSharper restore MergeIntoPattern
                {
                    var n = AsDouble(inner.Token.Value);
                    if (n is null)
                        return null;
                    return m.Name.Identifier.Text switch
                    {
                        "FromMilliseconds" => n,
                        "FromSeconds" => n * 1000,
                        _ => null,
                    };
                }

            default:
                return null;
        }
    }

    private static double? AsDouble(object? value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        uint u => u,
        ulong ul => ul,
        _ => null,
    };

    private static string Format(double ms) =>
        // ReSharper disable once CompareOfFloatsByEqualityOperator — exact integer check on double is safe here (values from TimeSpan are exact)
        ms == Math.Floor(ms) ? ((long)ms).ToString(CultureInfo.InvariantCulture) : ms.ToString(CultureInfo.InvariantCulture);

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
