using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>Immutable descriptor for a provider-key row.</summary>
    public sealed record ProviderKeyRow(
        string DisplayName,
        string EnvVarName,
        string GetKeyUrl);

    /// <summary>Canonical five-provider list in display order.</summary>
    public static readonly IReadOnlyList<ProviderKeyRow> AllProviderKeys =
    [
        new("Hugging Face (Recommended)", "HF_TOKEN", "https://huggingface.co/settings/tokens"),
        new("DeepSeek (Recommended)", "DEEPSEEK_API_KEY", "https://platform.deepseek.com/api_keys"),
        new("Moonshot (Recommended)", "MOONSHOT_API_KEY", "https://platform.moonshot.ai/console/api-keys"),
        new("Anthropic (Expensive)", "ANTHROPIC_API_KEY", "https://console.anthropic.com/settings/keys"),
        new("OpenAI (Expensive)", "OPENAI_API_KEY", "https://platform.openai.com/api-keys"),
    ];

    /// <summary>Observable per-provider state, rebuilt by <see cref="RefreshKeyStatesAsync"/>.</summary>
    public ObservableCollection<ProviderKeyState> KeyStates { get; } = [];

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

    /// <summary>Set when a Run was blocked by the HF gate so pasting a token can resume it.</summary>
    private string? _pendingHfRunTaskId;

    /// <summary>
    /// Set to true after the first call to <see cref="RefreshKeyStatesAsync"/> completes.
    /// Guards the HF-gate banner against a startup flash: <see cref="IsHuggingFaceConfigured"/>
    /// defaults false before key states are known, so without this flag the banner would blink
    /// briefly on every launch even when a token is present.
    /// </summary>
    private bool _keyStatesLoaded;

    /// <summary>
    /// True when HF_TOKEN is present in the user .env or process environment.
    /// Folded into the execution gate; does NOT affect browsing.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DrainQueueCommand))]
    [NotifyPropertyChangedFor(nameof(HfGateMessage))]
    [NotifyPropertyChangedFor(nameof(ShowHfGate))]
    private bool _isHuggingFaceConfigured;

    /// <summary>
    /// True when the HF-gate banner should be shown: key states have been loaded once
    /// (no startup flash) and HF_TOKEN is still absent.
    /// </summary>
    public bool ShowHfGate => _keyStatesLoaded && !IsHuggingFaceConfigured;

    /// <summary>
    /// Human-readable summary of tier→model resolutions given present keys,
    /// produced by <see cref="BackendConfigGenerator.Generate"/>.
    /// Retained for logging/diagnostics; the Live Tiers UI binds to
    /// <see cref="LitTierRows"/> instead.
    /// </summary>
    [ObservableProperty]
    private string? _litTiersSummary;

    /// <summary>
    /// Structured per-tier rows for the Live Tiers UI, populated by
    /// <see cref="RefreshLitTiersAsync"/> from <see cref="BackendConfigGenerator.GetTierRows"/>.
    /// </summary>
    public ObservableCollection<TierModelRow> LitTierRows { get; } = [];

    /// <summary>Remediation message shown when HF_TOKEN is missing.</summary>
    public string HfGateMessage => IsHuggingFaceConfigured
        ? string.Empty
        : "Set a free Hugging Face token to run tasks — open Settings.";

    /// <summary>Honest pay-as-you-go note displayed under the HF row.</summary>
    public string HfPricingNote =>
        "Free to get a token; usage is pay-as-you-go beyond HF's free credit.";

    // ═══════════════════════════════════════════════════════════════════════
    // Commands
    // ═══════════════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task SaveKeyAsync(ProviderKeyState state)
    {
        if (string.IsNullOrWhiteSpace(state.PendingValue))
            return;

        KeyEnvFile.Upsert(state.Row.EnvVarName, state.PendingValue.Trim(), EnvironmentAccessor);
        state.PendingValue = string.Empty;
        await RefreshKeyStatesAsync();

        // Resume a run that was blocked by the HF gate.
        if (_pendingHfRunTaskId is { } pending && IsHuggingFaceConfigured)
        {
            _pendingHfRunTaskId = null;
            var task = Tasks.FirstOrDefault(t => t.Id == pending);
            if (task is not null)
            {
                SelectedTask = task;
                await RunSelectedCommand.ExecuteAsync(null);
            }
        }
    }

    [RelayCommand]
    private async Task OpenGetKeyUrlAsync(string url)
    {
        var psi = new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(psi);
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Refresh
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads <see cref="KeyEnvFile"/> + process env, rebuilds <see cref="KeyStates"/>,
    /// flips <see cref="IsHuggingFaceConfigured"/>, and refreshes <see cref="LitTiersSummary"/>.
    /// </summary>
    public async Task RefreshKeyStatesAsync()
    {
        var fileEnv = KeyEnvFile.Read(EnvironmentAccessor);
        KeyStates.Clear();
        foreach (var row in AllProviderKeys)
        {
            var processVal = KeyEnvFile.GetEnv(row.EnvVarName, EnvironmentAccessor);
            var fileVal = fileEnv.GetValueOrDefault(row.EnvVarName);
            var isSet = !string.IsNullOrWhiteSpace(processVal) || !string.IsNullOrWhiteSpace(fileVal);
            var displayValue = isSet ? MaskValue(processVal ?? fileVal!) : "(not set)";
            KeyStates.Add(new ProviderKeyState(row, isSet, displayValue));
        }

        IsHuggingFaceConfigured = KeyStates.First(s => s.Row.EnvVarName == "HF_TOKEN").IsSet;

        // Mark that at least one load has completed so ShowHfGate is unblocked.
        if (!_keyStatesLoaded)
        {
            _keyStatesLoaded = true;
            OnPropertyChanged(nameof(ShowHfGate));
        }

        await RefreshLitTiersAsync();
    }

    private bool _suppressLitTierPersist;

    private async Task RefreshLitTiersAsync()
    {
        try
        {
            var presentKeys = new HashSet<string>();
            foreach (var state in KeyStates)
            {
                if (state.IsSet)
                    presentKeys.Add(state.Row.EnvVarName);
            }

            // Load tier model overrides from config.
            IReadOnlyDictionary<string, string>? overrides = null;
            if (Directory.Exists(RootPath))
            {
                var configResult = await RelayConfigLoader.TryLoadAsync(RootPath);
                if (configResult.Status == RelayConfigStatus.Loaded)
                    overrides = configResult.Config.TierModelOverrides;
            }

            // Resolve the template the same way the tests do: walk up from the
            // app base directory until we find the repo root, then into tools/backend.
            var templatePath = LocateTemplate();
            if (templatePath is not null && File.Exists(templatePath))
            {
                var (_, summary) = BackendConfigGenerator.Generate(presentKeys, templatePath, overrides);
                LitTiersSummary = summary;
            }
            else
            {
                LitTiersSummary = "(template not found)";
            }

            _suppressLitTierPersist = true;
            LitTierRows.Clear();
            foreach (var row in BackendConfigGenerator.GetTierRows(presentKeys, overrides))
            {
                // SelectedModel = the override from config (if present and valid)
                // else the auto-resolved model.
                var selected = overrides is not null
                    && overrides.TryGetValue(row.Tier, out var ov)
                    && row.SelectableModels.Contains(ov, StringComparer.Ordinal)
                        ? ov
                        : row.Model;

                LitTierRows.Add(new TierModelRow
                {
                    Tier = row.Tier,
                    SelectedModel = selected,
                    ProviderName = row.ProviderName,
                    KeyPresent = row.KeyPresent,
                    IsEditable = row.IsEditable,
                    SelectableModels = row.SelectableModels,
                    OnSelectedModelPersist = PersistTierOverrideAsync,
                });
            }
            _suppressLitTierPersist = false;
        }
        catch
        {
            LitTiersSummary = "(unavailable)";
            LitTierRows.Clear();
        }
    }

    private Task PersistTierOverrideAsync(string _)
    {
        if (_suppressLitTierPersist || !Directory.Exists(RootPath))
            return Task.CompletedTask;

        var overrides = new Dictionary<string, string>();
        foreach (var row in LitTierRows)
        {
            if (!row.IsEditable)
                continue;
            if (string.IsNullOrWhiteSpace(row.SelectedModel))
                continue;
            // Only persist models that are in the tier's selectable list —
            // auto-resolved values like "fallback" or "(key missing)" are
            // transient and must not land in .relay/config.json.
            if (!row.SelectableModels.Contains(row.SelectedModel, StringComparer.Ordinal))
                continue;
            overrides[row.Tier] = row.SelectedModel;
        }

        if (overrides.Count > 0)
            RelayConfigWriter.UpsertTierModelOverrides(RootPath, overrides);
        return Task.CompletedTask;
    }

    private static string? LocateTemplate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "visual-relay")))
            dir = dir.Parent;

        return dir is not null
            ? Path.Combine(dir.FullName, "tools", "backend", "litellm-config.yaml")
            : null;
    }

    private static string MaskValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= 8) return new string('•', value.Length);
        return value[..4] + "…" + value[^4..];
    }
}
