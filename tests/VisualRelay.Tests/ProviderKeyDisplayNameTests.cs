using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Pins the provider-key display labels in <see cref="MainWindowViewModel.AllProviderKeys"/>.
/// The suffix lives in <c>DisplayName</c> (no separate flag/enum/badge), so these
/// assertions guard that contract. Since the premium providers were retired every
/// remaining provider is recommended, and nothing may carry the "(Expensive)" marker.
/// </summary>
public sealed class ProviderKeyDisplayNameTests
{
    private static MainWindowViewModel.ProviderKeyRow Row(string envVar) =>
        MainWindowViewModel.AllProviderKeys.First(r => r.EnvVarName == envVar);

    /// <summary>
    /// Exact values pin the format: single leading space, word in parentheses.
    /// </summary>
    [Fact]
    public void EveryRow_DisplayName_CarriesRecommendedSuffix()
    {
        Assert.Equal("Hugging Face (Recommended)", Row("HF_TOKEN").DisplayName);
        Assert.Equal("Z.AI (Recommended)", Row("ZAI_API_KEY").DisplayName);
        Assert.Equal("DeepSeek (Recommended)", Row("DEEPSEEK_API_KEY").DisplayName);
        Assert.Equal("Moonshot (Recommended)", Row("MOONSHOT_API_KEY").DisplayName);
    }

    /// <summary>
    /// No row may carry the retired "(Expensive)" marker: the premium providers
    /// it labelled are gone, and re-adding one would put the boundary back.
    /// </summary>
    [Fact]
    public void NoRow_IsMarkedExpensive()
    {
        foreach (var row in MainWindowViewModel.AllProviderKeys)
        {
            Assert.EndsWith(" (Recommended)", row.DisplayName, StringComparison.Ordinal);
            Assert.DoesNotContain("Expensive", row.DisplayName, StringComparison.Ordinal);
        }
    }
}
