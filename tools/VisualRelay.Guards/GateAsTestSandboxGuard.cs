using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// VR-specific dev-gate guard: flags a test that runs a shell-out dev gate
/// end-to-end as a "gate-as-test" — an invocation of <c>…Gate.Run(…)</c> (e.g.
/// <c>InspectCodeGate.Run(paths)</c>) — UNLESS the test carries the
/// <c>VR_RUN_NONO_INTEGRATION</c> opt-in skip-guard (or an inline allow-marker).
/// A gate-as-test re-runs a whole dev gate inside the suite; gates like InspectCode
/// shell out to an external tool (JetBrains ReSharper) that the verify's nono
/// sandbox denies a host write / keychain lookup, so the test FALSE-FAILS under
/// nono even though nothing the task did is wrong. The dev gate's coverage is
/// retained where it runs unsandboxed (<c>./visual-relay check</c> step 6), so the
/// in-suite copy must be skip-guarded out of the sandboxed verify.
///
/// <para><see cref="RealBuildSubprocessGuard"/> structurally cannot catch this: the
/// spawn is indirect — a variable program inside the gate with a non-build verb,
/// outside the scanned tree — so it needs this gate-aware sibling. This guard keys
/// on VR's <c>…Gate</c> naming convention, which is appropriate because it polices
/// VR's OWN dev-gate test-infra, not the general-purpose relay engine. The carve-out
/// (env-gated opt-in skip) is shared with the build-subprocess guard via
/// <see cref="SandboxSkipScan"/> so both accept the one well-known marker.</para>
///
/// Literal/AST-based and self-exempt by filename, mirroring the build-subprocess
/// guard. No I/O, no git — callers supply the (path, source) pairs.
/// </summary>
public static class GateAsTestSandboxGuard
{
    /// <summary>Describes a single un-skip-guarded gate-as-test violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>A same-line suppression — only valid with a non-empty reason after the colon.</summary>
    private static readonly Regex AllowMarkerPattern =
        new(@"//\s*vr-allow-gate-as-test:\s*\S", RegexOptions.Compiled);

    /// <summary>Filenames whose own bodies legitimately contain these gate-call fixtures.</summary>
    private static readonly string[] SelfExemptFileNames =
        ["GateAsTestSandboxGuard.cs", "GateAsTestSandboxGuardTests.cs"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every un-skip-guarded gate-as-test violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line. Self-exempt files yield nothing.
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
        var seen = new HashSet<int>();

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsGateRun(inv, out var gateName, out var nameToken))
                continue;

            var line = LineOf(text, nameToken.SpanStart);
            if (AllowMarkerPattern.IsMatch(text.Lines[line - 1].ToString()))
                continue;
            if (SandboxSkipScan.HasOptInSkip(SandboxSkipScan.EnclosingScope(inv)))
                continue;
            if (!seen.Add(line))
                continue;

            sink.Add(new Violation(path, line, SnippetOf(text, line),
                $"test invokes shell-out gate `{gateName}.Run(...)` with no VR_RUN_NONO_INTEGRATION skip-guard"));
        }
    }

    /// <summary>
    /// True when <paramref name="inv"/> is a <c>…Gate.Run(…)</c> call: a member access
    /// whose method name is <c>Run</c> and whose receiver's rightmost name ends with
    /// <c>Gate</c> (so <c>InspectCodeGate.Run</c> and <c>Gates.InspectCodeGate.Run</c>
    /// both match, but <c>TestGit.Run</c> does not).
    /// </summary>
    private static bool IsGateRun(InvocationExpressionSyntax inv, out string gateName, out SyntaxToken nameToken)
    {
        gateName = string.Empty;
        nameToken = default;

        if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Run" } ma)
            return false;

        var receiver = RightmostName(ma.Expression);
        if (receiver.Length <= "Gate".Length || !receiver.EndsWith("Gate", StringComparison.Ordinal))
            return false;

        gateName = receiver;
        nameToken = ma.Name.Identifier;
        return true;
    }

    private static string RightmostName(ExpressionSyntax expr) => expr switch
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
