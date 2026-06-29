using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// VR-specific dev-gate guard: flags a config-record property that is PARSED by the
/// loader but CONSUMED NOWHERE — a "dead config field". It exists because VR's
/// <c>RelayConfigLoader</c> loads every field with itself as the fallback default:
/// <code>config = defaults with { Field = OptionalX(root, "key", defaults.Field) };</code>
/// That <c>defaults.Field</c> getter access is a genuine read, so ReSharper
/// InspectCode's <c>NotAccessedPositionalProperty.Global</c> (already active in the
/// gate) can NEVER see a config field as unused — the entire record is immune to it.
/// This guard closes exactly that gap, treating the self-default read as a
/// non-consumer.
///
/// <para><b>Detection rule.</b> Keys on the <i>loader pattern</i>, not a hard-coded
/// type name, so it generalises to any config record loaded the same way:</para>
/// <list type="number">
///   <item><b>Candidates</b> — for every <c>operand with { Field = Call(…, operand.Field) }</c>
///         whose LAST argument is the self-default <c>operand.Field</c> (receiver
///         identifier == the <c>with</c> operand, member name == the assigned field),
///         and where <c>Field</c> is a real record property (so XAML-bound
///         <c>[ObservableProperty]</c>/VM/DI members, which are never loaded this way,
///         are structurally out of scope). The matched <c>operand.Field</c> node is
///         recorded as the self-default to EXCLUDE.</item>
///   <item><b>Consumers</b> — a candidate is "consumed" if ANY source reads it via a
///         <c>.Field</c> member access (e.g. <c>config.Field</c>) that is NOT the
///         recorded self-default, OR via a property-pattern subpattern
///         (<c>cfg is { Field: … }</c>). The record declaration and the
///         <c>with</c>-initializer write (<c>Field =</c>) are naturally non-consumers
///         (neither is a member-access/property-pattern read).</item>
/// </list>
/// A candidate with zero consumers is reported. Bias is toward false NEGATIVES, never
/// false positives: any read of the field by name marks it live, so a genuinely-used
/// field is never flagged. (A field consumed ONLY via positional record
/// deconstruction — by position, not name — is out of scope; absent here.)
///
/// <para>This polices VR's OWN source (like the other
/// <c>tools/VisualRelay.Guards/*Guard.cs</c>), so the loader-pattern knowledge is
/// appropriate; it must never run against user codebases / the relay engine. No I/O,
/// no git — callers supply the (path, source) pairs.</para>
///
/// <para><b>Complementary alternative (no custom code).</b> Refactor
/// <c>RelayConfigLoader</c> so <c>OptionalX</c> no longer passes <c>defaults.Field</c>
/// as the fallback (hold defaults as plain constants / a separate source the getter
/// never touches). Removing the phantom self-read lets the already-active
/// <c>NotAccessedPositionalProperty.Global</c> catch dead config fields with no guard
/// at all. That touches a deliberate, pervasive pattern across ~23 fields (larger,
/// riskier), so this scoped guard is preferred; the refactor is the path to take if
/// the loader is ever reworked.</para>
/// </summary>
public static class DeadConfigFieldGuard
{
    /// <summary>A config field parsed by the loader but consumed nowhere in the scanned sources.</summary>
    public sealed record Violation(string Field, string Path, int Line, string Reason);

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every dead config field across <paramref name="files"/>, ordered by
    /// field name (ordinal). Empty when every loaded field has a consumer.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var parsed = files
            .Select(f =>
            {
                var tree = CSharpSyntaxTree.ParseText(f.Source, ParseOptions);
                return (f.Path, Root: tree.GetRoot(), Text: tree.GetText());
            })
            .ToList();

        // Record properties (positional params + init-only props): name -> declaration site.
        var declarations = new Dictionary<string, (string Path, int Line)>(StringComparer.Ordinal);
        foreach (var (path, root, text) in parsed)
            CollectDeclarations(path, root, text, declarations);

        // Loader self-default candidates + the exact `operand.Field` nodes to exclude.
        var candidates = new Dictionary<string, (string Path, int Line)>(StringComparer.Ordinal);
        var selfDefaults = new HashSet<SyntaxNode>();
        foreach (var (_, root, _) in parsed)
            CollectCandidates(root, declarations, candidates, selfDefaults);

        if (candidates.Count == 0)
            return [];

        // A candidate is live if any source reads it (excluding its self-default).
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, root, _) in parsed)
            CollectConsumers(root, candidates, selfDefaults, consumed);

        return candidates
            .Where(c => !consumed.Contains(c.Key))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new Violation(c.Key, c.Value.Path, c.Value.Line,
                $"config field `{c.Key}` is parsed by the loader (defaults.{c.Key} self-default) " +
                "but has no consumer — a dead config field InspectCode cannot see"))
            .ToList();
    }

    /// <summary>Records each record's positional parameters and init-only properties (first site wins).</summary>
    private static void CollectDeclarations(
        string path, SyntaxNode root, SourceText text, Dictionary<string, (string, int)> sink)
    {
        foreach (var record in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
        {
            if (record.ParameterList is { } parameterList)
                foreach (var parameter in parameterList.Parameters)
                    AddDeclaration(sink, parameter.Identifier, path, text);

            foreach (var property in record.Members.OfType<PropertyDeclarationSyntax>())
                AddDeclaration(sink, property.Identifier, path, text);
        }
    }

    private static void AddDeclaration(
        Dictionary<string, (string, int)> sink, SyntaxToken identifier, string path, SourceText text)
    {
        if (!sink.ContainsKey(identifier.Text))
            sink[identifier.Text] = (path, LineOf(text, identifier.SpanStart));
    }

    /// <summary>
    /// Finds the loader self-default shape <c>operand with { Field = Call(…, operand.Field) }</c>.
    /// Only fields that are real record properties become candidates; the matched
    /// <c>operand.Field</c> node is recorded so the consumer pass excludes it.
    /// </summary>
    private static void CollectCandidates(
        SyntaxNode root,
        Dictionary<string, (string Path, int Line)> declarations,
        Dictionary<string, (string Path, int Line)> candidates,
        HashSet<SyntaxNode> selfDefaults)
    {
        foreach (var with in root.DescendantNodes().OfType<WithExpressionSyntax>())
        {
            if (with.Expression is not IdentifierNameSyntax operand)
                continue;

            foreach (var assignment in with.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not IdentifierNameSyntax field
                    || assignment.Right is not InvocationExpressionSyntax invocation)
                    continue;

                var arguments = invocation.ArgumentList.Arguments;
                if (arguments.Count == 0
                    || arguments[^1].Expression is not MemberAccessExpressionSyntax selfDefault
                    || selfDefault.Expression is not IdentifierNameSyntax receiver
                    || receiver.Identifier.Text != operand.Identifier.Text
                    || selfDefault.Name.Identifier.Text != field.Identifier.Text)
                    continue;

                var name = field.Identifier.Text;
                if (!declarations.TryGetValue(name, out var location))
                    continue; // not a real record property — out of scope

                selfDefaults.Add(selfDefault);
                candidates.TryAdd(name, location);
            }
        }
    }

    /// <summary>Marks a candidate live on any <c>.Field</c> member-access read (not its self-default) or property-pattern read.</summary>
    private static void CollectConsumers(
        SyntaxNode root,
        Dictionary<string, (string Path, int Line)> candidates,
        HashSet<SyntaxNode> selfDefaults,
        HashSet<string> consumed)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax memberAccess
                    when candidates.ContainsKey(memberAccess.Name.Identifier.Text)
                         && !selfDefaults.Contains(memberAccess):
                    consumed.Add(memberAccess.Name.Identifier.Text);
                    break;

                case SubpatternSyntax subpattern
                    when PropertyPatternName(subpattern) is { } name && candidates.ContainsKey(name):
                    consumed.Add(name);
                    break;
            }
        }
    }

    /// <summary>The property name a subpattern matches on — <c>{ Name: … }</c> or extended <c>{ A.Name: … }</c>.</summary>
    private static string? PropertyPatternName(SubpatternSyntax subpattern) =>
        subpattern.ExpressionColon?.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            _ => null,
        };

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;
}
