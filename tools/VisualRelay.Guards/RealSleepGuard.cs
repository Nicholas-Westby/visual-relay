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
///   <item>every C# <c>Thread.Sleep(...)</c> (it has no virtual-clock overload) and every
///         <c>Task.Delay(...)</c> that lacks a <see cref="System.TimeProvider"/> argument —
///         ANY duration, cancellable or not. A real <see cref="System.Threading.CancellationToken"/>
///         no longer exempts a delay: only a TimeProvider (the virtual-clock seam) does. The
///         3-arg <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> and the 2-arg
///         <c>Task.Delay(TimeSpan, TimeProvider)</c> forms are the sanctioned virtual delays.</item>
/// </list>
///
/// An inline <c>// vr-allow-sleep: &lt;reason&gt;</c> on the violation's line suppresses it; a bare
/// marker with no reason does not. The matcher self-exempts by filename the guard's own fixture
/// carriers (<c>RealSleepGuard.cs</c>, <c>RealSleepGuardTests.cs</c>) and the opt-in
/// slow-integration files (<see cref="RealIntegrationExemptFileNames"/>) whose real waits run
/// only behind <c>SlowIntegration.SkipIfNotOptedIn()</c>. No I/O, no git — callers supply the
/// (path, source) pairs.
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

    /// <summary>Filenames whose own bodies legitimately contain sleep fixtures.</summary>
    private static readonly string[] SelfExemptFileNames = ["RealSleepGuard.cs", "RealSleepGuardTests.cs"];

    /// <summary>
    /// Opt-in slow-integration files: their real processes and real wall-clock windows
    /// (kill escalation, setpgid reap, SIGINT trap, socket wedge) run only behind
    /// <c>SlowIntegration.SkipIfNotOptedIn()</c>, so a genuine wait there is legitimate.
    /// Each has an always-on virtual-clock sibling asserting the same decision logic.
    /// </summary>
    private static readonly string[] RealIntegrationExemptFileNames =
    [
        "ProcessCaptureGracefulStopTests.cs",
        "SandboxedTestRunnerReapTests.cs",
        "ActivityWatchdogSocketWedgeTests.cs",
        // Real detached-child reaping and a Windows heartbeat-file window: genuine OS
        // effects with no virtualizable signal, gated / OS-guarded, real settle needed.
        "FdLeakTests.cs",
        "WindowsExecutionTests.cs",
    ];

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
            var fileName = Path.GetFileName(path);
            if (SelfExemptFileNames.Contains(fileName) || RealIntegrationExemptFileNames.Contains(fileName))
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
    /// Returns every real-sleep violation across <paramref name="trees"/>, ordered by
    /// path (ordinal) then line. Self-exempt files yield nothing. Uses pre-parsed
    /// <see cref="SyntaxTree"/> objects instead of re-parsing string sources.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, SyntaxTree Tree)> trees)
    {
        var violations = new List<Violation>();

        foreach (var (path, tree) in trees)
        {
            var fileName = Path.GetFileName(path);
            if (SelfExemptFileNames.Contains(fileName) || RealIntegrationExemptFileNames.Contains(fileName))
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

    private static void ScanSource(string path, string source, List<Violation> sink)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        ScanTree(path, tree, sink);
    }

    private static void ScanTree(string path, SyntaxTree tree, List<Violation> sink)
    {
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

        // Rule 3 — every Thread.Sleep, and every Task.Delay lacking a TimeProvider.
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

            // Task.Delay driven by a TimeProvider (the virtual-clock seam) is exempt —
            // any duration, cancellable or not. Thread.Sleep has no such overload, so it
            // is ALWAYS a real sleep. A real CancellationToken no longer exempts a delay:
            // only a TimeProvider does (that is the loophole this rule closes).
            if (isTaskDelay && HasTimeProviderArgument(args))
                continue;

            var reason = isThreadSleep
                ? "Thread.Sleep is a real wall-clock sleep (no TimeProvider overload)"
                : "Task.Delay with no TimeProvider argument (drive it with a virtual clock)";
            var line = LineOf(text, ma.Name.Identifier.SpanStart);
            raw.Add(new Violation(path, line, SnippetOf(text, line), reason));
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

    /// <summary>
    /// True when a <c>Task.Delay</c> argument list carries a <see cref="System.TimeProvider"/>.
    /// The only 3-argument overload is <c>(TimeSpan, TimeProvider, CancellationToken)</c>, so any
    /// call with three arguments carries one; a 2-argument call carries one only when its second
    /// argument is recognisably a TimeProvider (the ambiguous <c>(TimeSpan, TimeProvider)</c> vs
    /// <c>(TimeSpan, CancellationToken)</c> case), matched by shape/name.
    /// </summary>
    private static bool HasTimeProviderArgument(SeparatedSyntaxList<ArgumentSyntax> args)
    {
        if (args.Count >= 3)
            return true;
        return args.Count == 2 && IsTimeProviderExpression(args[1].Expression);
    }

    /// <summary>Recognises a TimeProvider second argument: a <c>TimeProvider.X</c> member access,
    /// or an identifier named for a clock (<c>timeProvider</c>, <c>_timeProvider</c>, <c>tp</c>,
    /// <c>time</c>, <c>clock</c>). CancellationToken arguments never match these.</summary>
    private static bool IsTimeProviderExpression(ExpressionSyntax expr)
    {
        if (expr is MemberAccessExpressionSyntax ma && RightmostIdentifier(ma.Expression) == "TimeProvider")
            return true;
        var name = expr switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => string.Empty,
        };
        var lower = name.ToLowerInvariant();
        return lower.Contains("timeprovider", StringComparison.Ordinal)
            || lower is "tp" or "time" or "clock" or "_time" or "_clock" or "_timeprovider" or "_tp";
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
