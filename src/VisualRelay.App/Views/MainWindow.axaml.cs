using System;
using Avalonia.Controls;

namespace VisualRelay.App.Views;

public partial class MainWindow : Window
{
    private const double PreferredWidth = 1440;
    private const double PreferredHeight = 900;

    public MainWindow()
    {
        InitializeComponent();
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
