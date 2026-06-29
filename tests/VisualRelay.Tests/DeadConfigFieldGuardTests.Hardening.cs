using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Regression tests for the DeadConfigFieldGuard hardening fixes — the consumer/candidate
/// shapes that previously caused a false positive or a coverage hole: null-conditional
/// reads, by-tier dictionary-merge self-defaults, extended property patterns, and the
/// tools/ consumer scan. Partial sibling of <see cref="DeadConfigFieldGuardTests"/>,
/// split to keep each file within the 300-line guard.
/// </summary>
public sealed partial class DeadConfigFieldGuardTests
{
    /// <summary>
    /// A field whose ONLY consumer is a null-conditional read (<c>c?.Knob</c>, which
    /// compiles to a <c>MemberBindingExpression</c>, not a <c>MemberAccess</c>) — or a
    /// chained one (<c>h?.Cfg?.Deep</c>) — is NOT reported. This mirrors the live
    /// <c>configResult?.Config?.TasksDir</c> shape; without binding-read coverage the
    /// guard would false-flag a field read only through <c>?.</c>.
    /// </summary>
    [Fact]
    public void ConfigField_ConsumedViaNullConditionalRead_IsNotReported()
    {
        const string record = "public sealed record Cfg(int Knob, int Deep);";
        const string loader = """
            public static class CfgLoader
            {
                public static Cfg Load(Cfg defaults, JsonElement root) =>
                    defaults with
                    {
                        Knob = OptionalInt(root, "knob", defaults.Knob),
                        Deep = OptionalInt(root, "deep", defaults.Deep),
                    };
            }
            """;
        // Knob is read only via a direct `c?.Knob`; Deep only via chained `h?.Cfg?.Deep`.
        const string consumer = """
            public class Engine
            {
                public int? A(Cfg c) => c?.Knob;
                public int? B(Holder h) => h?.Cfg?.Deep;
            }
            """;

        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", record), ("CfgLoader.cs", loader), ("Engine.cs", consumer)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Coverage hole: a field seeded via a <c>new Dictionary&lt;…&gt;(defaults.Field)</c>
    /// self-default — the merge shape VR's loader uses for its by-tier dictionaries,
    /// where the self-default is a CONSTRUCTOR argument (inline or via a local), not
    /// <c>OptionalX</c>'s last argument — IS a candidate. <c>Dead</c> (consumed nowhere)
    /// is reported; <c>Live</c> (read via <c>c.Live</c>) is not. Before generalising
    /// candidate detection these fields fell through BOTH this guard and InspectCode.
    /// </summary>
    [Fact]
    public void ConfigField_SeededViaDictionaryMergeSelfDefault_IsReportedOnlyWhenDead()
    {
        const string record =
            "public sealed record Cfg(IReadOnlyDictionary<string,int> Live, IReadOnlyDictionary<string,int> Dead);";
        const string loader = """
            public static class CfgLoader
            {
                public static Cfg Load(Cfg defaults, JsonElement root)
                {
                    var dead = new Dictionary<string,int>(defaults.Dead);
                    return defaults with
                    {
                        Live = new Dictionary<string,int>(defaults.Live),
                        Dead = dead,
                    };
                }
            }
            """;
        const string consumer = "public class Engine { public int Run(Cfg c) => c.Live.Count; }";

        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", record), ("CfgLoader.cs", loader), ("Engine.cs", consumer)]);

        var v = Assert.Single(violations);
        Assert.Equal("Dead", v.Field);
    }

    /// <summary>
    /// A field consumed via an EXTENDED property pattern (<c>c is { Knob.Length: &gt; 0 }</c>)
    /// is NOT reported: the OUTER segment (<c>Knob</c>, the config field) is the consumer,
    /// not the inner member (<c>Length</c>). Without this the guard would false-flag the
    /// idiom by keying on the inner name.
    /// </summary>
    [Fact]
    public void ConfigField_ConsumedViaExtendedPropertyPattern_IsNotReported()
    {
        const string record = "public sealed record Cfg(string Knob);";
        const string loader = """
            public static class CfgLoader
            {
                public static Cfg Load(Cfg defaults, JsonElement root) =>
                    defaults with { Knob = OptionalString(root, "knob", defaults.Knob) };
            }
            """;
        const string consumer = "public class Engine { public bool Hot(Cfg c) => c is { Knob.Length: > 0 }; }";

        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", record), ("CfgLoader.cs", loader), ("Engine.cs", consumer)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A config field consumed ONLY in a tools/ file (e.g. a CLI-display knob, product
    /// code outside src/) is NOT reported. Candidates come from the src set (where the
    /// loader lives); consumers are counted across BOTH src and tools. With its consumer
    /// only in the src set the field is flagged dead; adding the tools/ file to the
    /// consumer set clears it.
    /// </summary>
    [Fact]
    public void ConfigField_ConsumedOnlyInToolsSet_IsNotReported()
    {
        const string record = "public sealed record Cfg(int Knob);";
        const string loader = """
            public static class CfgLoader
            {
                public static Cfg Load(Cfg defaults, JsonElement root) =>
                    defaults with { Knob = OptionalInt(root, "knob", defaults.Knob) };
            }
            """;
        const string toolsConsumer = "public class Cli { public int Show(Cfg c) => c.Knob; }";

        (string Path, string Source)[] src = [("src/Cfg.cs", record), ("src/CfgLoader.cs", loader)];

        // Candidate present, no consumer in the src set → dead.
        var dead = Assert.Single(DeadConfigFieldGuard.FindViolations(src));
        Assert.Equal("Knob", dead.Field);

        // Same src candidates, but the consumer lives in the tools/ consumer set → live.
        var violations = DeadConfigFieldGuard.FindViolations(
            candidateFiles: src,
            consumerFiles: [.. src, ("tools/Cli.cs", toolsConsumer)]);
        Assert.Empty(violations);
    }
}
