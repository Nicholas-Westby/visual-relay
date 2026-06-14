using System;
using System.ComponentModel;
using Avalonia.Controls;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Views.Controls;

public partial class ActivityColumn : UserControl
{
    private MainWindowViewModel? _vm;

    public ActivityColumn()
    {
        InitializeComponent();
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
            ApplyRowSplit();
        }
        else
        {
            _vm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRunLogCollapsed) ||
            e.PropertyName == nameof(MainWindowViewModel.IsLlmCommandsCollapsed))
        {
            ApplyRowSplit();
        }
    }

    private void ApplyRowSplit()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (ActivityInnerGrid is null)
            return;

        var runLogCollapsed = vm.IsRunLogCollapsed;
        var llmCommandsCollapsed = vm.IsLlmCommandsCollapsed;

        if (runLogCollapsed && llmCommandsCollapsed)
        {
            ActivityInnerGrid.RowDefinitions[0].Height = Avalonia.Controls.GridLength.Auto;
            ActivityInnerGrid.RowDefinitions[1].Height = Avalonia.Controls.GridLength.Auto;
        }
        else if (runLogCollapsed)
        {
            ActivityInnerGrid.RowDefinitions[0].Height = Avalonia.Controls.GridLength.Auto;
            ActivityInnerGrid.RowDefinitions[1].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
        }
        else if (llmCommandsCollapsed)
        {
            ActivityInnerGrid.RowDefinitions[0].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
            ActivityInnerGrid.RowDefinitions[1].Height = Avalonia.Controls.GridLength.Auto;
        }
        else
        {
            ActivityInnerGrid.RowDefinitions[0].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
            ActivityInnerGrid.RowDefinitions[1].Height = new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);
        }
    }
}
