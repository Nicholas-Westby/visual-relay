using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed partial class ActivityColumnTabsUiTests
{
    /// <summary>
    /// The System tab (index 2) used to carry a "stageDivider" class that drew a
    /// left border, splitting the run-scoped tabs from the stage-scoped tabs with
    /// an awkward vertical line. The five tabs should read as one even row, so the
    /// System tab must have no left border and must not carry the stageDivider class.
    /// </summary>
    [AvaloniaFact]
    public void SystemTab_HasNoDividerBorderOrClass()
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        var tabControl = activityColumn.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault();
        Assert.NotNull(tabControl);

        var systemTab = Assert.IsType<TabItem>(tabControl.Items[2]);
        Assert.Equal("System", systemTab.Header?.ToString());

        Assert.DoesNotContain("stageDivider", systemTab.Classes);
        Assert.Equal(new Thickness(0), systemTab.BorderThickness);
    }
}
