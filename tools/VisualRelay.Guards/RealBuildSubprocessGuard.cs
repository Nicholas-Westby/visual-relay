using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags a test launching a REAL, heavy build/package
/// subprocess — the sandbox-safety counterpart of <see cref="RealSleepGuard"/>.
/// Such a child (<c>dotnet build|publish|run</c>, <c>cargo build</c>,
/// <c>npm install</c>, <c>go test</c>, …) can <b>wedge</b> when the suite runs
/// inside an OS sandbox that denies it a resource (on macOS, a child
/// <c>dotnet</c> intermittently blocks on the denied <c>com.apple.SecurityServer</c>
/// mach-lookup). The test host's blame-hang collector then kills the whole run,
/// discarding every test that already passed. A normal target codebase never
/// spawns these from its own tests, so this guard keeps the suite sandbox-safe
/// by construction. The pattern — not any VR symbol — is what it keys on, so it
/// ports to any C# codebase.
///
/// It Roslyn-parses each source and flags every <c>new ProcessStartInfo(...)</c>
/// / <c>Process.Start(...)</c> whose program is a known build tool AND whose
/// first CLI argument is a heavy verb (build, publish, run, restore, test,
/// install, …) — UNLESS the spawn is bounded or opted out:
/// <list type="bullet">
///   <item>a timeout in the same method (<c>WaitForExit(&lt;n&gt;)</c> / <c>CancelAfter(…)</c>);</item>
///   <item>a skip gate in the same method (<c>Assert.Skip(…)</c> or a
///         <c>Skip…()</c> helper such as an opt-in <c>SkipIfNotOptedIn()</c>);</item>
///   <item>an inline <c>// vr-allow-subprocess: &lt;reason&gt;</c> on the spawn line.</item>
/// </list>
/// Program and verb are read from string LITERALS only: a variable
/// <c>FileName</c> (a PATH-resolved native tool like <c>magick</c>/<c>iconutil</c>)
/// or an assertion string that merely mentions "dotnet build" is never matched.
/// Self-exempts its own fixture files. No I/O, no git — callers supply the
/// (path, source) pairs.
/// </summary>
public static class RealBuildSubprocessGuard
{
    /// <summary>Describes a single unbounded-build-subprocess violation (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Programs whose build/run children are heavy enough to wedge under a sandbox.</summary>
    private static readonly HashSet<string> BuildTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "msbuild", "nuget",
        "cargo", "go", "swift", "swiftc",
        "npm", "npx", "yarn", "pnpm", "bun",
        "mvn", "gradle", "bazel", "make", "cmake", "ninja",
        "pip", "pip3", "poetry", "uv",
    };

    /// <summary>The first-argument subcommands that make a build tool do heavy work.</summary>
    private static readonly HashSet<string> HeavyVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "publish", "run", "restore", "test",
        "pack", "install", "compile", "watch", "bench",
    };

    /// <summary>A same-line suppression — only valid with a non-empty reason after the colon.</summary>
    private static readonly Regex AllowMarkerPattern =
        new(@"//\s*vr-allow-subprocess:\s*\S", RegexOptions.Compiled);

    /// <summary>Filenames whose own bodies legitimately contain these spawn fixtures.</summary>
    private static readonly string[] SelfExemptFileNames =
        ["RealBuildSubprocessGuard.cs", "RealBuildSubprocessGuardTests.cs"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every unbounded-build-subprocess violation across <paramref name="files"/>,
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

        foreach (var node in root.DescendantNodes())
        {
            var (tool, verb, programToken) = DescribeSpawn(node);
            if (tool is null || verb is null || !BuildTools.Contains(tool) || !HeavyVerbs.Contains(verb))
                continue;

            var line = LineOf(text, programToken.SpanStart);
            if (AllowMarkerPattern.IsMatch(text.Lines[line - 1].ToString()))
                continue;
            if (IsBounded(node))
                continue;
            if (!seen.Add(line))
                continue;

            sink.Add(new Violation(path, line, SnippetOf(text, line),
                $"test launches real `{tool} {verb}` subprocess with no timeout or skip-guard"));
        }
    }

    /// <summary>
    /// If <paramref name="node"/> configures a process launch, returns its
    /// (tool, first-verb, program-literal token); otherwise (null, null, default).
    /// </summary>
    private static (string? Tool, string? Verb, SyntaxToken Program) DescribeSpawn(SyntaxNode node) => node switch
    {
        ObjectCreationExpressionSyntax oce when RightmostName(oce.Type) == "ProcessStartInfo" => DescribePsi(oce),
        InvocationExpressionSyntax inv when IsProcessStart(inv) => DescribeProcessStart(inv),
        _ => (null, null, default),
    };

    private static (string?, string?, SyntaxToken) DescribePsi(ObjectCreationExpressionSyntax oce)
    {
        var ctorArgs = oce.ArgumentList?.Arguments ?? default;

        // Program: ctor first arg, else a FileName = "..." initializer.
        SyntaxToken? programToken = ctorArgs.Count >= 1 ? LiteralToken(ctorArgs[0].Expression) : null;
        programToken ??= InitializerLiteral(oce, "FileName");
        if (programToken is null)
            return (null, null, default);

        // Verb: ctor second arg, else an Arguments = "..." / ArgumentList = { "verb", … } initializer.
        var verb = ctorArgs.Count >= 2 ? FirstWord(LiteralText(ctorArgs[1].Expression)) : null;
        verb ??= FirstWord(InitializerLiteral(oce, "Arguments")?.ValueText);
        verb ??= FirstArgListElement(oce);

        return (NormalizeTool(programToken.Value), verb, programToken.Value);
    }

    private static (string?, string?, SyntaxToken) DescribeProcessStart(InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        if (args.Count == 0)
            return (null, null, default);

        var programToken = LiteralToken(args[0].Expression);
        if (programToken is null)
            return (null, null, default);

        var verb = args.Count >= 2 ? FirstWord(LiteralText(args[1].Expression)) : null;
        return (NormalizeTool(programToken.Value), verb, programToken.Value);
    }

    /// <summary>True for a method/local-function/lambda-local timeout or skip gate around the spawn.</summary>
    private static bool IsBounded(SyntaxNode spawn)
    {
        var scope = spawn.FirstAncestorOrSelf<SyntaxNode>(n =>
            n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
              or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax)
            ?? spawn.SyntaxTree.GetRoot();

        var invocations = scope.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        // An Assert.Skip only counts as a sandbox opt-out when the same scope reads an
        // environment variable to decide it (the VR_RUN_NONO_INTEGRATION idiom). An
        // unrelated platform skip (OperatingSystem.IsMacOS) must NOT excuse the spawn,
        // because it still runs the build child on the (macOS) verify host.
        var hasEnvOptIn = invocations.Any(IsEnvironmentRead);

        foreach (var inv in invocations)
        {
            // Timeout: WaitForExit(<arg>) or any CancelAfter(...).
            if (inv.Expression is MemberAccessExpressionSyntax tma)
            {
                var n = tma.Name.Identifier.Text;
                if (n == "CancelAfter")
                    return true;
                if (n == "WaitForExit" && inv.ArgumentList.Arguments.Count >= 1)
                    return true;
            }

            if (IsSkipGate(inv, hasEnvOptIn))
                return true;
        }

        return false;
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

    private static bool IsProcessStart(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Start" } ma
        && RightmostName(ma.Expression) == "Process";

    private static SyntaxToken? InitializerLiteral(ObjectCreationExpressionSyntax oce, string member)
    {
        foreach (var assign in Assignments(oce))
        {
            // ReSharper disable once MergeIntoPattern — `member` is a variable parameter, cannot use constant pattern
            if (assign.Left is IdentifierNameSyntax id && id.Identifier.Text == member)
                return LiteralToken(assign.Right);
        }

        return null;
    }

    private static string? FirstArgListElement(ObjectCreationExpressionSyntax oce)
    {
        foreach (var assign in Assignments(oce))
        {
            if (assign.Left is IdentifierNameSyntax { Identifier.Text: "ArgumentList" }
                // ReSharper disable once MergeIntoPattern — assign.Right is a separate member access, not a constant property
                && assign.Right is InitializerExpressionSyntax init)
            {
                foreach (var e in init.Expressions)
                {
                    if (LiteralToken(e) is { } t)
                        return FirstWord(t.ValueText);
                }
            }
        }

        return null;
    }

    private static IEnumerable<AssignmentExpressionSyntax> Assignments(ObjectCreationExpressionSyntax oce) =>
        oce.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [];

    private static SyntaxToken? LiteralToken(ExpressionSyntax? expr) =>
        expr is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.StringLiteralToken)
            ? lit.Token : null;

    private static string? LiteralText(ExpressionSyntax? expr) => LiteralToken(expr)?.ValueText;

    private static string? NormalizeTool(SyntaxToken programToken)
    {
        var name = Path.GetFileName(programToken.ValueText);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Length == 0 ? null : name;
    }

    private static string? FirstWord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var parts = value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    private static string RightmostName(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => string.Empty,
    };

    private static string RightmostName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax qn => qn.Right.Identifier.Text,
        _ => type.ToString(),
    };

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
