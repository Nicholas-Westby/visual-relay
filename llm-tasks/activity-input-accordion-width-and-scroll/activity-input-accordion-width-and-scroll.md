# Activity Input/Output accordions are ragged-width and their last item's bottom is unreachable

In the ACTIVITY column's **Input** tab (and the **Output** tab, which mirrors it), the stage prompt
is rendered as a vertical list of collapsible accordion sections — Avalonia `Expander` items such as
"Header", "Task input", "Prior stages", "Output contract" (the section titles come from
`AssembledPromptParser.Parse`, `src/VisualRelay.App/Services/AssembledPromptParser.cs:14-23` /
`:78-100`). Two layout defects, both reported with a screenshot:

1. **Width misalignment.** Some accordion items do **not** stretch to the full width of the panel —
   the ones whose header + body content is narrower than the column render narrow, so they look
   ragged/misaligned next to the full-width ones.
2. **Cannot scroll to the bottom.** The user cannot scroll down far enough to read the bottom of the
   **last** accordion item — its tail content is clipped / unreachable.

## Context (researched — verify by searching for the quoted markup, not by trusting line numbers)

The accordions render identically in two sibling views; both are bound to `StageDetailViewModel`
(`src/VisualRelay.App/ViewModels/StageDetailViewModel.cs`) and hosted as tabs of `ActivityColumn`
(`src/VisualRelay.App/Views/Controls/ActivityColumn.axaml:47-64`, the `Input` and `Output`
`TabItem`s, indices 3 and 4). They were built by the completed task
`stage-visibility-3-activity-column-tabs-and-rendering`.

- **Input tab** — `src/VisualRelay.App/Views/Controls/StageInputView.axaml:84-122`. The
  "Ready: parsed input sections" branch is:

  ```xml
  <ScrollViewer Grid.Row="2"
                Padding="8"
                HorizontalScrollBarVisibility="Disabled"
                IsVisible="{Binding StageDetail.IsInputReadyAndNotRawText}">
    <ItemsControl ItemsSource="{Binding StageDetail.InputSections}">
      <ItemsControl.ItemTemplate>
        <DataTemplate DataType="{x:Type svc:PromptSection}">
          <Border Margin="4" BorderBrush="#2A303A" BorderThickness="1" CornerRadius="6">
            <Expander Header="{Binding Title}"
                      IsExpanded="{Binding CollapsedByDefault, Converter={x:Static cvt:BoolNotConverter.Instance}}"
                      Padding="8,4">
              ...
  ```

- **Output tab** — `src/VisualRelay.App/Views/Controls/StageOutputView.axaml:84-136`. Same
  `ScrollViewer Padding="8"` → `ItemsControl` over `StageDetail.OutputFields`, each item a
  `Border Margin="4"`. (Output's items are plain `Border`s today, not `Expander`s — but they share
  the *same* ScrollViewer / ItemsControl shell and the *same* width and scroll behaviour, so the fix
  applies to both.)

### Root cause — defect 1 (ragged width)

`ItemsControl`'s default `ItemsPanel` is a vertical `StackPanel`. A vertical `StackPanel` offers each
child the full cross-axis (horizontal) width, but it does **not force** the child to take it — a
child with `HorizontalAlignment` left unset and a smaller desired width arranges to its own desired
width. The accordion items set **no** `HorizontalAlignment`:

- `StageInputView.axaml:91` — `<Border Margin="4" .../>` (no `HorizontalAlignment`).
- `StageInputView.axaml:95-97` — `<Expander Header="..." Padding="8,4">` (no `HorizontalAlignment`,
  no `HorizontalContentAlignment`).
- `StageOutputView.axaml:91` — `<Border Margin="4" Padding="12" .../>` (no `HorizontalAlignment`).

There is **no `Expander` (or `ItemsControl`/`ContentPresenter`) style** in the app theme
(`src/VisualRelay.App/Styles/VisualRelayTheme.axaml` has selectors for `Button`, `ListBox`,
`ListBoxItem`, `Border.*`, `TextBlock.*`, `SelectableTextBlock.*` — grep it: no `Expander`), so these
controls fall through to Avalonia 12's default Fluent template, which does not stretch the item to the
panel. Net effect: items whose content is narrower than the panel render narrow → the ragged look.
The fix is to make the item container stretch to the panel width (`HorizontalAlignment="Stretch"` on
the item `Border`, and `HorizontalAlignment="Stretch"` + `HorizontalContentAlignment="Stretch"` on
the `Expander` so its body fills too).

### Root cause — defect 2 (last item's bottom unreachable)

The `ScrollViewer` is the only scrolling container (vertical, `HorizontalScrollBarVisibility="Disabled"`)
and there is **no fixed/`MaxHeight` anywhere** on these views or on `ActivityColumn` (grep confirms:
the only `*Height` hits are `LineHeight`/`MinHeight`), so this is **not** a clipped fixed height — it
is an **extent vs. padding** problem. The `ScrollViewer` has `Padding="8"`, and each item `Border`
adds `Margin="4"`. The combination of the scroll-presenter padding and the last item's bottom
margin leaves the last item's tail below the reachable scroll extent, so the bottom content cannot be
brought into view. The fix is to make the scrollable extent actually include the last item's full
height plus breathing room at the bottom — e.g. move the bottom inset onto the *content* (a bottom
`Margin`/`Padding` on the `ItemsControl`, so it is part of the measured extent) rather than relying on
the ScrollViewer's `Padding`, and verify the last item's bottom is within `Extent.Height`.

> The exact mechanic (presenter padding vs. content margin) should be confirmed by the failing test
> below; implement whatever makes the test pass. Do not just bump a magic number — the test must
> assert the last item's bottom is reachable.

## Goal

In both the Input and Output tabs, with parsed sections/fields shown:

1. Every accordion item arranges to the **full content width of the panel** (no ragged short items),
   robust to the panel being resized (the column is user-resizable — see "Sequencing").
2. The user can scroll until the **last** item's **bottom edge** is visible — its tail content is
   never clipped or unreachable, with a small, deliberate gap below it.

No change to what the sections contain, how they parse, or the collapse-by-default behaviour
(`IsExpanded="{Binding CollapsedByDefault, Converter=BoolNotConverter}"` stays).

## Approach (suggested — Plan/Implement may refine)

1. **Width:** on the item `Border` in both views set `HorizontalAlignment="Stretch"`; on the
   `Expander` in `StageInputView` set `HorizontalAlignment="Stretch"` and
   `HorizontalContentAlignment="Stretch"`. If a `ContentPresenter`/item-container default still
   blocks the stretch, add a scoped style (e.g. `Style Selector="...ItemsControl > ContentPresenter"`
   with `HorizontalAlignment="Stretch"`, or set `ItemsControl.ItemContainerTheme`/an explicit
   `ItemsPanel`) — keep it local to these views, do not restyle `Expander` app-wide.
2. **Scroll:** give the scrollable content a real bottom inset that is part of the measured extent —
   e.g. a bottom `Margin`/`Padding` on the `ItemsControl` (or wrap it so the inset is measured),
   instead of relying solely on the `ScrollViewer Padding="8"`. Confirm the `ScrollViewer`'s
   `Extent.Height` covers the last item's bottom after layout.
3. Apply the same fix to **both** `StageInputView.axaml` and `StageOutputView.axaml` (shared shape).

Rejected (do not re-litigate): widening the column to hide the raggedness (the column is
intentionally resizable); adding a fixed `Height`/`MaxHeight` to force a scrollbar (masks the extent
bug and reintroduces clipping at other sizes).

## Files

- `src/VisualRelay.App/Views/Controls/StageInputView.axaml` — Input accordions (Expanders).
- `src/VisualRelay.App/Views/Controls/StageOutputView.axaml` — Output fields (same shell).
- `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` — only if a scoped stretch style is the cleanest
  fix; keep any new selector narrow.
- `tests/VisualRelay.Tests/ActivityColumnTabsUiTests.*.cs` (or a new
  `ActivityColumnAccordionLayoutTests.cs`) — the failing tests.

## Tests (TDD — write the failing tests first)

Headless `[AvaloniaFact]`, modelled on `tests/VisualRelay.Tests/ActivityColumnTabsUiTests.StageRendering.cs`
(`InputTab_Ready_UsesExpandersWithCollapsedByDefault`, `OutputTab_Ready_RendersFieldsByKind`) and its
helpers `SwitchToTabAndFindView<T>` (switches the `TabControl`, runs the dispatcher) and
`GetVisualDescendants` (`tests/VisualRelay.Tests/ActivityColumnTabsUiTests.cs:263-285`). Build a
`MainWindowViewModel`, populate `StageDetail` with `InputState = StageDetailState.Ready` +
`InputSections` (a mix of short and long bodies, e.g. `new PromptSection("Header", "x", false)`,
`new PromptSection("Task input", <long multi-line body>, false)`, `new PromptSection("Output contract",
<long body>, false)`), `Show()` a `MainWindow` at a fixed size (1440×900, as the sibling tests do),
`RunJobs()` to settle layout. Write these first; they must fail on today's markup, then pass:

1. **Every Expander stretches to the panel content width.** Find the `StageInputView`, get the
   `ScrollViewer`'s content presenter (or the `ItemsControl`) actual width as the available content
   width, and assert each `Expander`'s `Bounds.Width` equals that width (within a 1 px tolerance for
   the item `Border`'s `Margin`/`BorderThickness`). On today's layout the short-content Expanders are
   narrower than the long ones; assert all item widths are equal AND equal the panel content width.
   (Equivalently: assert `Math.Abs(expander.Bounds.Width - panelContentWidth) <= tolerance` for all.)
2. **The last item's bottom is reachable / inside the extent.** After layout, assert the
   `ScrollViewer`'s `Extent.Height >= lastItem.Bounds.Bottom` (translate the last `Expander`/`Border`
   into the ScrollViewer/content coordinate space via `TranslatePoint`), with a small positive bottom
   gap — i.e. the content extent fully contains the last item plus an inset. This fails today (the
   last item's bottom falls outside the reachable extent) and passes after the fix. Make the content
   tall enough to require scrolling (long bodies and/or many sections) so the assertion is meaningful.
3. **Output tab parity.** Repeat assertions 1 and 2 for `StageOutputView` with
   `OutputState = StageDetailState.Ready` and a set of `OutputField`s (Text/List/Json kinds, as in
   `OutputTab_Ready_RendersFieldsByKind`), asserting the item `Border`s stretch to the panel width and
   the last field's bottom is within the ScrollViewer extent.
4. **Regression:** keep the existing
   `InputTab_Ready_UsesExpandersWithCollapsedByDefault` / `OutputTab_*` tests green (collapse-by-default
   and field-by-kind behaviour unchanged).

## Sequencing

The Input and Output tabs **share the same ScrollViewer/ItemsControl rendering shape**, so fix and
test **both** in this task. The ACTIVITY column is **user-resizable** (the `GridSplitter`
`ActivitySplitter` in `src/VisualRelay.App/Views/MainWindow.axaml:86-91`, width persisted as
`ActivityColumnWidth` — added by `stage-visibility-4`): the width fix must hold at any column width,
so assert against the *measured* panel content width, not a hardcoded pixel value. No coordination
with other in-flight tasks is required; this is a self-contained layout fix.

## Done when

- [ ] The new headless `[AvaloniaFact]` tests are written first, fail on the current markup, and pass
      after the fix: every Input `Expander` (and every Output item `Border`) stretches to the panel
      content width, and the last item's bottom is within the `ScrollViewer` extent (reachable).
- [ ] Both the Input and Output tabs are fixed (shared rendering); no item is ragged-narrow and the
      last item's tail is fully scrollable into view.
- [ ] Existing Activity-tab UI tests stay green (collapse-by-default, raw toggles, field-by-kind).
- [ ] `./visual-relay check` green; every changed/new file under 300 lines; Conventional Commit
      subjects (e.g. `fix(app): stretch activity accordions and reach last item bottom`).
