namespace VisualRelay.Tests;

/// <summary>
/// Guard against accidentally using the legacy HF provider-pinned model-string
/// form (<c>huggingface/&lt;provider&gt;/&lt;org&gt;/&lt;repo&gt;</c>), which
/// routes through the retired
/// <c>https://router.huggingface.co/&lt;provider&gt;/v3/openai/chat/completions</c>
/// endpoint.  The modern form is <c>huggingface/&lt;org&gt;/&lt;repo&gt;:&lt;provider&gt;</c>
/// (suffix-pinned) or <c>huggingface/&lt;org&gt;/&lt;repo&gt;</c> (unpinned auto-route).
/// </summary>
public sealed class BackendConfigGeneratorHfModelStringGuardTests
{
    /// <summary>
    /// Scan the litellm-config template for HF model strings that use the
    /// legacy three-or-more-segment provider-prefix form (no <c>:provider</c>
    /// suffix).  These map to the retired router endpoint and must not exist.
    /// </summary>
    [Fact]
    public void HfModelStrings_NoLegacyProviderPinnedForm()
    {
        var yaml = File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath);

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();

            // Only examine lines that declare a huggingface model string.
            const string prefix = "model: huggingface/";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            // Strip "model: huggingface/" → remainder is e.g.
            //   "novita/Qwen/Qwen3-Coder-480B-A35B-Instruct"      (legacy)
            //   "Qwen/Qwen3-Coder-480B-A35B-Instruct:novita"      (modern suffix-pinned)
            //   "Qwen/Qwen3-VL-235B-A22B-Instruct"                (modern auto-routed)
            var remainder = line[prefix.Length..].Trim();

            var segments = remainder.Split('/');

            // Legacy form has ≥3 segments with no ':' suffix →
            // <provider>/<org>/<repo> mapped to the retired endpoint.
            Assert.False(
                segments.Length >= 3 && !segments.Any(s => s.Contains(':')),
                $"Legacy HF model string detected: '{line}'.  "
                + "Use the modern form '<org>/<repo>:<provider>' for suffix-pinned "
                + "routing, or '<org>/<repo>' for HF auto-routing.");
        }
    }
}
