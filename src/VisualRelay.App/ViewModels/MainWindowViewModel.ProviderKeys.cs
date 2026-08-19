using CommunityToolkit.Mvvm.ComponentModel;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>Immutable descriptor for a provider-key row.</summary>
    public sealed record ProviderKeyRow(
        string DisplayName,
        string EnvVarName,
        string GetKeyUrl);

    /// <summary>
    /// Canonical provider list in display order. Z.AI sits directly below
    /// Hugging Face because it now backs the frontier tier's primary model
    /// (GLM 5.3); without it the frontier chain falls through to GLM 5.2 over
    /// Hugging Face, so both are worth having.
    /// </summary>
    public static readonly IReadOnlyList<ProviderKeyRow> AllProviderKeys =
    [
        new("Hugging Face (Recommended)", "HF_TOKEN", "https://huggingface.co/settings/tokens"),
        new("Z.AI (Recommended)", "ZAI_API_KEY", "https://z.ai/manage-apikey/apikey-list"),
        new("DeepSeek (Recommended)", "DEEPSEEK_API_KEY", "https://platform.deepseek.com/api_keys"),
        new("Moonshot (Recommended)", "MOONSHOT_API_KEY", "https://platform.moonshot.ai/console/api-keys"),
        new("Anthropic (Expensive)", "ANTHROPIC_API_KEY", "https://console.anthropic.com/settings/keys"),
        new("OpenAI (Expensive)", "OPENAI_API_KEY", "https://platform.openai.com/api-keys"),
    ];

    /// <summary>
    /// Per-row observable state for a single provider key.
    /// Exposed as a nested class so the DataTemplate can bind directly.
    /// </summary>
    public sealed partial class ProviderKeyState : ObservableObject
    {
        public ProviderKeyRow Row { get; }

        [ObservableProperty]
        private bool _isSet;

        [ObservableProperty]
        private string _displayValue = string.Empty;

        /// <summary>Password-masked value typed/pasted by the user before saving.</summary>
        [ObservableProperty]
        private string _pendingValue = string.Empty;

        public ProviderKeyState(ProviderKeyRow row, bool isSet, string displayValue)
        {
            Row = row;
            _isSet = isSet;
            _displayValue = displayValue;
        }
    }
}
