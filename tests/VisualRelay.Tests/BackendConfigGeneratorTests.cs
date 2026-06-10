using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class BackendConfigGeneratorTests
{
    private static string TemplatePath =>
        Path.Combine(RepoSetup.Root, "tools", "backend", "litellm-config.yaml");

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Dictionary<string, string> GeneratedAliases(ISet<string> keys)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath);
        return ParseAliases(yaml);
    }

    private static Dictionary<string, List<string>> GeneratedFallbacks(ISet<string> keys)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath);
        return ParseFallbacks(yaml);
    }

    private static (string Yaml, string Summary) Generate(ISet<string> keys) =>
        BackendConfigGenerator.Generate(keys, TemplatePath);

    /// Extracts tier→model from the model_group_alias: block.
    private static Dictionary<string, string> ParseAliases(string yaml)
    {
        var result = new Dictionary<string, string>();
        var inBlock = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == "  model_group_alias:") { inBlock = true; continue; }
            if (!inBlock) continue;
            if (line.Length > 0 && !line.StartsWith("    ")) break;
            var t = line.TrimStart();
            if (t.Length == 0) continue;
            var colon = t.IndexOf(':');
            if (colon < 0) continue;
            var key = t[..colon].Trim();
            var value = t[(colon + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0) result[key] = value;
        }
        return result;
    }

    /// Extracts tier→[models] from the fallbacks: block.
    private static Dictionary<string, List<string>> ParseFallbacks(string yaml)
    {
        var result = new Dictionary<string, List<string>>();
        var inBlock = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == "  fallbacks:") { inBlock = true; continue; }
            if (!inBlock) continue;
            if (line.Length > 0 && !line.StartsWith("    ") && !line.StartsWith("  ")) break;
            var t = line.TrimStart();
            if (t.Length == 0 || !t.StartsWith("- ")) continue;
            var inner = t[2..];
            var colon = inner.IndexOf(':');
            if (colon < 0) continue;
            var key = inner[..colon].Trim();
            var rest = inner[(colon + 1)..].Trim();
            if (rest.StartsWith('[') && rest.EndsWith(']'))
            {
                result[key] = rest[1..^1].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
        }
        return result;
    }

    private static bool ChainTerminatesInFallback(string tier, Dictionary<string, List<string>> fb)
        => fb.TryGetValue(tier, out var c) && c.Count > 0 && c[^1] == "fallback";

    // ── 1. HF only ───────────────────────────────────────────────────────

    [Fact]
    public void HfOnly_DefaultTiersResolveToFallbackFloor()
    {
        var present = new HashSet<string> { "HF_TOKEN" };
        var (yaml, summary) = Generate(present);
        var aliases = ParseAliases(yaml);

        Assert.Equal("fallback", aliases["cheap-kimi"]);
        Assert.Equal("fallback", aliases["balanced-kimi"]);
        Assert.Equal("fallback", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);
        Assert.Equal("hf-qwen3-coder-next", aliases["fallback"]);
        Assert.False(aliases.ContainsKey("claude"));

        // No absent-key model appears as a primary.
        Assert.DoesNotContain("kimi-k2", aliases.Values);
        Assert.DoesNotContain("deepseek-v4-pro", aliases.Values);
        Assert.DoesNotContain("deepseek-v4-flash", aliases.Values);

        Assert.Contains("HF_TOKEN", summary, StringComparison.Ordinal);
    }

    // ── 2. HF + DeepSeek ─────────────────────────────────────────────────

    [Fact]
    public void HfPlusDeepSeek_CheapFlash_BalancedPro_FrontierPro()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var aliases = GeneratedAliases(present);
        var fallbacks = GeneratedFallbacks(present);

        Assert.Equal("deepseek-v4-flash", aliases["cheap-kimi"]);
        Assert.Equal("deepseek-v4-pro", aliases["balanced-kimi"]);
        Assert.Equal("deepseek-v4-pro", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);
        Assert.Equal("hf-qwen3-coder-next", aliases["fallback"]);
        Assert.False(aliases.ContainsKey("claude"));
        Assert.DoesNotContain("kimi-k2", aliases.Values);

        foreach (var tier in new[] { "cheap-kimi", "balanced-kimi", "frontier", "vision" })
            Assert.True(ChainTerminatesInFallback(tier, fallbacks),
                $"fallback chain for {tier} should terminate in fallback");
    }

    // ── 3. Trio: HF + DeepSeek + Moonshot ────────────────────────────────

    [Fact]
    public void Trio_FrontierKimi_ChainTerminatesInFallback()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY" };
        var aliases = GeneratedAliases(present);
        var fallbacks = GeneratedFallbacks(present);

        Assert.Equal("deepseek-v4-flash", aliases["cheap-kimi"]);
        Assert.Equal("deepseek-v4-pro", aliases["balanced-kimi"]);
        Assert.Equal("kimi-k2", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);

        Assert.True(fallbacks.ContainsKey("frontier"));
        var chain = fallbacks["frontier"];
        Assert.Contains("deepseek-v4-pro", chain);
        Assert.Contains("hf-qwen3-coder-next", chain);
        Assert.Equal("fallback", chain[^1]);

        foreach (var tier in new[] { "cheap-kimi", "balanced-kimi", "frontier", "vision" })
            Assert.True(ChainTerminatesInFallback(tier, fallbacks));
    }

    // ── 4. HF + Anthropic ────────────────────────────────────────────────

    [Fact]
    public void HfPlusAnthropic_ClaudeLit_OtherTiersFallback()
    {
        var present = new HashSet<string> { "HF_TOKEN", "ANTHROPIC_API_KEY" };
        var aliases = GeneratedAliases(present);
        var fallbacks = GeneratedFallbacks(present);

        Assert.True(aliases.ContainsKey("claude"));
        Assert.Equal("claude-opus-1m", aliases["claude"]);
        Assert.True(fallbacks.ContainsKey("claude"));
        Assert.Contains("claude-sonnet", fallbacks["claude"]);
        Assert.DoesNotContain("fallback", fallbacks["claude"]);

        Assert.Equal("fallback", aliases["cheap-kimi"]);
        Assert.Equal("fallback", aliases["balanced-kimi"]);
        Assert.Equal("fallback", aliases["frontier"]);
        Assert.Equal("hf-qwen3-vl-235b", aliases["vision"]);
    }

    // ── 5. Shape guard ───────────────────────────────────────────────────

    [Fact]
    public void ShapeGuard_ParsesAndEveryTierHasNonEmptyChainEndingInFallback()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var (yaml, _) = Generate(present);
        var aliases = ParseAliases(yaml);
        var fallbacks = ParseFallbacks(yaml);

        Assert.Contains("model_group_alias:", yaml, StringComparison.Ordinal);
        Assert.Contains("fallbacks:", yaml, StringComparison.Ordinal);
        // model_list and litellm_settings preserved verbatim.
        Assert.Contains("kimi-k2", yaml, StringComparison.Ordinal);
        Assert.Contains("drop_params: true", yaml, StringComparison.Ordinal);
        Assert.Contains("json_logs: true", yaml, StringComparison.Ordinal);
        Assert.Contains("stream_timeout:", yaml, StringComparison.Ordinal);
        Assert.Contains("request_timeout:", yaml, StringComparison.Ordinal);

        foreach (var tier in new[] { "cheap-kimi", "balanced-kimi", "frontier", "vision", "fallback" })
        {
            Assert.True(aliases.ContainsKey(tier), $"tier '{tier}' must have an alias");
            Assert.False(string.IsNullOrWhiteSpace(aliases[tier]),
                $"alias for '{tier}' must be non-empty");
            Assert.True(ChainTerminatesInFallback(tier, fallbacks),
                $"fallback chain for {tier} should terminate in fallback");
        }
    }

    // ── 6. Summary line ──────────────────────────────────────────────────

    [Fact]
    public void Summary_MentionsDetectedKeysAndResolution()
    {
        var present = new HashSet<string> { "HF_TOKEN", "DEEPSEEK_API_KEY" };
        var (_, summary) = Generate(present);

        Assert.Contains("HF_TOKEN", summary, StringComparison.Ordinal);
        Assert.Contains("DEEPSEEK_API_KEY", summary, StringComparison.Ordinal);
        Assert.Contains("cheap", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frontier", summary, StringComparison.OrdinalIgnoreCase);
        // When Anthropic absent, summary notes claude is absent.
        Assert.Contains("claude", summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── 7. Degenerate: no keys at all ────────────────────────────────────

    [Fact]
    public void EmptyKeySet_DoesNotCrash_AndEveryTierHasAlias()
    {
        var present = new HashSet<string>();
        var (yaml, _) = Generate(present);
        var aliases = ParseAliases(yaml);

        foreach (var tier in new[] { "cheap-kimi", "balanced-kimi", "frontier", "vision" })
            Assert.True(aliases.ContainsKey(tier),
                $"tier '{tier}' must have an alias even with no keys");
        Assert.False(aliases.ContainsKey("claude"));
    }

    // ── 8. Per-model hard-timeout ceilings ───────────────────────────────

    /// <summary>
    /// Extracts model_name → timeout (seconds) from the <c>model_list:</c>
    /// section. Models without a <c>timeout:</c> key are absent from the
    /// result. Stops scanning at the first top-level key after model_list
    /// (e.g. <c>router_settings:</c>).
    /// </summary>
    private static Dictionary<string, int> ParseModelTimeouts(string yaml)
    {
        var result = new Dictionary<string, int>();
        var lines = yaml.Split('\n');
        string? currentModel = null;
        var inModelList = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            if (line == "model_list:") { inModelList = true; continue; }
            if (!inModelList) continue;

            // Exit on a top-level (non-indented) key — router_settings:,
            // litellm_settings:, or anything else at column 0 that isn't a
            // comment or blank line.
            if (line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith('#'))
                break;

            // Each model entry starts with "  - model_name: <name>".
            if (line.StartsWith("  - model_name: "))
            {
                currentModel = line["  - model_name: ".Length..].Trim();
                continue;
            }

            // timeout: lives inside litellm_params (6-space indent).
            if (currentModel != null)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("timeout: "))
                {
                    var val = trimmed["timeout: ".Length..].Trim();
                    if (int.TryParse(val, out var seconds))
                        result[currentModel] = seconds;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Every model in the template must carry an explicit per-model
    /// <c>timeout:</c> so no request can hang indefinitely on a byte-0
    /// upstream stall (the global request_timeout alone proved insufficient
    /// for the 2026-06-10 wedge).
    /// </summary>
    [Fact]
    public void PerModelTimeout_AllNineModelsHaveExplicitCeiling()
    {
        var yaml = File.ReadAllText(TemplatePath);
        var timeouts = ParseModelTimeouts(yaml);

        string[] allModels =
        [
            "kimi-k2",
            "deepseek-v4-pro",
            "deepseek-v4-flash",
            "hf-qwen3-coder-next",
            "hf-qwen3-vl-235b",
            "hf-qwen3-vl-30b",
            "claude-opus-1m",
            "claude-sonnet",
            "gpt-5",
        ];

        foreach (var model in allModels)
            Assert.True(timeouts.ContainsKey(model),
                $"model '{model}' must have an explicit per-model timeout");
    }

    [Fact]
    public void PerModelTimeout_FrontierKimiK2Has480s()
    {
        var yaml = File.ReadAllText(TemplatePath);
        var timeouts = ParseModelTimeouts(yaml);

        Assert.True(timeouts.TryGetValue("kimi-k2", out var t),
            "kimi-k2 must have a per-model timeout");
        // 480s (8 min) > 412s observed worst-case healthy Review,
        // far below the 40-min stage cap.
        Assert.Equal(480, t);
    }

    [Fact]
    public void PerModelTimeout_DeepSeekModelsKeep75s()
    {
        var yaml = File.ReadAllText(TemplatePath);
        var timeouts = ParseModelTimeouts(yaml);

        // 75s is below the relay's first-output watchdog (90s/120s) so a
        // hung DeepSeek call hits this Timeout first → fallback engages.
        Assert.Equal(75, timeouts["deepseek-v4-pro"]);
        Assert.Equal(75, timeouts["deepseek-v4-flash"]);
    }

    [Fact]
    public void PerModelTimeout_HfModelsHave120s()
    {
        var yaml = File.ReadAllText(TemplatePath);
        var timeouts = ParseModelTimeouts(yaml);

        // 120s (2 min) — generous for HF inference; cross-provider floor.
        Assert.Equal(120, timeouts["hf-qwen3-coder-next"]);
        Assert.Equal(120, timeouts["hf-qwen3-vl-235b"]);
        Assert.Equal(120, timeouts["hf-qwen3-vl-30b"]);
    }

    [Fact]
    public void PerModelTimeout_ClaudeAndOpenAiHave300s()
    {
        var yaml = File.ReadAllText(TemplatePath);
        var timeouts = ParseModelTimeouts(yaml);

        // 300s (5 min) matches the global request_timeout; Claude/OpenAI
        // are fast and this ceiling is a safety net, not a latency budget.
        Assert.Equal(300, timeouts["claude-opus-1m"]);
        Assert.Equal(300, timeouts["claude-sonnet"]);
        Assert.Equal(300, timeouts["gpt-5"]);
    }

    /// <summary>
    /// The generator passes <c>model_list</c> through verbatim, so every
    /// per-model timeout in the template must survive into the generated
    /// YAML that the backend actually runs.
    /// </summary>
    [Fact]
    public void PerModelTimeout_GeneratedYamlPreservesAllCeilings()
    {
        var present = new HashSet<string>
        {
            "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY",
            "ANTHROPIC_API_KEY", "OPENAI_API_KEY",
        };
        var (yaml, _) = Generate(present);
        var timeouts = ParseModelTimeouts(yaml);

        Assert.Equal(480, timeouts["kimi-k2"]);
        Assert.Equal(75, timeouts["deepseek-v4-pro"]);
        Assert.Equal(75, timeouts["deepseek-v4-flash"]);
        Assert.Equal(120, timeouts["hf-qwen3-coder-next"]);
        Assert.Equal(120, timeouts["hf-qwen3-vl-235b"]);
        Assert.Equal(120, timeouts["hf-qwen3-vl-30b"]);
        Assert.Equal(300, timeouts["claude-opus-1m"]);
        Assert.Equal(300, timeouts["claude-sonnet"]);
        Assert.Equal(300, timeouts["gpt-5"]);
    }
}
