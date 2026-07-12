# Design-time preview data for the queue cards, and componentize QueuePanel under the size guard

## Problem

Opening `src/VisualRelay.App/Views/Controls/QueuePanel.axaml` in the Rider/Avalonia previewer renders garbage, and there is no sustainable way to see the task cards while iterating on colors:

1. **The previewer has no DataContext, so every binding fails in the worst way.** `QueuePanel.axaml` declares `x:DataType="vm:MainWindowViewModel"` but no design-time DataContext. With no source, `ItemsSource="{Binding Tasks}"` yields no items (no cards at all), and every failed `IsVisible` binding leaves the control at its default `IsVisible="True"` — so the "Initialize this project" card, the "This project's .relay/config.json could not be read" diagnostic box, and the HuggingFace-gate footer all render **simultaneously**, stacked. That is binding fallout, not any real app state.
2. **`MainWindow.axaml` has a design DataContext, but a useless one.** Its `<Design.DataContext><vm:MainWindowViewModel/></Design.DataContext>` element constructs a bare view model: `Tasks` is empty, so the whole-window preview shows an empty queue.
3. **Card state visuals cannot be mocked from XAML.** Every state-dependent color on a card is computed in `TaskRowViewModel` (`src/VisualRelay.App/ViewModels/TaskRowViewModel.cs`) from `IsRunning`/`IsSelected`/`NeedsReview`/`IsArchived` — the `Brush.Parse` statics `SelectedBrush`, `RunningBrush`, `ReviewBrush`, `WaitingCardBrush`, `SelectedCardBrush`, `RunningCardBrush`, the border brushes, and the `BoxShadows` statics, exposed through computed properties (`AccentBrush`, `RailBrush`, `CardBackgroundBrush`, `SelectedHighlightBorderBrush`, `CardBorderBrush`, `CardShadow`, `ProgressFraction`). The only way to preview real card states is real `TaskRowViewModel` instances put into those states — which is cheap, since the constructor takes only an in-memory `RelayTaskItem` record (`src/VisualRelay.Domain/RelayTaskItem.cs`) and the state mutators (`MarkRunning`, `RecordStageCompleted`, `IsSelected`, `RunningElapsedLabel`) are public or internal-to-App.
4. **`QueuePanel.axaml` is at 284 lines against a hard 300-line guard.** `FileSizeGuard` (`tools/VisualRelay.Guards/FileSizeGuard.cs`, `DefaultLimit = 300`, scans `.cs` **and** `.axaml`, runs in `./visual-relay check`) leaves no headroom for the design-time attributes this task adds, let alone future styling work. Two self-contained regions carry most of the bulk: the card `DataTemplate` body (~82 lines) and the two bottom-footer `Border`s (~83 lines).

## Fix

One direction, in this order: add a design-data class, extract the card template into `TaskCard.axaml`, extract the footer into `QueueFooter.axaml`, wire `Design.*` attributes everywhere, and update the two static-scan test classes that parse `QueuePanel.axaml` by path. Design properties are previewer-only — the XAML compiler strips `Design.*` assignments outside the designer — so none of this changes runtime behavior or cost.

### 1. New file: `src/VisualRelay.App/DesignTime/DesignData.cs`

A static class holding pre-populated view models for `Design.DataContext`. This is the single place future preview states get added — fiddling with colors never requires touching C# again.

```csharp
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.App.DesignTime;

/// <summary>
/// Populated view models for the Avalonia previewer (Design.DataContext).
/// Never constructed at runtime: the XAML compiler strips Design.* property
/// assignments outside the designer.
/// </summary>
public static class DesignData
{
    /// <summary>Richest single card: running, selected, mid-progress.</summary>
    public static TaskRowViewModel Card { get; }

    /// <summary>Queue/main-window context with one row per card state.</summary>
    public static MainWindowViewModel Main { get; }

    static DesignData()
    {
        Card = new TaskRowViewModel(NewItem("03-survive-display-sleep-at-launch", completedStages: 8));
        Card.MarkRunning(9, "Fix");
        Card.RunningElapsedLabel = "14m 12s";
        for (var stage = 1; stage <= 8; stage++)
        {
            Card.RecordStageCompleted(stage);
        }
        Card.IsSelected = true;

        Main = new MainWindowViewModel();
        Main.Tasks.Add(Card);
        Main.Tasks.Add(new TaskRowViewModel(NewItem("04-cost-panel-concrete-models")));
        Main.Tasks.Add(new TaskRowViewModel(NewItem("02-fix-task-author-real-evidence",
            reviewReason: "Stage 08 visual-review skipped: renderer unavailable",
            costUsd: 0.34, durationSeconds: 512, completedStages: 12)));
        Main.Tasks.Add(new TaskRowViewModel(NewItem("01-replace-target-env-base-not-overlay",
            costUsd: 0.06, durationSeconds: 138, completedStages: 5)));
        Main.Tasks.Add(new TaskRowViewModel(NewItem("00-bootstrap-project",
            archived: true, costUsd: 0.42, durationSeconds: 947, completedStages: 12))
        { DayHeader = "Today ($0.42)" });
        // Deliberately NOT setting Main.SelectedTask: OnSelectedTaskChanged
        // (MainWindowViewModel.Commands.cs) kicks SelectTaskAsync, which reads
        // the fabricated markdown path from disk and surfaces the failure into
        // StatusText — polluting every preview. Card.IsSelected above already
        // drives all selected-state card visuals.
        Main.StatusText = "Running 03-survive-display-sleep-at-launch — Stage 09 · Fix";
    }

    private static RelayTaskItem NewItem(
        string id, string? reviewReason = null, bool archived = false,
        double costUsd = 0, double durationSeconds = 0, int completedStages = 0)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, "Dev", "acme", "llm-tasks", id);
        return new RelayTaskItem(id, Path.Combine(dir, $"{id}.md"), dir, false, [],
            reviewReason, archived, null, costUsd, durationSeconds, completedStages);
    }
}
```

Notes that must hold:

- `RecordStageCompleted` is `internal`; `DesignData` lives in the same assembly, so this compiles as-is.
- The home-anchored `MarkdownPath` is intentional: `HomePathToTildeConverter` then renders `~/Dev/acme/…` in the preview exactly as in the shipped app.
- `new MainWindowViewModel()` (null environment accessor) is already proven previewer-safe — `MainWindow.axaml`'s current `Design.DataContext` constructs one today.

### 2. Extract the card template into `Views/Controls/TaskCard.axaml` (+ `.axaml.cs`)

Move the **entire content** of the `DataTemplate` in `QueuePanel.axaml`'s `TaskQueueList` — the `<Grid RowDefinitions="Auto,*">` that holds the `DayHeader` TextBlock and the two nested card `Border`s (starting at the `<!-- outer = inner 8 + ring thickness 2 -->` comment) — into a new UserControl, preserving the moved XML byte-for-byte (comments included). Root element:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:VisualRelay.App.ViewModels"
             xmlns:controls="using:VisualRelay.App.Views.Controls"
             xmlns:dt="using:VisualRelay.App.DesignTime"
             x:Class="VisualRelay.App.Views.Controls.TaskCard"
             x:DataType="vm:TaskRowViewModel"
             Design.DataContext="{x:Static dt:DesignData.Card}"
             Design.Width="320">
```

- `xmlns:controls` is required because the moved body references `{x:Static controls:HomePathToTildeConverter.Instance}`.
- The root **must not set `Background`** (and no wrapper element may be added around the moved Grid): `TaskCardRenderTests` asserts every `Border`/`ContentPresenter` between each `ListBoxItem` and its `Border.queueCard` is transparent, and the UserControl's default template (a background-less `ContentPresenter`) satisfies that only if `Background` stays null.
- Code-behind follows the repo's thin-view idiom (`Views/Controls/CommandsView.axaml.cs`): `public partial class TaskCard : UserControl` with only an `InitializeComponent()` constructor.

The `DataTemplate` in `QueuePanel.axaml` becomes exactly:

```xml
<ListBox.ItemTemplate>
  <DataTemplate DataType="{x:Type vm:TaskRowViewModel}">
    <controls:TaskCard/>
  </DataTemplate>
</ListBox.ItemTemplate>
```

DataContext flows from the list item; the drag-reorder code-behind (`QueuePanel.axaml.cs`) is untouched — it hit-tests up to the `ListBoxItem` ancestor and reads its `DataContext`, both of which are unchanged.

### 3. Extract the bottom footer into `Views/Controls/QueueFooter.axaml` (+ `.axaml.cs`)

Move the **two** `Grid.Row="2"` sibling `Border`s at the bottom of `QueuePanel.axaml` — the HF-gate box (`IsVisible="{Binding ShowHfGate}"`, containing `HfGateMessage`, `HfPricingNote`, the "Get a free token →" button) and the status strip (`IsVisible="{Binding !ShowHfGate}"`, containing the `StatusText` TextBlock and the `x:Name="StatusExpandButton"` flyout button) — into a new UserControl. Strip the `Grid.Row="2"` attributes from the moved Borders and wrap them in a single `<Panel>` (they remain mutually exclusive via `ShowHfGate`); preserve everything else byte-for-byte. Root element:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:VisualRelay.App.ViewModels"
             xmlns:buttons="using:VisualRelay.App.Views.Controls.Buttons"
             xmlns:dt="using:VisualRelay.App.DesignTime"
             x:Class="VisualRelay.App.Views.Controls.QueueFooter"
             x:DataType="vm:MainWindowViewModel"
             Design.DataContext="{x:Static dt:DesignData.Main}"
             Design.Width="340">
```

In `QueuePanel.axaml` the two Borders are replaced by exactly `<controls:QueueFooter Grid.Row="2"/>` (the `controls:` xmlns already exists there). Same thin code-behind idiom as TaskCard. `ShowHfGate` is `_keyStatesLoaded && !IsHuggingFaceConfigured` (`MainWindowViewModel.Keys.cs`) — false at design time since keys never load, so the preview shows the status strip, fed by the `StatusText` set in `DesignData`.

### 4. `QueuePanel.axaml` root: design attributes

Add to the root `<UserControl>` element (keeping all existing attributes):

```xml
xmlns:dt="using:VisualRelay.App.DesignTime"
Design.DataContext="{x:Static dt:DesignData.Main}"
Design.Width="340" Design.Height="780"
```

Use Avalonia's `Design.*` attached properties exactly as written — not the Blend `d:`/`mc:Ignorable` namespace pair. After steps 2–4 the file lands around 125 lines. The `ListBox` line carrying `x:Name="TaskQueueList" ItemContainerTheme="{StaticResource TaskCardItemTheme}"` must keep both attributes on that one line — `TaskCardThemeGuardTests` scans for the `x:Name="TaskQueueList"` line and asserts it also contains `ItemContainerTheme`.

### 5. `MainWindow.axaml`: point the existing design context at the populated VM

Replace the three-line `<Design.DataContext><vm:MainWindowViewModel/></Design.DataContext>` element with the attribute form on the `<Window>` root, plus the `xmlns:dt` declaration:

```xml
xmlns:dt="using:VisualRelay.App.DesignTime"
Design.DataContext="{x:Static dt:DesignData.Main}"
```

The whole-window preview then shows the populated queue for free.

### 6. `Styles/VisualRelayTheme.axaml`: preview host for theme fiddling

The card-adjacent styles (`Border.queueCard`, `ListBoxItem:pointerover Border.queueCard`, `Border.panel`, `Border.chip`, `TaskCardItemTheme`, the `drop-above`/`drop-below` styles) live here, and a bare styles file previews as blank. Add `xmlns:controls="using:VisualRelay.App.Views.Controls"` to the `<Styles>` root and, as its first child, a preview host so editing this file renders live cards:

```xml
<Design.PreviewWith>
  <Border Width="360" Height="800" Padding="10" Background="#0C0E12">
    <controls:QueuePanel/>
  </Border>
</Design.PreviewWith>
```

(`#0C0E12` is the real window background from `MainWindow.axaml`. The nested QueuePanel picks up its own `Design.DataContext` because the previewer process runs in design mode globally. The file is at 212 lines; this fits the guard.)

### 7. Required updates to existing tests (they parse QueuePanel.axaml by path)

**`tests/VisualRelay.Tests/ContrastTests.cs`** statically scans `QueuePanel.axaml`; after the split it would fail (`QueuePanel_NamedText_MeetsAaOnItsSurface` pins `Text="{Binding DayHeader}"` and `Text="STATUS"` via `.Single(…)`, and after extraction the on-panel scan's `Assert.NotEmpty` would see zero literals left in QueuePanel.axaml). Extend the scan to all three files so accessibility coverage is preserved, not narrowed:

- Replace `LoadQueuePanel()` with:

  ```csharp
  private static readonly string[] PanelXamlFiles =
      ["QueuePanel.axaml", "TaskCard.axaml", "QueueFooter.axaml"];

  private static IEnumerable<XElement> LoadPanelRoots()
  {
      foreach (var name in PanelXamlFiles)
      {
          var path = Path.Combine(RepoSetup.Root,
              "src", "VisualRelay.App", "Views", "Controls", name);
          Assert.True(File.Exists(path), $"{name} not found: {path}");
          yield return XDocument.Load(path).Root!;
      }
  }

  private static IEnumerable<XElement> PanelTextElements() =>
      LoadPanelRoots().SelectMany(TextElements);
  ```

- Use `PanelTextElements()` at all three former `TextElements(LoadQueuePanel())` call sites (the named-text theory and the two scan enumerators), rename the three `QueuePanel_…` fact/theory methods to `PanelXamls_…`, and update the class/section doc comments to name the three files.
- `ResolveTextSurface` needs **no change** — verify these classifications hold rather than "fixing" them: the `DayHeader` TextBlock in `TaskCard.axaml` walks to the UserControl root with no `Background` attribute and correctly resolves to the panel background (TaskCard renders on the panel); the flyout `STATUS` label and HF-gate texts still resolve to their inline `#1B2028` surface; card-interior text stays excluded because the card Border's `Background="{Binding CardBackgroundBrush}"` is a non-hex attribute (resolves null), exactly as today.

**`tests/VisualRelay.Tests/StatusFooterFlyoutTests.cs`** does `queuePanel.FindControl<CommonButton>("StatusExpandButton")` — after extraction that name lives in `QueueFooter`'s namescope, so `FindControl` on `QueuePanel` returns null. In both facts, resolve the footer first:

```csharp
var footer = window.GetVisualDescendants().OfType<QueueFooter>().Single();
var expandButton = footer.FindControl<CommonButton>("StatusExpandButton");
```

Update the class doc comment (the footer is hosted at QueuePanel's `Grid.Row="2"` but defined in `QueueFooter.axaml`). Keep everything else, including the MainWindow boot these existing tests already use.

## Rejected approaches — do not do these

- Do NOT move the card state brushes out of `TaskRowViewModel`, or any inline hex out of the XAML, into theme resources/pseudo-class styles "while you're in there". The current placement is deliberate (centralized for accessibility and theming, guarded by `ContrastTests` and `TaskCardRenderTests`); this task is preview + split only, with moved XML preserved byte-for-byte.
- Do NOT set `Main.SelectedTask` in `DesignData` (and do not "simplify" `Card.IsSelected = true` away in favor of it): `OnSelectedTaskChanged` fires `SelectTaskAsync`, which reads the markdown path from disk and writes the failure into `StatusText`, corrupting every preview. Row-level `IsSelected` drives all selected-state visuals.
- Do NOT add HotAvalonia or any other preview/hot-reload package — no new dependencies of any kind.
- Do NOT use the Blend `xmlns:d`/`mc:Ignorable="d"` design namespaces in the new files; use Avalonia's `Design.DataContext`/`Design.Width`/`Design.Height` attached properties.
- Do NOT extract the "Initialize this project" card or the config-diagnostic box: they are already small, `ConfigInitEmptyStateUiTests`/`InitPanelButtonsLayoutTests` locate their named controls (`InitEmptyState`, `InitTestCommandBox`, `CreateConfigButton`) in QueuePanel's namescope, and the two extractions above already put the file ~175 lines under the guard.
- Do NOT rename any `x:Name`d control (`TaskQueueList`, `StatusExpandButton`, `InitEmptyState`, `InitTestCommandBox`, `CreateConfigButton`) or touch the drag-reorder logic in `QueuePanel.axaml.cs`.
- Do NOT split the `DayHeader` block out of `TaskCard` — the day header is part of the item template on purpose (per-row group heading), and `TaskCardRenderTests.DayHeader_IsUntinted_WhenSelected` walks it inside the item.
- Do NOT add the new test file to the fixed file list in `SplitGuardVerificationTests.HeadlessCollectionFiles_HaveCollectionAttribute` — that list is a legacy pin, not a registry.

## Tests

New file `tests/VisualRelay.Tests/DesignDataTests.cs` (plain `[Fact]`s, no Avalonia session — pure view-model assertions):

- `Main_CoversEveryCardState` — in `DesignData.Main.Tasks`: exactly one row `IsRunning`; at least one `NeedsReview && !IsRunning`; at least one `IsArchived`; at least one `Task.CompletedStageCount == 0 && !IsRunning` (the "No run history" card); at least one non-empty `DayHeader`; exactly one `IsSelected`.
- `Main_LeavesSelectedTaskNull_SoPreviewsDoNoDiskIo` — `Assert.Null(DesignData.Main.SelectedTask)`, with a comment naming the `SelectTaskAsync` disk-read reason. This is the guardrail that keeps the rejected approach out.
- `Card_IsRunningSelectedMidProgress` — `DesignData.Card.IsRunning`, `.IsSelected`, `Assert.InRange(Card.ProgressFraction, 0.01, 0.99)`, and `MetricsLine` contains `"Stage 09 · Fix"` (proves the running-stage label plumbing renders in previews).

New file `tests/VisualRelay.Tests/QueuePanelSplitRenderTests.cs` (`[Collection("Headless")]`, `public sealed class`, `[AvaloniaFact]`s) — the machine-checkable proxy for "the previewer can render these controls standalone". Host each control in a bare `Window` (`window.Show()` + `Dispatcher.UIThread.RunJobs()`, per the scoped-control rule in `AGENTS.md` — do NOT boot `MainWindow` here):

- `TaskCard_RendersStandalone_FromDesignData` — `new Window { Content = new TaskCard { DataContext = DesignData.Card }, Width = 340, Height = 200 }`; after layout, a descendant `Border` with class `queueCard` exists, and the single descendant `ProgressBar`'s `Value` equals `DesignData.Card.ProgressFraction` (double-precision overload).
- `QueueFooter_HostsExpandButton_InOwnNameScope` — host `new QueueFooter { DataContext = DesignData.Main }` the same way; `footer.FindControl<CommonButton>("StatusExpandButton")` is non-null and carries a `Flyout`.

Existing suites that must pass **unmodified**: `TaskCardRenderTests` (the transparency-chain and CornerRadius facts prove the extraction is visually inert), `TaskCardThemeGuardTests`, `ConfigInitEmptyStateUiTests`, `InitPanelButtonsLayoutTests`. `ContrastTests` and `StatusFooterFlyoutTests` change only as prescribed above.

## Constraints

- `./visual-relay check` must pass end-to-end — in particular the file-size guard: every touched or created `.cs`/`.axaml` file stays ≤ 300 lines (expected: QueuePanel.axaml ~125, TaskCard.axaml ~95, QueueFooter.axaml ~95, DesignData.cs ~80, thin code-behinds ~12 each).
- No new NuGet packages; no changes outside the files named in this spec plus the listed tests.
- The moved XAML is byte-identical to the original except for the stripped `Grid.Row="2"` attributes and the new UserControl roots — no attribute reordering, no re-indentation of moved content, no color or spacing changes anywhere.
- Manual acceptance (post-merge, human): opening `TaskCard.axaml`, `QueuePanel.axaml`, `QueueFooter.axaml`, or `VisualRelayTheme.axaml` in the Rider previewer shows populated, styled content with no init/diagnostic boxes stacked over the cards.
- If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove coverage to satisfy it.
