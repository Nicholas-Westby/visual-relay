using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags sync-over-async deadlock patterns in C# test sources —
/// the static counterpart of <see cref="RealSleepGuard"/>. It Roslyn-parses each
/// source and flags blocking <c>.Result</c>, <c>.GetAwaiter().GetResult()</c>,
/// <c>.Wait()</c>, and <c>Task.WaitAll/WaitAny(...)</c> calls inside UI-thread
/// test method bodies (<c>[Fact]</c>/<c>[AvaloniaFact]</c>/<c>[StaFact]</c>/
/// <c>[WpfFact]</c>/<c>[UIFact]</c>/<c>[WinFormsFact]</c> and their Theory variants)
/// — the classic sync-over-async deadlock on a single-threaded dispatcher.
///
/// Two design choices keep the guard free of false positives (a false positive
/// breaks the build the moment legit code lands):
///   1. Scope to test-method bodies — static helpers and non-test methods are not
///      scanned, so legitimate <c>.GetAwaiter().GetResult()</c> in process-launch
///      helpers is clean by construction.
///   2. Require a plausibly-<c>Task</c> RECEIVER for <c>.Result</c>/<c>.Wait()</c>
///      (see <see cref="IsTaskishReceiver"/>) — judged purely syntactically (no
///      semantic model). This is why <c>record.Result</c>, <c>SemaphoreSlim.Wait()</c>,
///      <c>Match.Result(...)</c>, and <c>Task.Run(() =&gt; x.Result)</c> are NOT
///      flagged, while <c>SomethingAsync().Result</c> / <c>.Wait()</c> ARE. The
///      <c>.GetAwaiter().GetResult()</c> chain is kept broad (the chain itself is
///      the signal).
///
/// An inline <c>// vr-allow-sync-over-async: &lt;reason&gt;</c> on the violation's
/// line suppresses it; a bare marker with no reason does not. The matcher
/// self-exempts only its own fixture files (<c>SyncOverAsyncGuard.cs</c>,
/// <c>SyncOverAsyncGuardTests.cs</c>) — no product/test file is exempted by name,
/// so the guard has no blind spots. No I/O, no git — callers supply the (path,
/// source) pairs.
/// </summary>
public static class SyncOverAsyncGuard
{
    /// <summary>Describes a single sync-over-async violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>A same-line suppression — only valid with a non-empty reason after the colon.</summary>
    private static readonly Regex AllowMarkerPattern =
        new(@"//\s*vr-allow-sync-over-async:\s*\S", RegexOptions.Compiled);

    /// <summary>Test attribute names that scope detection to UI-thread test-method
    /// bodies. Covers xunit (<c>Fact</c>/<c>Theory</c>), Avalonia headless, and the
    /// Xunit.StaFact family (<c>StaFact</c>/<c>WpfFact</c>/<c>UIFact</c>/
    /// <c>WinFormsFact</c> and their Theory variants) — all run on a single-threaded
    /// dispatcher where sync-over-async deadlocks.</summary>
    private static readonly HashSet<string> TestAttributeNames =
        new(StringComparer.Ordinal)
        {
            "Fact", "Theory", "AvaloniaFact", "AvaloniaTheory",
            "StaFact", "StaTheory", "WpfFact", "WpfTheory",
            "UIFact", "UITheory", "WinFormsFact", "WinFormsTheory",
        };

    /// <summary>The matcher's OWN fixture files only (this matcher + its tests carry
    /// sync-over-async patterns as string fixtures). Kept minimal and general — no
    /// product/test file is exempted by name, so the guard has no blind spots.</summary>
    private static readonly string[] SelfExemptFileNames =
        ["SyncOverAsyncGuard.cs", "SyncOverAsyncGuardTests.cs"];

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

            // Walk descendant nodes inside the method body, looking for blocking
            // patterns ON A PLAUSIBLY-TASK RECEIVER. The receiver gate (see
            // IsTaskishReceiver) is what keeps legit non-Task members — record.Result,
            // SemaphoreSlim.Wait(), Match.Result(...) — from tripping the guard.
            // Blocking inside an offloaded Task.Run/StartNew lambda runs on a pool
            // thread (no UI-dispatcher deadlock) and is excluded too. We scan from the
            // method node so the line number reflects the violation's position in file.
            foreach (var node in method.DescendantNodes())
            {
                if (IsInsideOffloadedLambda(node))
                    continue;

                switch (node)
                {
                    // `<task>.Result` PROPERTY read (not an `x.Result(...)` call).
                    case MemberAccessExpressionSyntax ma
                        when ma.Name.Identifier.Text == "Result"
                             && !IsInvokedMember(ma)
                             && IsTaskishReceiver(ma.Expression):
                        Report(ma.Name.SpanStart, ".Result");
                        break;

                    // `<task>.Wait()` / `<task>.Wait(timeout)`.
                    case InvocationExpressionSyntax wInv
                        when wInv.Expression is MemberAccessExpressionSyntax wm
                             && wm.Name.Identifier.Text == "Wait"
                             && IsTaskishReceiver(wm.Expression):
                        Report(wm.Name.SpanStart, ".Wait()");
                        break;

                    // Static `Task.WaitAll(...)` / `Task.WaitAny(...)` blocking joins.
                    case InvocationExpressionSyntax sInv
                        when sInv.Expression is MemberAccessExpressionSyntax sm
                             && sm.Name.Identifier.Text is "WaitAll" or "WaitAny"
                             && RootIdentifier(sm) == "Task":
                        Report(sm.Name.SpanStart, $".{sm.Name.Identifier.Text}()");
                        break;

                    // `<anything>.GetAwaiter().GetResult()` — the canonical
                    // sync-over-async bridge; kept broad (the chain is the signal).
                    case InvocationExpressionSyntax gInv
                        when gInv.Expression is MemberAccessExpressionSyntax maOuter
                             && maOuter.Name.Identifier.Text == "GetResult"
                             && maOuter.Expression is InvocationExpressionSyntax invInner
                             && invInner.Expression is MemberAccessExpressionSyntax maInner
                             && maInner.Name.Identifier.Text == "GetAwaiter":
                        Report(maInner.Name.SpanStart, ".GetAwaiter().GetResult()");
                        break;
                }
            }

            continue;

            void Report(int position, string pattern)
            {
                var line = LineOf(text, position);
                raw.Add(new Violation(path, line, SnippetOf(text, line),
                    $"sync-over-async: {pattern} on a Task in a UI-thread test method"));
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

    /// <summary>
    /// True when <paramref name="ma"/> is the callee of an invocation — i.e. the
    /// member is being CALLED (<c>Match.Result("$1")</c>), so it is a method named
    /// "Result", not the blocking <c>Task&lt;T&gt;.Result</c> property.
    /// </summary>
    private static bool IsInvokedMember(MemberAccessExpressionSyntax ma) =>
        ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma;

    /// <summary>
    /// True when <paramref name="receiver"/> is, judged PURELY SYNTACTICALLY (no
    /// semantic model), plausibly a <c>Task</c>/<c>Task&lt;T&gt;</c>: an
    /// <c>…Async()</c>-named invocation (incl. <c>Dispatcher.UIThread.InvokeAsync(…)</c>),
    /// a <c>Task.*(…)</c> call (<c>Task.Run</c>/<c>Task.WhenAll</c>/<c>Task.FromResult</c>),
    /// or an identifier/member whose name ends in "Task" (<c>downloadTask</c>,
    /// <c>_loadTask</c>). Everything else — <c>record</c>, <c>semaphore</c>, arbitrary
    /// properties — is treated as NOT a Task; that is the false-positive surface this
    /// guard deliberately avoids.
    /// </summary>
    private static bool IsTaskishReceiver(ExpressionSyntax receiver) => receiver switch
    {
        InvocationExpressionSyntax inv =>
            RightmostName(inv.Expression).EndsWith("Async", StringComparison.Ordinal)
            || RootIdentifier(inv.Expression) == "Task",
        IdentifierNameSyntax id => id.Identifier.Text.EndsWith("Task", StringComparison.OrdinalIgnoreCase),
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text.EndsWith("Task", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="node"/> sits lexically inside a <c>Task.Run(…)</c> /
    /// <c>Task.Factory.StartNew(…)</c> argument — blocking there runs on a thread-pool
    /// thread, not the single-threaded UI dispatcher, so it cannot deadlock.
    /// </summary>
    private static bool IsInsideOffloadedLambda(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>().Any(IsOffloadInvocation);

    private static bool IsOffloadInvocation(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax ma
        && ma.Name.Identifier.Text is "Run" or "StartNew"
        && RootIdentifier(ma) == "Task";

    /// <summary>The rightmost identifier of a (possibly dotted) expression —
    /// <c>InvokeAsync</c> from <c>Dispatcher.UIThread.InvokeAsync</c>.</summary>
    private static string RightmostName(ExpressionSyntax expr) => expr switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        SimpleNameSyntax id => id.Identifier.Text,
        _ => string.Empty,
    };

    /// <summary>The leftmost identifier of a (possibly dotted/invoked) expression —
    /// <c>Task</c> from <c>Task.Factory.StartNew</c>.</summary>
    private static string RootIdentifier(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => RootIdentifier(ma.Expression),
        InvocationExpressionSyntax inv => RootIdentifier(inv.Expression),
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
