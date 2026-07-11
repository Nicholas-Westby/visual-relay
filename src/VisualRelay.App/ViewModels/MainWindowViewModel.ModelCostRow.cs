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
        private string _displayName = string.Empty;

        [ObservableProperty]
        private double _inputRate;

        [ObservableProperty]
        private double _outputRate;

        [ObservableProperty]
        private double? _cachedInputRate;

        [ObservableProperty]
        private double? _cacheWriteRate;

        [ObservableProperty]
        private string _cacheWriteDisplay = string.Empty;

        [ObservableProperty]
        private bool _hasWindows;

        public ObservableCollection<ModelCostWindowRow> Windows { get; } = [];
    }

    public sealed partial class ModelCostWindowRow : ObservableObject
    {
        [ObservableProperty]
        private string _startTimeDisplay = string.Empty;

        [ObservableProperty]
        private string _endTimeDisplay = string.Empty;

        [ObservableProperty]
        private string _sourceTimezoneLabel = string.Empty;

        [ObservableProperty]
        private string _displayTimezoneLabel = string.Empty;

        [ObservableProperty]
        private double _multiplier;

        [ObservableProperty]
        private double _peakInputRate;

        [ObservableProperty]
        private double _peakOutputRate;

        [ObservableProperty]
        private double _peakCachedInputRate;

        [ObservableProperty]
        private double _peakCacheWriteRate;

        [ObservableProperty]
        private string _peakCacheWriteDisplay = string.Empty;
    }
}
