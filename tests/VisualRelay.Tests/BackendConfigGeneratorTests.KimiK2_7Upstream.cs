namespace VisualRelay.Tests;

public sealed partial class BackendConfigGeneratorTests
{
    // ── Kimi K2.7 Code upstream model id ─────────────────────────────────

    /// <summary>
    /// Extracts the upstream <c>model:</c> value for a given
    /// <c>model_name:</c> from the <c>model_list:</c> section of a
    /// litellm-config YAML string.
    /// </summary>
    private static string? ParseUpstreamModel(string yaml, string modelName)
    {
        var lines = yaml.Split('\n');
        string? currentModel = null;
        var inModelList = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line == "model_list:") { inModelList = true; continue; }
            if (!inModelList) continue;

            // Exit on a top-level key after model_list.
            if (line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith('#'))
                break;

            // Each model entry starts with "  - model_name: <name>".
            if (line.StartsWith("  - model_name: "))
            {
                currentModel = line["  - model_name: ".Length..].Trim();
                continue;
            }

            // model: lives inside litellm_params (6-space indent).
            if (currentModel == modelName)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("model: "))
                {
                    return trimmed["model: ".Length..].Trim();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The kimi-k2 alias in the litellm-config template must point at the
    /// Kimi K2.7 Code upstream model (moonshot/kimi-k2.7-code, released
    /// 2026-06-12), not the older K2.6.
    /// </summary>
    [Fact]
    public void KimiK2_UpstreamModel_IsKimiK2_7Code()
    {
        var yaml = File.ReadAllText(TemplatePath);
        var upstream = ParseUpstreamModel(yaml, "kimi-k2");

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
        var (yaml, _) = Generate(present);

        Assert.Contains("moonshot/kimi-k2.7-code", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// No reference to the old <c>kimi-k2.6</c> model id may remain in the
    /// litellm-config template after the upgrade to K2.7 Code.
    /// </summary>
    [Fact]
    public void KimiK2_Template_DoesNotContainK2_6()
    {
        var yaml = File.ReadAllText(TemplatePath);

        Assert.DoesNotContain("kimi-k2.6", yaml, StringComparison.Ordinal);
    }
}
