using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed partial class BackendConfigGeneratorTests
{
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
