using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
///   <item><b>Candidates</b> — for every <c>operand with { Field = …operand.Field… }</c>
///         where the self-default <c>operand.Field</c> (receiver == the <c>with</c>
///         operand, member == the assigned field) is passed AS A CALL/CTOR ARGUMENT — the
///         <c>OptionalX(…, operand.Field)</c> fallback or the
///         <c>new Dictionary&lt;…&gt;(operand.Field)</c> seed, inline or via a local the
///         assignment aliases — and <c>Field</c> is a real record property (so XAML-bound
///         <c>[ObservableProperty]</c>/VM/DI members, never loaded this way, are
///         structurally out of scope). Requiring a call/ctor argument keeps ordinary
///         self-referential copies (<c>x with { N = x.N + 1 }</c>) out: those are plain
///         reads InspectCode already sees. Every matched <c>operand.Field</c> read is
///         recorded as a self-default to EXCLUDE.</item>
///   <item><b>Consumers</b> — a candidate is "consumed" if ANY consumer source reads it
///         by name: a <c>.Field</c> member access (<c>config.Field</c>) that is NOT the
///         recorded self-default, a null-conditional <c>?.Field</c> binding
///         (<c>config?.Field</c>, chained <c>h?.Cfg?.Field</c>), or a property-pattern
///         read (<c>cfg is { Field: … }</c>, extended <c>{ Field.Sub: … }</c>). The record
///         declaration and the <c>with</c>-initializer write (<c>Field =</c>) are naturally
///         non-consumers.</item>
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
/// <para><b>Scanned file set.</b> CANDIDATES are detected from <c>src/</c> (where the
/// config record + <c>RelayConfigLoader</c> live); a candidate is live if any CONSUMER
/// source reads it, scanned across BOTH <c>src/</c> AND <c>tools/</c> — product code, so a
/// field consumed only by a CLI-display path under <c>tools/</c> still counts and is not
/// false-flagged. <c>tests/</c> is deliberately NOT a consumer source: a config field
/// referenced only by tests drives no product behaviour, so it is effectively dead and
/// should be flagged. bin/obj build output is excluded by the caller. The single-set
/// <see cref="FindViolations(System.Collections.Generic.IEnumerable{System.ValueTuple{string,string}})"/>
/// overload draws candidates and consumers from one set (used by unit tests).</para>
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
public static partial class DeadConfigFieldGuard
{
    /// <summary>A config field parsed by the loader but consumed nowhere in the scanned sources.</summary>
    public sealed record Violation(string Field, string Path, int Line, string Reason);

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Returns every dead config field, ordered by field name (ordinal). Candidates AND
    /// consumers are drawn from the same <paramref name="files"/> set. Empty when every
    /// loaded field has a consumer.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)
    {
        var list = files as IReadOnlyCollection<(string Path, string Source)> ?? files.ToList();
        return FindViolations(list, list);
    }

    /// <summary>
    /// Returns every dead config field, ordered by field name (ordinal). CANDIDATES (the
    /// config record + its loader) come from <paramref name="candidateFiles"/>; a candidate
    /// is live if ANY of <paramref name="consumerFiles"/> reads it. The two sets typically
    /// overlap (consumers ⊇ candidates); each unique path is parsed ONCE so a recorded
    /// self-default node is reference-identical across the candidate and consumer passes.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(
        IEnumerable<(string Path, string Source)> candidateFiles,
        IEnumerable<(string Path, string Source)> consumerFiles)
    {
        var parsed = new Dictionary<string, (SyntaxNode Root, SourceText Text, bool Candidate, bool Consumer)>(
            StringComparer.Ordinal);

        void Register(string path, string source, bool candidate, bool consumer)
        {
            if (parsed.TryGetValue(path, out var p))
                parsed[path] = (p.Root, p.Text, p.Candidate || candidate, p.Consumer || consumer);
            else
            {
                var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
                parsed[path] = (tree.GetRoot(), tree.GetText(), candidate, consumer);
            }
        }

        foreach (var (path, source) in candidateFiles)
            Register(path, source, candidate: true, consumer: false);
        foreach (var (path, source) in consumerFiles)
            Register(path, source, candidate: false, consumer: true);

        // Record properties + loader self-default candidates come from the candidate set;
        // the matched `operand.Field` nodes are recorded so the consumer pass excludes them.
        var declarations = new Dictionary<string, (string Path, int Line)>(StringComparer.Ordinal);
        foreach (var (path, entry) in parsed)
            if (entry.Candidate)
                CollectDeclarations(path, entry.Root, entry.Text, declarations);

        var candidates = new Dictionary<string, (string Path, int Line)>(StringComparer.Ordinal);
        var selfDefaults = new HashSet<SyntaxNode>();
        foreach (var entry in parsed.Values)
            if (entry.Candidate)
                CollectCandidates(entry.Root, declarations, candidates, selfDefaults);

        if (candidates.Count == 0)
            return [];

        // A candidate is live if any consumer source reads it (excluding its self-default).
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in parsed.Values)
            if (entry.Consumer)
                CollectConsumers(entry.Root, candidates, selfDefaults, consumed);

        return candidates
            .Where(c => !consumed.Contains(c.Key))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new Violation(c.Key, c.Value.Path, c.Value.Line,
                $"config field `{c.Key}` is parsed by the loader (defaults.{c.Key} self-default) " +
                "but has no consumer — a dead config field InspectCode cannot see"))
            .ToList();
    }
}
