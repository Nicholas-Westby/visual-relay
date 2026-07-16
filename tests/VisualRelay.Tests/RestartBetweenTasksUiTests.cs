using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views.Controls;
using VisualRelay.Core.Queue;

namespace VisualRelay.Tests;

/// <summary>
/// Headless visual-tree tests for the Restart Between Tasks dropdown UI
/// in TopBar.axaml.  Verifies that the three protocols render with names
/// and descriptions, the collapsed control shows only the name, and
/// accessibility stays intact.
/// </summary>
[Collection("Headless")]
public sealed class RestartBetweenTasksUiTests
{
    [AvaloniaFact]
    public void Dropdown_AllThreeOptions_HaveNameAndDescription()
    {
        var options = MainWindowViewModel.RunAllModeOptions;
        Assert.Equal(3, options.Count);

        // Standard
        Assert.Contains(options, o => o.Mode == RunAllMode.Standard && o.Name == "Standard"
            && o.Description == "Plan all tasks up front, then execute");

        // Sequential
        Assert.Contains(options, o => o.Mode == RunAllMode.Sequential && o.Name == "Sequential"
            && o.Description == "One task at a time, checking for new tasks between");

        // Restart Between Tasks
        Assert.Contains(options, o => o.Mode == RunAllMode.RestartBetweenTasks
            && o.Name == "Restart Between Tasks"
            && o.Description.Contains("rebuilds and relaunches after each committed task"));
    }

    [AvaloniaFact]
    public void Dropdown_CollapsedState_ShowsNameOnly()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };

        var topBar = new TopBar { DataContext = vm, Width = 1440 };
        var window = new Window { Content = topBar, Width = 1440, Height = 100 };
        window.Show();

        var combo = topBar.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.Name == "RunAllModeCombo");
        Assert.NotNull(combo);

        // SelectionBoxItemTemplate must be set so the collapsed control
        // renders only the protocol name (not the description).
        Assert.NotNull(combo.SelectionBoxItemTemplate);

        // ItemTemplate must be set for the expanded popup.
        Assert.NotNull(combo.ItemTemplate);

        // ItemsSource must be the three options.
        var items = combo.ItemsSource?.Cast<MainWindowViewModel.RunAllModeOption>().ToList();
        Assert.NotNull(items);
        Assert.Equal(3, items!.Count);
    }

    [AvaloniaFact]
    public void Dropdown_Accessibility_AndToolTip()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };

        var topBar = new TopBar { DataContext = vm, Width = 1440 };
        var window = new Window { Content = topBar, Width = 1440, Height = 100 };
        window.Show();

        var combo = topBar.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.Name == "RunAllModeCombo");
        Assert.NotNull(combo);

        var name = Avalonia.Automation.AutomationProperties.GetName(combo);
        Assert.Equal("Run All mode", name);

        var tooltip = ToolTip.GetTip(combo);
        Assert.NotNull(tooltip);
        var tipText = tooltip.ToString();
        Assert.Contains("Standard", tipText!, StringComparison.Ordinal);
        Assert.Contains("Sequential", tipText!, StringComparison.Ordinal);
        Assert.Contains("Restart", tipText!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void RunAllModeOptions_CountIsThree()
    {
        Assert.Equal(3, MainWindowViewModel.RunAllModeOptions.Count);
    }
}
