using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VisualRelay.Guards;

/// <summary>
/// Shared AST predicate for the sandbox-safety guards: does a method/lambda scope
/// opt out of the sandboxed default verify run via the <c>VR_RUN_NONO_INTEGRATION</c>
/// idiom? A spawn or shell-out gate guarded this way SKIPS in the default suite, so
/// it can neither wedge nor false-fail under nono. Recognises exactly two forms
/// (kept in lockstep with the house idiom in <c>NonoRealBuildTests</c>):
/// <list type="bullet">
///   <item>a call to the known opt-in helper <c>SkipIfNotOptedIn(…)</c> — bare (via
///         <c>using static</c>) or qualified (<c>NonoIntegration.SkipIfNotOptedIn(…)</c>),
///         i.e. the invoked method's rightmost name is <c>SkipIfNotOptedIn</c>. The
///         helper wraps the env-var opt-in INTERNALLY, so a scope-local scan sees only
///         this call, not the env read — hence it must be recognised by name;</item>
///   <item>an <c>Assert.Skip(…)</c> alongside an
///         <c>Environment.GetEnvironmentVariable</c> read in the same scope (the inline
///         opt-in form, e.g. <c>PackagingToolTests</c>).</item>
/// </list>
/// Matching the EXACT helper name (not a loose "starts with <c>Skip</c>") is
/// deliberate: an unrelated <c>SkipWhitespace()</c> / <c>collection.Skip(n)</c> in the
/// same scope must NOT excuse the spawn/gate. A bare platform skip
/// (<c>OperatingSystem.IsMacOS</c> → <c>Assert.Skip</c>) with no env read does NOT
/// count either: the spawn/gate still runs on the macOS verify host.
/// Factored out of <see cref="RealBuildSubprocessGuard"/> so the build-subprocess
/// guard and the gate-as-test guard accept the one well-known marker identically.
/// <c>SkipIfNotOptedIn</c> is a test-helper-name convention, not a VR engine symbol.
/// </summary>
internal static class SandboxSkipScan
{
    /// <summary>
    /// The opt-in test-helper name (a test-idiom naming convention, NOT a VR engine
    /// symbol) that wraps the <c>VR_RUN_NONO_INTEGRATION</c> env read; see
    /// <c>NonoIntegration.SkipIfNotOptedIn</c>.
    /// </summary>
    private const string OptInHelperName = "SkipIfNotOptedIn";

    /// <summary>The nearest enclosing method/local-function/accessor/lambda, else the tree root.</summary>
    internal static SyntaxNode EnclosingScope(SyntaxNode node) =>
        node.FirstAncestorOrSelf<SyntaxNode>(n =>
            n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
              or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax)
        ?? node.SyntaxTree.GetRoot();

    /// <summary>True when <paramref name="scope"/> carries an env-gated sandbox opt-out skip.</summary>
    internal static bool HasOptInSkip(SyntaxNode scope)
    {
        var invocations = scope.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        // An Assert.Skip only counts as a sandbox opt-out when the same scope reads an
        // environment variable to decide it (the VR_RUN_NONO_INTEGRATION idiom). An
        // unrelated platform skip (OperatingSystem.IsMacOS) must NOT excuse the spawn,
        // because it still runs on the (macOS) verify host.
        var hasEnvOptIn = invocations.Any(IsEnvironmentRead);
        return invocations.Any(inv => IsSkipGate(inv, hasEnvOptIn));
    }

    private static bool IsEnvironmentRead(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "GetEnvironmentVariable" };

    private static bool IsSkipGate(InvocationExpressionSyntax inv, bool hasEnvOptIn)
    {
        // The one known opt-in helper, recognised by the invoked method's rightmost
        // name — whether called bare via `using static` (SkipIfNotOptedIn(...)) or
        // qualified (NonoIntegration.SkipIfNotOptedIn(...)). It wraps the env opt-in
        // internally, so the scope-local scan never sees the env read — a name-based
        // signal is the only thing available. Matching the EXACT name (not "starts
        // with Skip") keeps unrelated calls like SkipWhitespace() / collection.Skip(n)
        // from excusing the spawn/gate.
        if (RightmostName(inv.Expression) == OptInHelperName)
            return true;

        // Otherwise only an inline Assert.Skip(...) alongside an env-var opt-in read in
        // the same scope counts (the inline VR_RUN_NONO_INTEGRATION idiom). A bare
        // platform skip (OperatingSystem.IsMacOS -> Assert.Skip) with no env read does
        // not, because the spawn/gate still runs on the macOS verify host.
        return hasEnvOptIn
            && inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Skip" } ma
            && RightmostName(ma.Expression) == "Assert";
    }

    /// <summary>
    /// The rightmost identifier of an expression: a bare name as-is, or the trailing
    /// member of a member access. Used to read a call's method name (bare or qualified)
    /// and a receiver's last segment. Shared with the sandbox-safety guards.
    /// </summary>
    internal static string RightmostName(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => string.Empty,
    };
}
