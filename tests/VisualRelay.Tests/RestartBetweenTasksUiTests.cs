using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
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

    // ---- Contrast / foreground tests for the dropdown -------------------

    [AvaloniaFact]
    public void CollapsedSelectionBox_NameForeground_IsIntendedColor()
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

        // The collapsed selection box shows only the protocol name via
        // SelectionBoxItemTemplate.  Find that TextBlock.
        var selectionText = combo!.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Text == "Standard");
        Assert.NotNull(selectionText);

        // Must be the intended palette colour #F2F5FA (TopBarTextBrush).
        var fg = selectionText!.Foreground as SolidColorBrush;
        Assert.NotNull(fg);
        Assert.Equal(Color.Parse("#F2F5FA"), fg!.Color);
    }

    [AvaloniaFact]
    public void CollapsedSelectionBox_NameContrast_MeetsAA()
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

        var selectionText = combo!.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Text == "Standard");
        Assert.NotNull(selectionText);

        var fgBrush = selectionText!.Foreground as SolidColorBrush;
        Assert.NotNull(fgBrush);
        var fgHex = $"#{fgBrush!.Color.R:X2}{fgBrush.Color.G:X2}{fgBrush.Color.B:X2}";

        // Walk up to find the nearest ancestor Border with a Background.
        var bgHex = ResolveAncestorBackground(selectionText);

        var ratio = ContrastTests.ContrastRatio(fgHex, bgHex);
        Assert.True(ratio >= ContrastTests.AaNormal,
            $"Collapsed name {fgHex} on {bgHex}: {ratio:F2}:1 < {ContrastTests.AaNormal}:1");
    }

    [AvaloniaFact]
    public void PopupItems_NameAndDescriptionForeground_Resolve()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        var topBar = new TopBar { DataContext = vm, Width = 1440 };
        var window = new Window { Content = topBar, Width = 1440, Height = 400 };
        window.Show();

        var combo = topBar.GetVisualDescendants().OfType<ComboBox>()
            .FirstOrDefault(c => c.Name == "RunAllModeCombo");
        Assert.NotNull(combo);
        Assert.NotNull(combo!.ItemTemplate);

        // The ItemTemplate DataTemplate sets Foreground via
        // {DynamicResource <key>} on two TextBlocks.  Verify the
        // keys resolve against the running app.  Before the fix the
        // unreachable ThemeForegroundBrush returns false; after the
        // fix TopBarTextBrush + TopBarTextMutedBrush return true.
        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextBrush", null, out var nameBrush), "TopBarTextBrush must resolve");
        Assert.IsType<SolidColorBrush>(nameBrush);

        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextMutedBrush", null, out var descBrush), "TopBarTextMutedBrush must resolve");
        Assert.IsType<SolidColorBrush>(descBrush);
    }

    [AvaloniaFact]
    public void PopupItems_UnselectedRows_HaveIdenticalForegrounds()
    {
        // All popup rows share one ItemTemplate whose TextBlocks use
        // the same DynamicResource keys → every row resolves to the
        // identical brush object.  Verify that by resolving each key
        // once and confirming they are stable SolidColorBrushes.
        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextBrush", null, out var nameBrush), "TopBarTextBrush must resolve");
        Assert.IsType<SolidColorBrush>(nameBrush);

        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextMutedBrush", null, out var descBrush), "TopBarTextMutedBrush must resolve");
        Assert.IsType<SolidColorBrush>(descBrush);

        // Same resource key → same brush object → all rows identical.
        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextBrush", null, out var nameBrush2));
        Assert.Same(nameBrush, nameBrush2);
    }

    [AvaloniaFact]
    public void PopupItems_NameAndDescription_Contrast_MeetsAA()
    {
        // Resolve the actual palette brushes from the running app.
        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextBrush", null, out var nameBrushObj),
            "TopBarTextBrush must resolve");
        Assert.True(Application.Current!.TryGetResource(
            "TopBarTextMutedBrush", null, out var descBrushObj),
            "TopBarTextMutedBrush must resolve");

        var nameBrush = Assert.IsType<SolidColorBrush>(nameBrushObj);
        var descBrush = Assert.IsType<SolidColorBrush>(descBrushObj);
        var nameFg = $"#{nameBrush.Color.R:X2}{nameBrush.Color.G:X2}{nameBrush.Color.B:X2}";
        var descFg = $"#{descBrush.Color.R:X2}{descBrush.Color.G:X2}{descBrush.Color.B:X2}";

        // Fluent Dark ComboBox/ComboBoxItem backgrounds.
        var states = new (string Label, string Background)[]
        {
            ("rest",             "#1F1F1F"), // flyout default
            ("pointerover",      "#2D2D2D"), // Fluent pointerover overlay
            ("selected",         "#2B2B2B"), // selected accent
            ("selected+pointer", "#333333"), // combined
        };

        foreach (var (label, bg) in states)
        {
            var nameRatio = ContrastTests.ContrastRatio(nameFg, bg);
            Assert.True(nameRatio >= ContrastTests.AaNormal,
                $"Name {nameFg} on {bg} ({label}): {nameRatio:F2}:1 < {ContrastTests.AaNormal}:1");

            var descRatio = ContrastTests.ContrastRatio(descFg, bg);
            Assert.True(descRatio >= ContrastTests.AaNormal,
                $"Description {descFg} on {bg} ({label}): {descRatio:F2}:1 < {ContrastTests.AaNormal}:1");
        }
    }

    /// <summary>
    /// Walk the visual ancestor chain for the nearest Border with a
    /// non-transparent Background and return it as a #RRGGBB hex string.
    /// Falls back to the top-bar background #101218.
    /// </summary>
    private static string ResolveAncestorBackground(Avalonia.Visual leaf)
    {
        for (var el = leaf.GetVisualParent(); el is not null; el = el.GetVisualParent())
        {
            if (el is Border border
                && border.Background is SolidColorBrush bg
                && bg.Color.A == 255)
            {
                return $"#{bg.Color.R:X2}{bg.Color.G:X2}{bg.Color.B:X2}";
            }

            if (el is Panel panel
                && panel.Background is SolidColorBrush pbg
                && pbg.Color.A == 255)
            {
                return $"#{pbg.Color.R:X2}{pbg.Color.G:X2}{pbg.Color.B:X2}";
            }
        }

        return "#101218";
    }
}
