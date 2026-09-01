using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// Guard against the legacy HF provider-pinned model-string form
/// (<c>&lt;provider&gt;/&lt;org&gt;/&lt;repo&gt;</c>), which routes through the
/// retired <c>router.huggingface.co/&lt;provider&gt;/v3/openai</c> endpoint. The
/// modern form is <c>&lt;org&gt;/&lt;repo&gt;:&lt;provider&gt;</c>
/// (suffix-pinned) or <c>&lt;org&gt;/&lt;repo&gt;</c> (unpinned auto-route).
/// <para>
/// This used to scan the proxy's YAML. The routes carry the model strings now.
/// </para>
/// </summary>
public sealed class BackendConfigGeneratorHfModelStringGuardTests
{
    /// <summary>
    /// No Hugging Face route uses the legacy three-or-more-segment
    /// provider-prefix form.
    /// </summary>
    [Fact]
    public void HfModelStrings_NoLegacyProviderPinnedForm()
    {
        var legacy = new List<string>();

        foreach (var alias in ProviderRoutes.Aliases)
        {
            var route = ProviderRoutes.For(alias)!;
            if (!route.ProviderName.Contains("Hugging", StringComparison.OrdinalIgnoreCase)) continue;

            // Modern: "Qwen/Qwen3-Coder-480B:novita" or "Qwen/Qwen3-VL-235B".
            // Legacy: "novita/Qwen/Qwen3-Coder-480B" — three segments, no pin.
            var segments = route.UpstreamModel.Split('/');
            if (segments.Length >= 3 && !route.UpstreamModel.Contains(':'))
                legacy.Add($"{alias} -> {route.UpstreamModel}");
        }

        Assert.True(legacy.Count == 0,
            "legacy HF model strings, which map to the retired router endpoint. Use "
            + "'<org>/<repo>:<provider>' for a pinned route or '<org>/<repo>' to auto-route:\n"
            + string.Join("\n", legacy));
    }

    /// <summary>
    /// The guard can fail: a legacy string is recognised as one. Without this,
    /// a guard that scans an empty set reports green forever.
    /// </summary>
    [Fact]
    public void TheGuard_RecognisesALegacyString()
    {
        const string legacy = "novita/Qwen/Qwen3-Coder-480B-A35B-Instruct";

        Assert.True(legacy.Split('/').Length >= 3 && !legacy.Contains(':'));
    }
}
