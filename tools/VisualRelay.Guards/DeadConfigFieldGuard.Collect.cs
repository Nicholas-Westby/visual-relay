using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace VisualRelay.Guards;

/// <summary>
/// Declaration / candidate / consumer collection passes for <see cref="DeadConfigFieldGuard"/>.
/// Split from the public surface to keep each file within the 300-line guard; see that
/// file's doc-comment for the detection rule, scanned file set, and bias rationale.
/// </summary>
public static partial class DeadConfigFieldGuard
{
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
