using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags <c>for</c>/<c>while</c> loops whose body contains BOTH
/// a real delay call (<c>Task.Delay</c>/<c>Thread.Sleep</c>) AND an invocation whose
/// receiver or arguments involve <c>IGitInvoker</c>, <c>RunAsync</c>, or process types
/// (<c>Process.Start</c>, <c>ProcessStartInfo</c>). This is the <c>RunGitAsync</c>
/// bug shape: retrying a deterministic failure (e.g. exit-128 <c>fatal: not a git
/// repository</c>) that can never succeed, burning wall-clock time with backoff sleeps.
///
/// <para>For each reported loop the matcher notes the attempt-count constant from the
/// loop bound, backoff-delay constants from delay expressions, and whether any
/// identifier in the loop body suggests failure classification (<c>isSuccess</c>,
/// <c>isSuccessExit</c>, <c>*Classifier*</c>, <c>shouldRetry</c>, or a
/// <c>Func&lt;int,bool&gt;</c>). A classifier does NOT exempt the loop — the finding
/// is still reported — but its presence is noted so a reviewer can decide whether the
/// retry is reasonable.</para>
///
/// <para>No I/O, no git — callers supply the (path, source) pairs. Self-exempts the
/// matcher's own fixture carriers.</para>
/// </summary>
public static class RetryDelayLoopsGuard
{
    /// <summary>Describes a retry-delay-loop violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    private static readonly string[] SelfExemptFileNames =
        ["RetryDelayLoopsGuard.cs", "RetryDelayLoopsGuardTests.cs"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every retry-delay-loop violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var violations = new List<Violation>();

        foreach (var (path, source) in files)
        {
            if (SelfExemptFileNames.Contains(Path.GetFileName(path)))
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
    /// Returns every retry-delay-loop violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Uses pre-parsed <see cref="SyntaxTree"/>
    /// objects.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, SyntaxTree Tree)> trees)
    {
        var violations = new List<Violation>();

        foreach (var (path, tree) in trees)
        {
            if (SelfExemptFileNames.Contains(Path.GetFileName(path)))
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

        foreach (var loop in root.DescendantNodes().OfType<StatementSyntax>())
        {
            StatementSyntax? body;
            int loopKeywordPosition;

            switch (loop)
            {
                case ForStatementSyntax forStmt:
                    body = forStmt.Statement;
                    loopKeywordPosition = forStmt.ForKeyword.SpanStart;
                    break;
                case WhileStatementSyntax whileStmt:
                    body = whileStmt.Statement;
                    loopKeywordPosition = whileStmt.WhileKeyword.SpanStart;
                    break;
                default:
                    continue;
            }

            if (body is null)
                continue;

            var hasDelay = HasDelayCall(body);
            var hasGitOrProcessInvocation = HasGitOrProcessInvocation(body);

            if (!hasDelay || !hasGitOrProcessInvocation)
                continue;

            var line = LineOf(text, loopKeywordPosition);
            var reason = BuildReason(body);

            sink.Add(new Violation(path, line, SnippetOf(text, line), reason));
        }
    }

    private static bool HasDelayCall(SyntaxNode body)
    {
        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma)
                continue;

            var method = ma.Name.Identifier.Text;
            var owner = RightmostIdentifier(ma.Expression);

            if (method == "Sleep" && owner == "Thread")
                return true;
            if (method == "Delay" && owner == "Task")
                return true;
        }

        return false;
    }

    private static bool HasGitOrProcessInvocation(SyntaxNode body)
    {
        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma)
            {
                var method = ma.Name.Identifier.Text;
                // Any invocation involving RunAsync, Process.Start, or git-like names.
                if (method == "RunAsync")
                    return true;

                var receiver = RightmostIdentifier(ma.Expression);
                if (receiver == "Process" && method == "Start")
                    return true;

                // Also catch anything with GitInvoker in the receiver chain.
                if (ma.Expression.ToString().Contains("GitInvoker", StringComparison.Ordinal))
                    return true;
            }
        }

        // Also check for object creations of process types.
        foreach (var creation in body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type.ToString();
            if (typeName.Contains("ProcessStartInfo", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string BuildReason(SyntaxNode body)
    {
        // Extract attempt-count constant from the loop.
        var attemptInfo = ExtractConstants(body);
        var classifierInfo = HasClassifier(body) ? " (classifier present)" : " (no classifier)";

        return $"retry-delay-loop: loop body contains both a delay and a git/process invocation — " +
               $"retrying a deterministic failure that may never succeed{attemptInfo}{classifierInfo}";
    }

    private static string ExtractConstants(SyntaxNode body)
    {
        // Walk up to the enclosing for/while and extract constants from the condition / initializer.
        var loop = body.Parent;
        var constants = new List<string>();

        if (loop is ForStatementSyntax forStmt)
        {
            // Look for `int attempt = 1; attempt <= 3; attempt++` — extract the bound.
            if (forStmt.Condition is BinaryExpressionSyntax cond
                && cond.Right is LiteralExpressionSyntax lit)
            {
                constants.Add($"maxAttempts={lit.Token.ValueText}");
            }
        }

        if (loop is WhileStatementSyntax whileStmt)
        {
            if (whileStmt.Condition is BinaryExpressionSyntax cond
                && cond.Right is LiteralExpressionSyntax lit)
            {
                constants.Add($"bound={lit.Token.ValueText}");
            }
        }

        // Find delay durations in the body.
        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is MemberAccessExpressionSyntax ma
                && ma.Name.Identifier.Text is "Delay" or "Sleep"
                && inv.ArgumentList.Arguments.Count > 0)
            {
                var firstArg = inv.ArgumentList.Arguments[0].Expression;
                if (firstArg is LiteralExpressionSyntax litArg)
                {
                    constants.Add($"delay={litArg.Token.ValueText}ms");
                }
                else
                {
                    var argText = firstArg.ToString();
                    if (argText.Length <= 60)
                        constants.Add($"delay={argText}");
                }
            }
        }

        return constants.Count > 0 ? $" [{string.Join(", ", constants)}]" : "";
    }

    private static bool HasClassifier(SyntaxNode body)
    {
        foreach (var ident in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var name = ident.Identifier.Text;
            // Check for classifiers like isSuccess, isSuccessExit, *Classifier*, shouldRetry.
            if (name.Contains("isSuccess", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Classifier", StringComparison.Ordinal)
                || name.Contains("shouldRetry", StringComparison.OrdinalIgnoreCase)
                || name.Contains("isTransient", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Also check for Func<int, bool> style delegates.
        foreach (var gen in body.DescendantNodes().OfType<GenericNameSyntax>())
        {
            if (gen.Identifier.Text == "Func")
                return true;
        }

        return false;
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
