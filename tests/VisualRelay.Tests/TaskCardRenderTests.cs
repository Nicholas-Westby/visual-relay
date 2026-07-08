using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Headless visual-tree assertions for the round-task-cards-in-every-state
/// task: chrome-free items, radius invariants, day-header isolation, all
/// encoded as machine-checkable tree walks (D1–D5).
/// </summary>
[Collection("Headless")]
public sealed class TaskCardRenderTests
{
    private static readonly IBrush Transparent = Brushes.Transparent;

    /// <summary>D1/D2 — In the default state, no element between each
    /// ListBoxItem root and its Border.queueCard paints a non-transparent
    /// Background (Fluent chrome must not paint square highlights).</summary>
    [AvaloniaFact]
    public void ListBoxItems_HaveNoChromeBackground_InDefaultState()
    {
        var (window, _) = CreateWindowWithTasks();
        var items = GetTaskListBoxItems(window);
        Assert.True(items.Count >= 3, $"expected >=3 ListBoxItems, got {items.Count}");
        foreach (var item in items)
        {
            var card = FindQueueCard(item);
            Assert.NotNull(card);
            foreach (var a in AncestorsUpTo(card, item))
                AssertBackgroundTransparent(a, "ListBoxItem ancestor has non-transparent Background");
        }
    }

    /// <summary>D2 — Forced :pointerover and :pressed pseudo-classes must not
    /// cause any element between item root and queueCard to paint a
    /// non-transparent Background.</summary>
    [AvaloniaFact]
    public void ListBoxItems_HaveNoChromeBackground_UnderForcedPseudoStates()
    {
        var (window, vm) = CreateWindowWithTasks();
        var items = GetTaskListBoxItems(window);
        Assert.True(items.Count >= 2, $"expected >=2 ListBoxItems, got {items.Count}");
        var testItem = items.First(i => !ReferenceEquals(i.DataContext, vm.SelectedTask));
        var classes = (IPseudoClasses)testItem.Classes;
        classes.Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();
        var card = FindQueueCard(testItem);
        Assert.NotNull(card);
        var ancestors = AncestorsUpTo(card, testItem);
        foreach (var a in ancestors)
            AssertBackgroundTransparent(a, "ListBoxItem ancestor painted Background under :pointerover");
        classes.Set(":pressed", true);
        Dispatcher.UIThread.RunJobs();
        foreach (var a in ancestors)
            AssertBackgroundTransparent(a, "ListBoxItem ancestor painted Background under :pressed");
    }

    /// <summary>D1 — Genuine ListBox selection must create no non-transparent
    /// Background between item root and queueCard; the selection highlight is
    /// the card's blue outer ring.</summary>
    [AvaloniaFact]
    public void ListBoxItems_HaveNoChromeBackground_WhenSelected()
    {
        var (window, vm) = CreateWindowWithTasks();
        var selected = vm.SelectedTask;
        Assert.NotNull(selected);
        var items = GetTaskListBoxItems(window);
        var selectedItem = items.FirstOrDefault(i => ReferenceEquals(i.DataContext, selected));
        Assert.NotNull(selectedItem);
        var card = FindQueueCard(selectedItem);
        Assert.NotNull(card);
        foreach (var a in AncestorsUpTo(card, selectedItem))
            AssertBackgroundTransparent(a, "selected ListBoxItem ancestor has non-transparent Background");
    }

    /// <summary>D5 — When a task row with a DayHeader is selected, the header
    /// TextBlock's ancestors up to the item root still paint no background.</summary>
    [AvaloniaFact]
    public void DayHeader_IsUntinted_WhenSelected()
    {
        var (window, vm) = CreateWindowWithTasks();
        var items = GetTaskListBoxItems(window);
        var dayItem = items.FirstOrDefault(i =>
            i.DataContext is TaskRowViewModel tr && !string.IsNullOrEmpty(tr.DayHeader));
        Assert.NotNull(dayItem);
        var dayRow = (TaskRowViewModel)dayItem.DataContext!;
        vm.SelectedTask = dayRow;
        Dispatcher.UIThread.RunJobs();
        var headerText = dayItem.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => !string.IsNullOrEmpty(tb.Text) && tb.Text!.Contains("Today"));
        Assert.NotNull(headerText);
        foreach (var a in AncestorsUpTo(headerText, dayItem))
            AssertBackgroundTransparent(a, "DayHeader ancestor has non-transparent Background when selected");
    }

    /// <summary>D3 — The outer highlight Border's CornerRadius must equal the
    /// inner card's CornerRadius plus the outer ring's BorderThickness.</summary>
    [AvaloniaFact]
    public void CornerRadius_OuterRingEqualsCardPlusRingThickness()
    {
        var (window, vm) = CreateWindowWithTasks();
        var selected = vm.SelectedTask;
        Assert.NotNull(selected);
        var items = GetTaskListBoxItems(window);
        var selectedItem = items.First(i => ReferenceEquals(i.DataContext, selected));
        var card = FindQueueCard(selectedItem);
        Assert.NotNull(card);
        var outerRing = card.Parent as Border;
        Assert.NotNull(outerRing);
        Assert.Equal(card.CornerRadius.TopLeft + outerRing.BorderThickness.Left,
                     outerRing.CornerRadius.TopLeft);
    }

    /// <summary>D3,D4 — Pins the exact CornerRadius literals: outer ring 10,
    /// inner card 8, selection rail 7,0,0,7.</summary>
    [AvaloniaFact]
    public void CornerRadius_LiteralsMatchSpec()
    {
        var (window, _) = CreateWindowWithTasks();
        var items = GetTaskListBoxItems(window);
        Assert.NotEmpty(items);
        var card = FindQueueCard(items[0]);
        Assert.NotNull(card);
        var outerRing = card.Parent as Border;
        Assert.NotNull(outerRing);
        Assert.Equal(new CornerRadius(10), outerRing.CornerRadius);
        Assert.Equal(new CornerRadius(8), card.CornerRadius);
        var rail = card.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("selectionRail"));
        Assert.NotNull(rail);
        Assert.Equal(new CornerRadius(7, 0, 0, 7), rail.CornerRadius);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates a headless MainWindow with tasks covering all four
    /// card states plus one item with a DayHeader.</summary>
    private static (MainWindow Window, MainWindowViewModel Vm) CreateWindowWithTasks()
    {
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() };
        var vm = new MainWindowViewModel(env);
        var runningTask = new TaskRowViewModel(NewTask("running-card"));
        runningTask.MarkRunning();
        var combinedTask = new TaskRowViewModel(NewTask("combined-card"));
        combinedTask.MarkRunning();
        var dayHeaderTask = new TaskRowViewModel(NewTask("day-header-card"))
        { DayHeader = "Today ($1.04)" };
        vm.Tasks.Add(new TaskRowViewModel(NewTask("default-card")));
        vm.Tasks.Add(runningTask);
        vm.Tasks.Add(combinedTask);
        vm.Tasks.Add(dayHeaderTask);
        vm.SelectedTask = combinedTask;
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static List<ListBoxItem> GetTaskListBoxItems(MainWindow window)
    {
        var queuePanel = window.GetVisualDescendants().OfType<QueuePanel>().Single();
        var listBox = queuePanel.FindControl<ListBox>("TaskQueueList");
        Assert.NotNull(listBox);
        return listBox.GetVisualDescendants().OfType<ListBoxItem>().ToList();
    }

    private static Border? FindQueueCard(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("queueCard"));

    /// <summary>Asserts that the Background of visual is either null or a
    /// transparent solid-colour brush. Works on Border and ContentPresenter;
    /// Fluent's ListBoxItem template applies pseudo-class fills to the
    /// ContentPresenter, not to a Border.</summary>
    private static void AssertBackgroundTransparent(Visual visual, string message)
    {
        IBrush? bg = visual switch
        {
            Border b => b.Background,
            ContentPresenter cp => cp.Background,
            _ => null
        };
        if (visual is not Border && visual is not ContentPresenter) return;
        if (bg is null) return;
        if (bg == Transparent) return;
        if (bg is ISolidColorBrush solid && solid.Color.A == 0) return;
        var desc = visual is Border b2
            ? $"Border [{(b2.Classes.Count > 0 ? string.Join(' ', b2.Classes) : "<no classes>")}]"
            : $"{visual.GetType().Name}";
        Assert.Fail($"{message}. {desc} has Background={bg}");
    }

    /// <summary>Returns every visual ancestor of descendant up to (but not
    /// including) root.</summary>
    private static List<Visual> AncestorsUpTo(Visual descendant, Visual root)
    {
        var result = new List<Visual>();
        var current = descendant.GetVisualParent();
        while (current is not null && !ReferenceEquals(current, root))
        {
            result.Add(current);
            current = current.GetVisualParent();
        }
        return result;
    }

    private static RelayTaskItem NewTask(string id) =>
        new(id, $"/tmp/{id}.md", "/tmp", false, [], CompletedStageCount: 0);
}
