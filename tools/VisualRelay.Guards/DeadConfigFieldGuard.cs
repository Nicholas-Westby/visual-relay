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
/// <para><b>Bare-name keying.</b> Consumer matching keys on the field NAME alone, ignoring
/// the declaring type, so an unrelated <c>Other.Field</c> read (e.g. <c>StageInvocation.MaxTurns</c>
/// vs <c>RelayConfig.MaxTurns</c>) can also mark a config <c>Field</c> live. A collision only
/// ever ADDS a consumer — never removes one — so it can suppress a fire but never cause one,
/// consistent with the false-negative bias; it cannot break the gate.</para>
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
    /// Finds the loader self-default shape — <c>operand with { Field = …operand.Field… }</c>
    /// where <c>operand.Field</c> is passed AS A CALL/CONSTRUCTOR ARGUMENT (the fallback in
    /// <c>OptionalX(…, operand.Field)</c> or the seed in <c>new Dictionary&lt;…&gt;(operand.Field)</c>,
    /// inline or via a local the assignment aliases). Only real record properties become
    /// candidates; EVERY matched <c>operand.Field</c> read is recorded so the consumer pass
    /// excludes it. Requiring the self-read to be a call/ctor argument (not merely present
    /// anywhere in the RHS) keeps ordinary self-referential copies — <c>x with { N = x.N + 1 }</c>,
    /// <c>x with { H = y ?? x.H }</c> — out of scope: those are plain reads InspectCode can see.
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
                if (assignment.Left is not IdentifierNameSyntax field)
                    continue;

                var name = field.Identifier.Text;
                if (!declarations.TryGetValue(name, out var location))
                    continue; // not a real record property — out of scope

                var reads = SelfDefaultReads(assignment.Right, operand, name, with);
                if (!reads.Any(IsCallArgument))
                    continue; // self-read isn't a parse fallback/seed — not a loaded field

                foreach (var read in reads)
                    selfDefaults.Add(read);
                candidates.TryAdd(name, location);
            }
        }
    }

    /// <summary>
    /// Every <c>operand.Field</c> self-read supplying this assignment's value — in the RHS
    /// directly, plus (one level) the initializer of a local the RHS aliases (<c>Field = local</c>
    /// where <c>var local = …operand.Field…</c>, the loader's by-tier dictionary-merge shape).
    /// </summary>
    private static IReadOnlyList<MemberAccessExpressionSyntax> SelfDefaultReads(
        ExpressionSyntax rhs, IdentifierNameSyntax operand, string field, WithExpressionSyntax with)
    {
        var reads = SelfReadsIn(rhs, operand, field).ToList();
        if (rhs is IdentifierNameSyntax alias && FindLocalInitializer(alias, with) is { } init)
            reads.AddRange(SelfReadsIn(init, operand, field));
        return reads;
    }

    /// <summary>The <c>operand.Field</c> member-access reads anywhere within <paramref name="scope"/>.</summary>
    private static IEnumerable<MemberAccessExpressionSyntax> SelfReadsIn(
        SyntaxNode scope, IdentifierNameSyntax operand, string field) =>
        scope.DescendantNodesAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax receiver
                        && receiver.Identifier.Text == operand.Identifier.Text
                        && m.Name.Identifier.Text == field);

    /// <summary>The initializer of the local named <paramref name="alias"/> in the enclosing method/local-function, or null.</summary>
    private static ExpressionSyntax? FindLocalInitializer(IdentifierNameSyntax alias, WithExpressionSyntax with)
    {
        var scope = with.Ancestors()
            .FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
        var declarator = scope?.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == alias.Identifier.Text && v.Initializer is not null);
        return declarator?.Initializer?.Value;
    }

    /// <summary>True when <paramref name="node"/> is an argument to an invocation or object-creation (a parse fallback/seed).</summary>
    private static bool IsCallArgument(SyntaxNode node) =>
        node.Parent is ArgumentSyntax
        {
            Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax }
        };

    /// <summary>
    /// Marks a candidate live on any read by name: a <c>.Field</c> member access (not its
    /// recorded self-default), a null-conditional <c>?.Field</c> member binding
    /// (<c>c?.Field</c>, chained <c>h?.Cfg?.Field</c>), or a property-pattern read
    /// (<c>{ Field: … }</c> / extended <c>{ Field.Sub: … }</c>).
    /// </summary>
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

                // `c?.Field` / chained `h?.Cfg?.Field` compile to a MemberBinding, not a
                // MemberAccess; a self-default (`operand.Field`) is never null-conditional,
                // so a binding read is always a genuine consumer.
                case MemberBindingExpressionSyntax memberBinding
                    when candidates.ContainsKey(memberBinding.Name.Identifier.Text):
                    consumed.Add(memberBinding.Name.Identifier.Text);
                    break;

                case SubpatternSyntax subpattern
                    when PropertyPatternName(subpattern) is { } name && candidates.ContainsKey(name):
                    consumed.Add(name);
                    break;
            }
        }
    }

    /// <summary>
    /// The config field a subpattern reads — its OUTER segment: <c>{ Name: … }</c> → Name;
    /// extended <c>{ Field.Sub: … }</c> → Field (the field, not the inner member <c>Sub</c>).
    /// </summary>
    private static string? PropertyPatternName(SubpatternSyntax subpattern) =>
        subpattern.ExpressionColon?.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => LeftmostName(memberAccess),
            _ => null,
        };

    /// <summary>The leftmost identifier of a dotted access (<c>Field.Sub.Deep</c> → Field), or null.</summary>
    private static string? LeftmostName(MemberAccessExpressionSyntax memberAccess)
    {
        ExpressionSyntax expression = memberAccess;
        while (expression is MemberAccessExpressionSyntax inner)
            expression = inner.Expression;
        return expression is IdentifierNameSyntax id ? id.Identifier.Text : null;
    }

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLinePosition(position).Line + 1;
}
