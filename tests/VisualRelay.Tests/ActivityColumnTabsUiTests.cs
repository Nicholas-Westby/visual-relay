using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

[Collection("Headless")]
public sealed partial class ActivityColumnTabsUiTests
{
    /// <summary>
    /// After the conversion the ActivityColumn hosts a TabControl with five
    /// tabs: Run Log, Commands, System, Input, Output.  Before the conversion
    /// this test fails because no TabControl exists inside the column.
    /// </summary>
    [AvaloniaFact]
    public void TabControl_HasFiveTabs_WithCorrectHeaders()
    {
        var vm = new MainWindowViewModel();
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
        Assert.NotNull(tabControl); // FAILS: no TabControl in old two-panel layout

        Assert.Equal(5, tabControl.ItemCount);

        var headers = new List<string?>();
        for (var i = 0; i < tabControl.ItemCount; i++)
        {
            if (tabControl.Items[i] is TabItem tabItem)
                headers.Add(tabItem.Header?.ToString());
        }

        Assert.Contains("Run Log", headers);
        Assert.Contains("Commands", headers);
        Assert.Contains("System", headers);
        Assert.Contains("Input", headers);
        Assert.Contains("Output", headers);
    }

    /// <summary>
    /// The three stage-scoped tabs (System, Input, Output) sit after Run Log
    /// and Commands with a visual divider between them.  This test verifies
    /// the ordering: Run Log (0), Commands (1), then the three stage tabs.
    /// Fails before conversion because there is no TabControl.
    /// </summary>
    [AvaloniaFact]
    public void StageTabs_AppearAfterCommandsTab()
    {
        var vm = new MainWindowViewModel();
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
        Assert.NotNull(tabControl); // FAILS: no TabControl yet

        // First two tabs are Run Log (0) and Commands (1).
        Assert.True(tabControl.ItemCount >= 5);
        Assert.Equal("Run Log", GetTabHeader(tabControl, 0));
        Assert.Equal("Commands", GetTabHeader(tabControl, 1));

        // The next three are the stage tabs.
        Assert.Equal("System", GetTabHeader(tabControl, 2));
        Assert.Equal("Input", GetTabHeader(tabControl, 3));
        Assert.Equal("Output", GetTabHeader(tabControl, 4));
    }

    /// <summary>
    /// Selecting a tab programmatically updates the SelectedIndex on the
    /// TabControl.  The two-way binding to ActivityTabIndex is verified
    /// in the VM layer.  Fails before conversion because there is no TabControl.
    /// </summary>
    [AvaloniaFact]
    public void SwitchingTabs_UpdatesSelectedIndex()
    {
        var vm = new MainWindowViewModel();
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
        Assert.NotNull(tabControl); // FAILS: no TabControl yet

        // Switch to the Commands tab (index 1).
        tabControl.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, tabControl.SelectedIndex);

        // Switch to the System tab (index 2).
        tabControl.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, tabControl.SelectedIndex);

        // Switch to the Output tab (index 4).
        tabControl.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, tabControl.SelectedIndex);
    }

    /// <summary>
    /// The column header title changes from "RUN LOG" to "ACTIVITY" and the
    /// Reveal button and LogScopeLabel chip remain present.  Fails before
    /// conversion because the header still says "RUN LOG".
    /// </summary>
    [AvaloniaFact]
    public void ColumnHeader_TitleIsActivity_KeepRevealAndScopeLabel()
    {
        var vm = new MainWindowViewModel { LogScopeLabel = "ideate" };

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

        // Header should say "ACTIVITY", not "RUN LOG".
        var allTextBlocks = activityColumn.GetVisualDescendants().OfType<TextBlock>().ToList();
        var headerTitle = allTextBlocks.FirstOrDefault(tb => tb.Text == "ACTIVITY");
        Assert.NotNull(headerTitle); // FAILS: currently says "RUN LOG"

        // The old "RUN LOG" title should NOT be present in the header.
        var oldTitle = allTextBlocks.FirstOrDefault(tb =>
            tb.Text == "RUN LOG" && IsHeaderTitle(tb));
        Assert.Null(oldTitle); // FAILS: "RUN LOG" is still the header

        // Reveal button still present.
        var revealButton = activityColumn.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Content?.ToString() == "Reveal");
        Assert.NotNull(revealButton);

        // LogScopeLabel chip still shows its value.
        var scopeText = allTextBlocks.FirstOrDefault(tb => tb.Text == "ideate");
        Assert.NotNull(scopeText);
    }

    /// <summary>
    /// The activity rail (collapsed state) has a single expand button and
    /// one rotated "ACTIVITY" label instead of the old two-button two-label
    /// rail.  Fails before conversion because the rail still has two buttons
    /// with labels "RUN LOG" and "LLM CMDS".
    /// </summary>
    [AvaloniaFact]
    public void Rail_HasSingleButtonAndActivityLabel()
    {
        // Force the rail visible by collapsing the column.
        var vm = new MainWindowViewModel { IsActivityColumnCollapsed = true };

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsActivityColumnCollapsed);

        // The rail Border is the sibling of ActivityColumn in the same Panel.
        var columnPanel = window.FindControl<ActivityColumn>("ActivityColumn")?.Parent as Panel;
        Assert.NotNull(columnPanel);

        var rails = columnPanel!.Children
            .OfType<Border>()
            .Where(b => b.Classes.Contains("rail"))
            .ToList();
        Assert.NotEmpty(rails);

        // Find the rail whose IsVisible is true.
        var activeRail = rails.FirstOrDefault(r => r.IsVisible);
        Assert.NotNull(activeRail);

        var railButtons = activeRail!.GetVisualDescendants()
            .OfType<Button>()
            .ToList();

        // After conversion: exactly ONE expand button (not two).
        // Before conversion: two buttons (ToggleRunLogCommand + ToggleLlmCommandsCommand).
        Assert.Single(railButtons); // FAILS: currently has 2 buttons

        // The rail label says "ACTIVITY", not "RUN LOG" or "LLM CMDS".
        var railTextBlocks = activeRail.GetVisualDescendants().OfType<TextBlock>().ToList();
        var activityLabel = railTextBlocks.FirstOrDefault(tb => tb.Text == "ACTIVITY");
        Assert.NotNull(activityLabel); // FAILS: currently says "RUN LOG" and "LLM CMDS"

        var oldRunLogLabel = railTextBlocks.FirstOrDefault(tb => tb.Text == "RUN LOG");
        Assert.Null(oldRunLogLabel); // FAILS: old label still present

        var oldLlmLabel = railTextBlocks.FirstOrDefault(tb => tb.Text == "LLM CMDS");
        Assert.Null(oldLlmLabel); // FAILS: old label still present
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? GetTabHeader(TabControl tabControl, int index)
    {
        if (tabControl.Items[index] is TabItem tabItem)
            return tabItem.Header?.ToString();
        return null;
    }

    private static void AssertContainsText(Control root, string expected)
    {
        var allText = root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(tb => tb.IsVisible)
            .Select(tb => tb.Text)
            .Concat(root.GetVisualDescendants()
                .OfType<SelectableTextBlock>()
                .Where(stb => stb.IsVisible)
                .Select(stb => stb.Text))
            .ToList();
        Assert.Contains(allText, t => t != null && t.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// Switches the ActivityColumn's TabControl to <paramref name="tabIndex"/>,
    /// runs the dispatcher, then finds the first descendant of type <typeparamref name="T"/>.
    /// Necessary because TabControl defers creation of non-selected tab content.
    /// </summary>
    private static T SwitchToTabAndFindView<T>(ActivityColumn activityColumn, int tabIndex) where T : Control
    {
        var tabControl = activityColumn.GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault();
        Assert.NotNull(tabControl);
        tabControl!.SelectedIndex = tabIndex;
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(); // Let bindings and layout settle
        var view = activityColumn.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault();
        Assert.NotNull(view);
        return view!;
    }

    private static bool IsHeaderTitle(TextBlock tb)
    {
        return tb.Parent is Panel or Border or Grid
               && tb.Classes.Contains("panelTitle");
    }
}
