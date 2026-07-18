using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Pure matcher that flags dependency-injection bypasses — method parameters that
/// default to a real collaborator (<c>?? new GitInvoker()</c>,
/// <c>?? TimeProvider.System</c>) on an optional parameter, PLUS every call site
/// of that method which omits the argument while the enclosing class holds a
/// <c>RelayDriverDependencies</c> field or property. This is the
/// <c>EarlyImplementationDetector</c> bug shape: an injection seam silently
/// bypassed because the caller forgets to pass the injected dependency.
///
/// <para>Two-pass syntax-tree analysis:
/// <b>Pass 1</b> — find methods with optional params (= null default) where the
/// method body does <c>param ?? new GitInvoker()</c> or
/// <c>param ?? TimeProvider.System</c>. Record (containing-type-name, method-name,
/// param-index, default-kind).
/// <b>Pass 2</b> — walk all invocations matching recorded method names; when the
/// bypassed param-index argument is omitted, check if the enclosing
/// <c>ClassDeclarationSyntax</c> contains a field/property typed
/// <c>RelayDriverDependencies</c> (by type-name identifier match). Report the call
/// site with method name, omitted param, and the bypass default.</para>
///
/// <para>Self-exempts the matcher's own fixture carriers. No I/O, no git.</para>
/// </summary>
public static class DiBypassGuard
{
    /// <summary>Describes a DI-bypass violation at a call site (1-based <paramref name="Line"/>).</summary>
    public sealed record Violation(string Path, int Line, string Snippet, string Reason);

    /// <summary>Records a bypassable method signature found in Pass 1.</summary>
    private sealed record BypassableMethod(
        string ContainingTypeName,
        string MethodName,
        int ParamIndex,
        string DefaultKind);

    private static readonly string[] SelfExemptFileNames =
        ["DiBypassGuard.cs", "DiBypassGuardTests.cs"];

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every DI-bypass violation across <paramref name="files"/>,
    /// ordered by path (ordinal) then line.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var list = files as IReadOnlyCollection<(string Path, string Source)> ?? files.ToList();

        // Parse all files and extract info.
        var parsed = new List<(string Path, SyntaxNode Root, SourceText Text)>();
        foreach (var (path, source) in list)
        {
            if (SelfExemptFileNames.Contains(Path.GetFileName(path)))
                continue;

            var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
            parsed.Add((path, tree.GetRoot(), tree.GetText()));
        }

        return ComputeViolations(parsed);
    }

    /// <summary>
    /// Returns every DI-bypass violation across <paramref name="trees"/>,
    /// ordered by path (ordinal) then line. Uses pre-parsed <see cref="SyntaxTree"/>
    /// objects.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, SyntaxTree Tree)> trees)
    {
        var parsed = new List<(string Path, SyntaxNode Root, SourceText Text)>();

        foreach (var (path, tree) in trees)
        {
            if (SelfExemptFileNames.Contains(Path.GetFileName(path)))
                continue;

            parsed.Add((path, tree.GetRoot(), tree.GetText()));
        }

        return ComputeViolations(parsed);
    }

    private static IReadOnlyList<Violation> ComputeViolations(
        List<(string Path, SyntaxNode Root, SourceText Text)> parsed)
    {
        // Pass 1 — find all bypassable methods.
        var bypassable = new List<(BypassableMethod Method, string Path)>();
        foreach (var (path, root, _) in parsed)
        {
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var typeName = GetEnclosingTypeName(method);
                if (typeName is null)
                    continue;

                var parameters = method.ParameterList.Parameters;
                for (int i = 0; i < parameters.Count; i++)
                {
                    var p = parameters[i];
                    // Must be an optional parameter with a null default.
                    if (p.Default is null || p.Default.Value is null)
                        continue;
                    if (p.Default.Value is not LiteralExpressionSyntax lit || lit.Kind() != SyntaxKind.NullLiteralExpression)
                        continue;

                    // Look in the method body for `param ?? new GitInvoker()` or `param ?? TimeProvider.System`.
                    var paramName = p.Identifier.Text;
                    var defaultKind = FindBypassDefault(method, paramName);
                    if (defaultKind is null)
                        continue;

                    bypassable.Add((new BypassableMethod(typeName, method.Identifier.Text, i, defaultKind), path));
                }
            }
        }

        if (bypassable.Count == 0)
            return [];

        // Build a lookup: methodName → list of (bypassable, defining-path).
        var byName = new Dictionary<string, List<(BypassableMethod Method, string DefiningPath)>>(StringComparer.Ordinal);
        foreach (var (method, defPath) in bypassable)
        {
            if (!byName.ContainsKey(method.MethodName))
                byName[method.MethodName] = [];
            byName[method.MethodName].Add((method, defPath));
        }

        // Pass 2 — find call sites that omit the bypassed argument.
        var violations = new List<Violation>();
        foreach (var (path, root, text) in parsed)
        {
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? methodName;
                if (inv.Expression is MemberAccessExpressionSyntax ma)
                    methodName = ma.Name.Identifier.Text;
                else if (inv.Expression is IdentifierNameSyntax id)
                    methodName = id.Identifier.Text;
                else
                    continue;

                if (!byName.TryGetValue(methodName, out var candidates))
                    continue;

                var argCount = inv.ArgumentList.Arguments.Count;

                foreach (var (candidate, defPath) in candidates)
                {
                    // If the invocation has enough arguments to cover the bypassed param, skip.
                    if (argCount > candidate.ParamIndex)
                        continue;

                    // If this is the defining file, and the invocation is inside the defining method itself, skip.
                    if (path == defPath)
                    {
                        var enclosingMethod = inv.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                        if (enclosingMethod is not null && enclosingMethod.Identifier.Text == methodName)
                            continue;
                    }

                    // Check if enclosing class holds RelayDriverDependencies.
                    var enclosingClass = inv.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                    if (enclosingClass is null || !HasRelayDriverDependencies(enclosingClass))
                        continue;

                    var line = LineOf(text, inv.SpanStart);
                    violations.Add(new Violation(path, line, SnippetOf(text, line),
                        $"di-bypass: call to `{candidate.ContainingTypeName}.{methodName}` omits parameter " +
                        $"#{candidate.ParamIndex + 1} (`{BypassedParamName(candidate)}`), which defaults to " +
                        $"{candidate.DefaultKind} — the enclosing class holds `RelayDriverDependencies`, " +
                        $"so an injected seam is silently bypassed"));
                }
            }
        }

        violations.Sort((a, b) =>
        {
            var byPath = string.CompareOrdinal(a.Path, b.Path);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return violations;
    }

    /// <summary>
    /// Looks inside <paramref name="method"/>'s body for a
    /// <c>paramName ?? new GitInvoker()</c> or <c>paramName ?? TimeProvider.System</c>
    /// expression. Returns the default kind string (e.g. "new GitInvoker()") or null.
    /// </summary>
    private static string? FindBypassDefault(MethodDeclarationSyntax method, string paramName)
    {
        if (method.Body is null && method.ExpressionBody is null)
            return null;

        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null)
            return null;

        foreach (var coalesce in body.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (!coalesce.IsKind(SyntaxKind.CoalesceExpression))
                continue;

            // Left side must be the parameter name.
            if (coalesce.Left is IdentifierNameSyntax ident && ident.Identifier.Text == paramName)
            {
                var right = coalesce.Right;
                var rightText = right.ToString();
                if (rightText.Contains("new GitInvoker()", StringComparison.Ordinal))
                    return "new GitInvoker()";
                if (rightText.Contains("TimeProvider.System", StringComparison.Ordinal))
                    return "TimeProvider.System";
            }
        }

        return null;
    }

    private static string? GetEnclosingTypeName(MethodDeclarationSyntax method)
    {
        var type = method.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        return type?.Identifier.Text;
    }

    private static bool HasRelayDriverDependencies(ClassDeclarationSyntax cls)
    {
        foreach (var member in cls.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                if (field.Declaration.Type.ToString().Contains("RelayDriverDependencies", StringComparison.Ordinal))
                    return true;
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                if (prop.Type.ToString().Contains("RelayDriverDependencies", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static string BypassedParamName(BypassableMethod m)
    {
        return m.DefaultKind switch
        {
            "new GitInvoker()" => "gitInvoker/gi",
            "TimeProvider.System" => "timeProvider/tp",
            _ => "?",
        };
    }

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;

    private static string SnippetOf(SourceText text, int line)
    {
        var s = text.Lines[line - 1].ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
