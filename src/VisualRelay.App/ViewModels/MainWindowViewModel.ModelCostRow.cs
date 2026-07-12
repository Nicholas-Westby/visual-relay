using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    public sealed partial class ModelCostRow : ObservableObject
    {
        [ObservableProperty]
        private string _modelKey = string.Empty;

        [ObservableProperty]
        private bool _isActive;

        [ObservableProperty]
        private bool _isPriced;

        [ObservableProperty]
        private string _inputDisplay = string.Empty;

        [ObservableProperty]
        private string _outputDisplay = string.Empty;

        [ObservableProperty]
        private string _cachedInputDisplay = string.Empty;

        [ObservableProperty]
        private string _cacheWriteDisplay = string.Empty;

        [ObservableProperty]
        private bool _hasWindows;

        public ObservableCollection<string> TierBadges { get; } = [];
        public ObservableCollection<ModelCostWindowRow> Windows { get; } = [];
    }

    public sealed partial class ModelCostWindowRow : ObservableObject
    {
        [ObservableProperty]
        private string _headline = string.Empty;

        [ObservableProperty]
        private string _sourceNote = string.Empty;

        [ObservableProperty]
        private string _peakInputDisplay = string.Empty;

        [ObservableProperty]
        private string _peakOutputDisplay = string.Empty;

        [ObservableProperty]
        private string _peakCachedInputDisplay = string.Empty;

        [ObservableProperty]
        private string _peakCacheWriteDisplay = string.Empty;
    }
}
