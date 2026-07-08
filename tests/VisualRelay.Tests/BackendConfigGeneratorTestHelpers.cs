using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers extracted from the former BackendConfigGeneratorTests partial
/// class so companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class BackendConfigGeneratorTestHelpers
{
    public static string TemplatePath =>
        Path.Combine(RepoSetup.Root, "tools", "backend", "litellm-config.yaml");

    public static Dictionary<string, string> GeneratedAliases(ISet<string> keys)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath);
        return ParseAliases(yaml);
    }

    public static Dictionary<string, List<string>> GeneratedFallbacks(ISet<string> keys)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath);
        return ParseFallbacks(yaml);
    }

    public static (string Yaml, string Summary) Generate(ISet<string> keys) =>
        BackendConfigGenerator.Generate(keys, TemplatePath);

    public static Dictionary<string, string> GeneratedAliases(
        ISet<string> keys,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath, overrides);
        return ParseAliases(yaml);
    }

    public static Dictionary<string, List<string>> GeneratedFallbacks(
        ISet<string> keys,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var (yaml, _) = BackendConfigGenerator.Generate(keys, TemplatePath, overrides);
        return ParseFallbacks(yaml);
    }

    /// Extracts tier→model from the model_group_alias: block.
    public static Dictionary<string, string> ParseAliases(string yaml)
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
    public static Dictionary<string, List<string>> ParseFallbacks(string yaml)
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

    public static bool ChainTerminatesInFallback(string tier, Dictionary<string, List<string>> fb)
        => fb.TryGetValue(tier, out var c) && c.Count > 0 && c[^1] == "fallback";

    /// <summary>
    /// Extracts the upstream <c>model:</c> value for a given
    /// <c>model_name:</c> from the <c>model_list:</c> section of a
    /// litellm-config YAML string.
    /// </summary>
    public static string? ParseUpstreamModel(string yaml, string modelName)
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
    /// Extracts model_name → timeout (seconds) from the <c>model_list:</c>
    /// section. Models without a <c>timeout:</c> key are absent from the
    /// result. Stops scanning at the first top-level key after model_list
    /// (e.g. <c>router_settings:</c>).
    /// </summary>
    public static Dictionary<string, int> ParseModelTimeouts(string yaml)
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
    /// Extracts <c>model = "…"</c> values from a swival.toml profile string,
    /// keyed by profile name (e.g. <c>"balanced"</c> → <c>"balanced"</c>).
    /// </summary>
    public static Dictionary<string, string> ParseSwivalProfileModelValues(string toml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentProfile = null;

        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("[profiles.", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                currentProfile = line["[profiles.".Length..^1];
            }
            else if (currentProfile != null && line.StartsWith("model = \"", StringComparison.Ordinal))
            {
                var closeQuote = line.IndexOf('"', "model = \"".Length);
                if (closeQuote >= 0)
                {
                    var model = line["model = \"".Length..closeQuote];
                    result[currentProfile] = model;
                }

                currentProfile = null; // consume the model line
            }
        }

        return result;
    }
}
