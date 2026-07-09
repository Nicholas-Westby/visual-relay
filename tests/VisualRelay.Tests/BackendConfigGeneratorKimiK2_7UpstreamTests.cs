namespace VisualRelay.Tests;

// The 'K2_7' segment encodes the Kimi K2.7 model id under test; the underscore
// is a deliberate, meaningful part of the name, not a naming-convention slip.
// ReSharper disable once InconsistentNaming
public sealed class BackendConfigGeneratorKimiK2_7UpstreamTests
{
    // ── Kimi K2.7 Code upstream model id ─────────────────────────────────

    /// <summary>
    /// The kimi-k2 alias in the litellm-config template must point at the
    /// Kimi K2.7 Code upstream model (moonshot/kimi-k2.7-code, released
    /// 2026-06-12), not the older K2.6.
    /// </summary>
    [Fact]
    public void KimiK2_UpstreamModel_IsKimiK2_7Code()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);
        var upstream = BackendConfigGeneratorTestHelpers.ParseUpstreamModel(yaml, "kimi-k2");

        Assert.NotNull(upstream);
        Assert.Equal("moonshot/kimi-k2.7-code", upstream);
    }

    /// <summary>
    /// When MOONSHOT_API_KEY is present, the generated config must carry
    /// <c>moonshot/kimi-k2.7-code</c> as the upstream model.  Because the
    /// generator passes model_list through verbatim, this is coupled to the
    /// template; this test guards against a stale generated config surviving
    /// a template-only edit.
    /// </summary>
    [Fact]
    public void KimiK2_GeneratedConfig_ContainsKimiK2_7Code()
    {
        var present = new HashSet<string> { "HF_TOKEN", "MOONSHOT_API_KEY" };
        var (yaml, _) = BackendConfigGeneratorTestHelpers.Generate(present);

        Assert.Contains("moonshot/kimi-k2.7-code", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// No reference to the old <c>kimi-k2.6</c> model id may remain in the
    /// litellm-config template after the upgrade to K2.7 Code.
    /// </summary>
    [Fact]
    public void KimiK2_Template_DoesNotContainK2_6()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        Assert.DoesNotContain("kimi-k2.6", yaml, StringComparison.Ordinal);
    }
}
