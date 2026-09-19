using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace VisualRelay.Tests;

/// <summary>
/// Every WSL share path written in a test names a distro no machine has. On Windows a share
/// path to a distro that exists reaches it, and touching one that is stopped boots it:
/// building a launch reads <c>&lt;root&gt;\.git</c>, which cost 2.7 s idle and 16 to 45 s per
/// test under the suite's load (measured 2026-09-18), and a distro stops again 15.9 s after
/// its last use, so how many boots a run paid was down to scheduling. A distro that does not
/// exist answers in about 25 ms and boots nothing. The real-WSL tests take their distro from a
/// live probe, never from a literal, so nothing legitimate is caught here.
/// </summary>
/// <param name="treesFixture">The parsed sources, shared by every guard that scans them.</param>
public sealed partial class FakeWslPathGuardTests(CachedSyntaxTreesFixture treesFixture)
{
    /// <summary>What every distro named in a test's share path starts with.</summary>
    private const string FictionalPrefix = "VrNoSuch";

    [GeneratedRegex(@"(?:\\\\|//)wsl(?:\.localhost|\$)[\\/](?<distro>[^\\/\s""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex SharePath();

    [Fact]
    public void EveryWslSharePathInTheTests_NamesADistroNoMachineHas()
    {
        var offenders = treesFixture.AllTrees
            .Where(t => t.RelativePath.Replace('\\', '/').StartsWith("tests/VisualRelay.Tests/", StringComparison.Ordinal))
            .SelectMany(t => t.Tree.GetRoot().DescendantTokens()
                .Where(IsStringText)
                .SelectMany(token => SharePath().Matches(token.ValueText)
                    .Where(match => NamesARealLookingDistro(match.Groups["distro"].Value))
                    .Select(match => $"{t.RelativePath}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {match.Value}")))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"WSL share paths in tests must name a distro starting with {FictionalPrefix} (VrNoSuchDistro, or " +
            "VrNoSuchOtherDistro where a test needs two), because on Windows a real distro's share path boots " +
            "that distro when a test touches it:\n" + string.Join("\n", offenders));
    }

    // A name made only of dots is a traversal case and a name in angle brackets is a placeholder
    // in a message; neither can be a distro, so neither can boot one.
    private static bool NamesARealLookingDistro(string distro) =>
        !distro.StartsWith(FictionalPrefix, StringComparison.OrdinalIgnoreCase)
        && distro.Any(c => c != '.')
        && !distro.StartsWith('<');

    private static bool IsStringText(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken)
        || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
        || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
        || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken);
}
