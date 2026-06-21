using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Views;

public partial class MainWindow : Window
{
    private const double PreferredWidth = 1440;
    private const double PreferredHeight = 900;
    private const double MinActivityWidth = 300;
    private MainWindowViewModel? _vm;
    private bool _syncingColumnWidth;

    public MainWindow()
    {
        InitializeComponent();
        ActivitySplitter.AddHandler(Thumb.DragCompletedEvent, OnActivitySplitterDragCompleted);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyCenterSplit();
            SyncActivityColumnWidth(vm);
        }
        else
        {
            _vm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsStagesCollapsed))
        {
            ApplyCenterSplit();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsActivityColumnCollapsed))
        {
            SyncActivityColumnWidth(_vm);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.ActivityColumnWidth) && _vm is not null)
        {
            SyncActivityColumnWidth(_vm);
        }
    }

    private void ApplyCenterSplit()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (CenterGrid is null)
            return;

        // When Stages is collapsed: TASK fills the full center (row 0 = *, row 1 = Auto).
        // When expanded: restore the 1.45:1 ratio (row 0 = 1.45*, row 1 = *).
        if (vm.IsStagesCollapsed)
        {
            CenterGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            CenterGrid.RowDefinitions[1].Height = GridLength.Auto;
        }
        else
        {
            CenterGrid.RowDefinitions[0].Height = new GridLength(1.45, GridUnitType.Star);
            CenterGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        }
    }

    private void SyncActivityColumnWidth(MainWindowViewModel? vm)
    {
        if (vm is null || ContentGrid is null)
            return;

        // Guard against re-entrancy: when the splitter drag updates the VM and
        // the VM notifies us back, we must not fight the splitter.
        if (_syncingColumnWidth)
            return;

        _syncingColumnWidth = true;
        try
        {
            var width = vm.IsActivityColumnCollapsed ? 36 : vm.ActivityColumnWidth;
            ContentGrid.ColumnDefinitions[2].Width = new GridLength(width);
        }
        finally
        {
            _syncingColumnWidth = false;
        }
    }

    private void OnActivitySplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null || ContentGrid is null)
            return;

        var value = ContentGrid.ColumnDefinitions[2].Width.Value;
        if (value < MinActivityWidth)
            value = MinActivityWidth;

        // Clamp to a reasonable maximum relative to the window.
        var maxWidth = Math.Max(MinActivityWidth, Bounds.Width - 400);
        if (value > maxWidth)
            value = maxWidth;

        _vm.ActivityColumnWidth = value;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // An explicit size was requested (e.g. the headless screenshot tool sets
        // Width/Height) — respect it and never auto-resize.
        if (!double.IsNaN(Width) || !double.IsNaN(Height))
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            Width = PreferredWidth;
            Height = PreferredHeight;
            return;
        }

        var scaling = screen.Scaling;
        var workWidth = screen.WorkingArea.Width / scaling;
        var workHeight = screen.WorkingArea.Height / scaling;
        var (width, height) = WindowFit.FitToWorkArea(PreferredWidth, PreferredHeight, workWidth, workHeight);
        Width = width;
        Height = height;
        Position = WindowFit.CenterPosition(screen.WorkingArea, width, height, scaling);
    }
}
