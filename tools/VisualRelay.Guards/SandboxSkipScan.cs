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
///   <item>a bare <c>Skip…()</c> helper call (e.g. <c>SkipIfNotOptedIn()</c>) — by
///         convention these wrap the env-var opt-in;</item>
///   <item>an <c>Assert.Skip(…)</c> alongside an
///         <c>Environment.GetEnvironmentVariable</c> read in the same scope.</item>
/// </list>
/// A bare platform skip (<c>OperatingSystem.IsMacOS</c> → <c>Assert.Skip</c>) with
/// no env read does NOT count: the spawn/gate still runs on the macOS verify host.
/// Factored out of <see cref="RealBuildSubprocessGuard"/> so the build-subprocess
/// guard and the gate-as-test guard accept the one well-known marker identically.
/// </summary>
internal static class SandboxSkipScan
{
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

    private static bool IsSkipGate(InvocationExpressionSyntax inv, bool hasEnvOptIn) => inv.Expression switch
    {
        // Bare helper whose name starts with "Skip" — e.g. SkipIfNotOptedIn(); by
        // convention these wrap the env-var opt-in. (LINQ's Skip is `collection.Skip`,
        // a member access, so it is excluded.)
        IdentifierNameSyntax id => id.Identifier.Text.StartsWith("Skip", StringComparison.Ordinal),
        // Assert.Skip(...) counts only alongside an env-var opt-in in the same scope.
        MemberAccessExpressionSyntax ma => hasEnvOptIn
            && ma.Name.Identifier.Text == "Skip"
            && RightmostName(ma.Expression) == "Assert",
        _ => false,
    };

    private static string RightmostName(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => string.Empty,
    };
}
