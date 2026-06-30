using CommunityToolkit.Mvvm.ComponentModel;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>Observable per-tier row for the Live Tiers dropdown UI.</summary>
    public sealed partial class TierModelRow : ObservableObject
    {
        [ObservableProperty]
        private string _tier = string.Empty;

        [ObservableProperty]
        private string _selectedModel = string.Empty;

        [ObservableProperty]
        private string _providerName = string.Empty;

        [ObservableProperty]
        private bool _keyPresent;

        [ObservableProperty]
        private bool _isEditable = true;

        [ObservableProperty]
        private IReadOnlyList<string> _selectableModels = [];

        /// <summary>Invoked when <see cref="SelectedModel"/> changes.
        /// The VM wires this to persist the new override.</summary>
        public Func<string, Task>? OnSelectedModelPersist { get; set; }

        partial void OnSelectedModelChanged(string value)
        {
            _ = OnSelectedModelPersist?.Invoke(value);
        }
    }
}
