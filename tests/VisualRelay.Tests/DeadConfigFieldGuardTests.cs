using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing dead-config-field guard-as-test (a VR-specific sibling of
/// <see cref="GateAsTestSandboxGuardTests"/>). The matcher is pure: it Roslyn-parses
/// each source and flags a config record property that is PARSED but CONSUMED
/// NOWHERE — i.e. whose only references are the record/property declaration and the
/// <c>defaults.&lt;Name&gt;</c> self-default in the loader's
/// <c>defaults with { Name = OptionalX(root, "key", defaults.Name) }</c> shape.
/// Because that self-default getter access counts as a "read", ReSharper
/// InspectCode's <c>NotAccessedPositionalProperty.Global</c> can NEVER catch a dead
/// config field — hence this dedicated guard. (The regression tests for the
/// consumer/candidate-shape hardening fixes live in the partial sibling
/// <c>DeadConfigFieldGuardTests.Hardening.cs</c>.)
///
/// <para>This is dev-gate infra for VR's OWN repo (it keys on the
/// <c>RelayConfig</c>/<c>RelayConfigLoader</c> self-default loader pattern), not the
/// general-purpose relay engine — so VR-specific knowledge is appropriate here. The
/// live tree currently reports ZERO dead fields (every <c>RelayConfig</c> field has a
/// genuine <c>.Field</c> consumer; <c>MaxStageFailures</c> is now the escalation
/// run-cap), so the synthesized-dead-field tests below are what prove the guard has
/// teeth.</para>
/// </summary>
public sealed partial class DeadConfigFieldGuardTests
{
    // A minimal config record + self-default loader, mirroring RelayConfig /
    // RelayConfigLoader, with two knobs: MaxTurns (given a real consumer below) and
    // DeadKnob (consumed nowhere).
    private const string Record = "public sealed record Cfg(int MaxTurns, int DeadKnob);";

    private const string Loader = """
        public static class CfgLoader
        {
            public static Cfg Load(Cfg defaults, JsonElement root) =>
                defaults with
                {
                    MaxTurns = OptionalInt(root, "maxTurns", defaults.MaxTurns),
                    DeadKnob = OptionalInt(root, "deadKnob", defaults.DeadKnob),
                };
        }
        """;

    /// <summary>
    /// Teeth: a field whose ONLY references are the record declaration and the
    /// <c>defaults.DeadKnob</c> self-default — with no <c>.DeadKnob</c> consumer
    /// anywhere — is reported. The sibling <c>MaxTurns</c> (which a separate file
    /// consumes via <c>c.MaxTurns</c>) is NOT reported: zero false positives.
    /// </summary>
    [Fact]
    public void DeadConfigField_ReferencedOnlyByDeclAndSelfDefault_IsReported()
    {
        const string consumer = "public class Engine { public int Run(Cfg c) => c.MaxTurns; }";

        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", Record), ("CfgLoader.cs", Loader), ("Engine.cs", consumer)]);

        var v = Assert.Single(violations);
        Assert.Equal("DeadKnob", v.Field);
        Assert.Contains("DeadKnob", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The self-default read alone is not a consumer: feeding ONLY the record + the
    /// self-default loader (no consumer file at all) flags every loaded knob, because
    /// each field's lone <c>defaults.Field</c> read is excluded by construction.
    /// </summary>
    [Fact]
    public void SelfDefaultReadAlone_DoesNotCountAsConsumer()
    {
        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", Record), ("CfgLoader.cs", Loader)]);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Field == "MaxTurns");
        Assert.Contains(violations, v => v.Field == "DeadKnob");
    }

    /// <summary>
    /// A field consumed via a property pattern (<c>c is { Knob: &gt; 0 }</c>) — not a
    /// <c>.Field</c> member access — is NOT reported. Property-pattern reads count as
    /// genuine consumers, so the guard never false-flags this idiom.
    /// </summary>
    [Fact]
    public void ConfigField_ConsumedViaPropertyPattern_IsNotReported()
    {
        const string record = "public sealed record Cfg(int Knob);";
        const string loader = """
            public static class CfgLoader
            {
                public static Cfg Load(Cfg defaults, JsonElement root) =>
                    defaults with { Knob = OptionalInt(root, "knob", defaults.Knob) };
            }
            """;
        const string consumer = "public class Engine { public bool Hot(Cfg c) => c is { Knob: > 0 }; }";

        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", record), ("CfgLoader.cs", loader), ("Engine.cs", consumer)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Precision: a field loaded WITHOUT a self-default (e.g.
    /// <c>OptionalStringArray(root, "key")</c>, no <c>defaults.Field</c> fallback) is
    /// not a candidate — it carries no phantom self-read, so InspectCode's own
    /// unused-positional check already covers it. Such a field is never flagged here
    /// even with no consumer, while its self-default sibling still is.
    /// </summary>
    [Fact]
    public void FieldLoadedWithoutSelfDefault_IsNotReported()
    {
        const string record = "public sealed record Cfg(int A, IReadOnlyList<string> B);";
        const string loader = """
            public static class CfgLoader
            {
                public static Cfg Load(Cfg defaults, JsonElement root) =>
                    defaults with
                    {
                        A = OptionalInt(root, "a", defaults.A),
                        B = OptionalStringArray(root, "b"),
                    };
            }
            """;

        var violations = DeadConfigFieldGuard.FindViolations(
            [("Cfg.cs", record), ("CfgLoader.cs", loader)]);

        var v = Assert.Single(violations);
        Assert.Equal("A", v.Field);
    }

    /// <summary>
    /// Precision: an ordinary <c>record with { ... }</c> non-destructive copy that is
    /// NOT the self-default loader shape (its RHS values are not
    /// <c>operand.Field</c> self-defaults) yields no candidates and no violations.
    /// </summary>
    [Fact]
    public void NonLoaderWithExpression_ProducesNoCandidates()
    {
        const string source = """
            public class M
            {
                public Snapshot Touch(Snapshot s) => s with { Status = "Done", Count = s.Count + 1 };
            }
            """;

        var violations = DeadConfigFieldGuard.FindViolations([("M.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// The live enforcing gate: every <c>RelayConfig</c> field that VR's
    /// <c>RelayConfigLoader</c> loads via the <c>defaults.&lt;Field&gt;</c> self-default
    /// (now including the by-tier dictionary-merge fields) has a genuine consumer in
    /// <c>src/</c> or <c>tools/</c> — but NOT counting <c>tests/</c>, since a field used
    /// only by tests is effectively dead in the product. This is the in-suite mirror of the
    /// <c>./visual-relay check</c> step. It currently reports ZERO dead config fields — any
    /// newly-added config knob that is parsed but consumed nowhere flips the guard to a
    /// build failure (consume it, or drop it).
    /// </summary>
    [Fact]
    public void LiveTree_HasNoDeadConfigFields()
    {
        // Candidates from src/ (the config record + loader); consumers from src/ + tools/.
        var candidateFiles = EnumerateCs("src");
        var consumerFiles = EnumerateCs("src", "tools");

        var violations = DeadConfigFieldGuard.FindViolations(candidateFiles, consumerFiles);

        Assert.True(violations.Count == 0,
            "dead-config-field guard found config fields parsed by the loader but consumed " +
            "nowhere in src/ or tools/ (consume the field, or remove it from the config record + loader):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Field} — {v.Reason}")));
    }

    /// <summary>Reads every non-build-artifact <c>*.cs</c> under the given repo-relative dirs as (relPath, source) pairs.</summary>
    private static List<(string Path, string Source)> EnumerateCs(params string[] dirs) =>
        dirs.SelectMany(d =>
                Directory.EnumerateFiles(Path.Combine(RepoSetup.Root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(RepoSetup.Root, f), File.ReadAllText(f)))
            .ToList();

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
