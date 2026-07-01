using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Pins the provider-key display labels in <see cref="MainWindowViewModel.AllProviderKeys"/>.
/// The Anthropic and OpenAI keys are comparatively expensive to use, so their
/// <c>DisplayName</c> carries a " (Expensive)" suffix that mirrors the " (Recommended)"
/// suffix baked into the first three providers. The suffix lives in <c>DisplayName</c>
/// (no separate flag/enum/badge), so these assertions guard that contract.
/// </summary>
public sealed class ProviderKeyDisplayNameTests
{
    private static MainWindowViewModel.ProviderKeyRow Row(string envVar) =>
        MainWindowViewModel.AllProviderKeys.First(r => r.EnvVarName == envVar);

    [Fact]
    public void AnthropicAndOpenAiRows_DisplayName_CarryExpensiveSuffix()
    {
        // Exact values also pin the format: single leading space, word in parentheses.
        Assert.Equal("Anthropic (Expensive)", Row("ANTHROPIC_API_KEY").DisplayName);
        Assert.Equal("OpenAI (Expensive)", Row("OPENAI_API_KEY").DisplayName);
        Assert.EndsWith(" (Expensive)", Row("ANTHROPIC_API_KEY").DisplayName, StringComparison.Ordinal);
        Assert.EndsWith(" (Expensive)", Row("OPENAI_API_KEY").DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendedRows_DisplayName_Unchanged_AndNotMarkedExpensive()
    {
        // The three recommended providers keep their parallel " (Recommended)" suffix
        // and must not gain the expensive marker — the change is scoped to two rows.
        foreach (var envVar in new[] { "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY" })
        {
            var row = Row(envVar);
            Assert.EndsWith(" (Recommended)", row.DisplayName, StringComparison.Ordinal);
            Assert.DoesNotContain("Expensive", row.DisplayName, StringComparison.Ordinal);
        }
    }
}
