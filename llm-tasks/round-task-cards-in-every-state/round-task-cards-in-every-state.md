# Round Task Cards in Every State — Remove the Square ListBoxItem Chrome Behind Them

A previous change (commit `d9baad5`, "fix: show combined active+in-progress state with blue outer
border") added the blue-outside-green combined state and put `CornerRadius="8"` on the card
layers. Square-corner artifacts still remain, because the biggest offender was never touched:
**Avalonia Fluent's own `ListBoxItem` template chrome paints square, full-item highlight
rectangles behind the rounded cards**. This task removes every remaining square artifact and
fixes two smaller geometry bugs, gated by machine-checkable render tests of every card state
(headless visual-tree assertions — image inspection is NOT required at any implementation stage;
the pipeline only has vision early, so screenshots cannot be a gate).

Evidence in this folder — for the early planning stages (which can see attached images) and for
the human reviewer; the enforcement gate is the render-test suite in step 4:

- `user-archive-quirks.png` / `user-queue-quirks.png` — the reported quirks (archive and queue).
- `archive-now.png` — live capture: the selected archive item shows a **square blue band spanning
  the whole item, including the "Today ($1.04)" day-header row**, behind the correctly rounded
  blue ring of the card itself.
- `queue-now.png` — live capture: the running+selected card's blue outer ring visibly pinches at
  the corners (radius math, defect D3 below).

## The defects, precisely

- **D1 — square selection band.** Selecting a card paints a square highlight over the entire
  `ListBoxItem` — most obvious in Archive, where the day header (`"Today ($1.04)"`) is part of
  the item template and gets swallowed by the band.
- **D2 — square hover/press tint.** Hovering (and pressing) a card adds a square full-item tint
  behind the rounded card.
- **D3 — pinched outer ring.** In the selected (and running+selected) state, the outer blue ring
  (`CornerRadius="8"`, `BorderThickness` 2) wraps the inner card (`CornerRadius="8"`) — for a
  uniform ring, the **outer radius must equal inner radius + outer border thickness**, i.e. 10.
  At 8/8 the gap pinches to zero at the corners, which reads as "squarish".
- **D4 — impossible rail radius.** The `selectionRail` is a 4px-wide stripe (`Grid`
  `ColumnDefinitions="4,*"`, column 0) carrying `CornerRadius="8"` on *all* corners — a radius
  larger than the element's width. It should be rounded on the left corners only, matched to the
  card's inner curvature.
- **D5 — day header participates in selection.** Consequence of D1: after the fix, the header
  row must show **no** tint or band in any state; card visuals must be the only painted state.

## Current state (researched)

- **Card template** — `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`, the
  `DataTemplate DataType="{x:Type vm:TaskRowViewModel}"` inside the `ListBox`
  `x:Name="TaskQueueList"`. Structure today (both queue and archive render through this same
  list — the archive toggle just swaps `ItemsSource` content):

  ```xaml
  <Grid RowDefinitions="Auto,*">
    <TextBlock Grid.Row="0" Text="{Binding DayHeader}" .../>          <!-- day header, same item -->
    <Border Grid.Row="1" CornerRadius="8"                              <!-- outer highlight ring -->
            BorderBrush="{Binding SelectedHighlightBorderBrush}"
            BorderThickness="{Binding SelectedHighlightBorderThickness}"
            BoxShadow="{Binding SelectedHighlightShadow}">
      <Border Padding="0" Classes="queueCard" MinHeight="76"           <!-- the card -->
              Background="{Binding CardBackgroundBrush}" BorderBrush="{Binding CardBorderBrush}"
              BorderThickness="{Binding CardBorderThickness}" BoxShadow="{Binding CardShadow}"
              CornerRadius="8">
        <Grid ColumnDefinitions="4,*">
          <Border Classes="selectionRail" Background="{Binding RailBrush}" CornerRadius="8"/>
          ...content, ProgressBar CornerRadius="3"...
  ```

- **State brushes** — `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs` (`RailBrush`,
  `CardBackgroundBrush`, `SelectedHighlightBorderBrush/Thickness/Shadow`, `CardBorderBrush`,
  `CardBorderThickness`, `CardShadow`). The selected ring is transparent/0 when not selected —
  this layered model is correct and should not be redesigned. Matrix tests live in
  `tests/VisualRelay.Tests/TaskRowViewModelTests.cs`.
- **Theme styles** — `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` (188 lines): `ListBox`
  transparent; `ListBoxItem` Padding 0 / Margin `0,0,0,8` / Background Transparent / MinHeight 0;
  `ListBoxItem:selected` Background Transparent; drag-reorder affordances
  `ListBoxItem.drop-above` / `.drop-below` (BorderBrush `#3191FF`, top/bottom `BorderThickness`);
  `Border.queueCard` defaults; `ListBoxItem:pointerover Border.queueCard` hover recolor.
- **Why the previous fix missed D1/D2**: those `ListBoxItem` styles set the *control's*
  `Background` property, but Fluent's `ListBoxItem` control theme applies its
  pointerover/pressed/selected fills to the **template's `ContentPresenter`** directly — a layer
  that app-level property setters on the control do not override. No amount of styling
  `Border.queueCard` can remove it; the item's template itself must stop painting state.
- **Screenshot tooling** — `tools/VisualRelay.Screenshots/Program.cs` builds a real
  `MainWindowViewModel` with demo tasks (`DemoTask(...)`), marks one running
  (`RestoreRunningTaskState(task.Id, 3, "Diagnose")`) and selects it (`viewModel.SelectedTask =
  task`), then renders PNGs. This is the vehicle for the mandatory visual verification below.

## What to build (in this order)

1. **Neutralize the item chrome with a scoped `ControlTheme` (fixes D1, D2, D5).** Add a keyed
   `ControlTheme` for `ListBoxItem` (put it in `VisualRelayTheme.axaml`'s resources — the
   `.axaml` files are near the 300-line guard: `QueuePanel.axaml` is at 276, so keep additions
   there to the one attribute) and reference it from the task list:
   `<ListBox x:Name="TaskQueueList" ItemContainerTheme="{StaticResource TaskCardItemTheme}" ...>`.
   The theme's template renders **only**:

   ```xaml
   <Border Background="{TemplateBinding Background}"
           BorderBrush="{TemplateBinding BorderBrush}"
           BorderThickness="{TemplateBinding BorderThickness}">
     <ContentPresenter Name="PART_ContentPresenter"
                       Content="{TemplateBinding Content}"
                       ContentTemplate="{TemplateBinding ContentTemplate}"
                       Padding="{TemplateBinding Padding}"/>
   </Border>
   ```

   with NO pseudo-class setters for `:pointerover`/`:pressed`/`:selected` — state visuals belong
   exclusively to the card's bound properties. The `Border` with `TemplateBinding` is required so
   the existing `.drop-above`/`.drop-below` drag-reorder line affordances (which set the *item's*
   `BorderBrush`/`BorderThickness`) keep rendering. Scope it to this list via the keyed theme —
   do NOT re-template `ListBoxItem` globally; other lists in the app keep their behavior.
2. **Fix the ring radius (D3).** Outer highlight `Border`: `CornerRadius="8"` → `"10"`, with a
   comment stating the invariant (`outer = inner 8 + ring thickness 2`). If the ring thickness
   ever changes, the radius must change with it.
3. **Fix the rail (D4).** `selectionRail` `CornerRadius="8"` → `"7,0,0,7"` (left corners only;
   7 ≈ inner radius 8 minus the 1px card border) so the stripe nests into the card's rounded
   left edge with no notch and no bleed. Check it against both the 1px (idle) and 2px (running)
   card border and adjust the left radii if a border overlap shows.
4. **Tests — the enforcement gate.** The implementing stages cannot look at images (the
   pipeline only has vision early), so every visual claim must be encoded as a machine-checkable
   assertion:
   - **View-model matrix** (`tests/VisualRelay.Tests/TaskRowViewModelTests.cs`): pin the full
     4-state matrix (default / selected / running / running+selected) across
     `SelectedHighlightBorderBrush`, `SelectedHighlightBorderThickness`, `CardBorderBrush`,
     `CardBorderThickness`, `RailBrush`, `CardBackgroundBrush` — selected always produces the
     blue ring, running the green card border, and both together, both.
   - **Headless render/tree tests** (new test class; pattern:
     `tests/VisualRelay.Tests/ChevronAffordanceRenderTests.cs` — `[AvaloniaFact]` +
     `[Collection("Headless")]`, builds real controls and walks the visual tree). Materialize the
     task `ListBox` with items covering all four states plus one item with a `DayHeader`, wait
     for layout, then assert structurally what an eye would check:
       - no element between each `ListBoxItem`'s root and its `Border.queueCard` paints a
         non-transparent `Background` — in the default state AND with pseudo-states forced via
         `((IPseudoClasses)item.Classes).Set(":pointerover", true)` / `":pressed"` and with the
         item genuinely selected (this is D1/D2, the Fluent chrome, expressed as an assertion);
       - when an item with a `DayHeader` is selected, the header `TextBlock`'s ancestors up to
         the item root still paint no background (D5);
       - `CornerRadius` invariants on the realized borders: outer ring `10`, card `8`, rail
         `7,0,0,7` — and assert outer == card radius + ring thickness so the *relationship* is
         pinned, not just the literals (D3, D4).
   - **Structural guard** (pattern: `ButtonsCentralizationTests.cs` scans source text): assert
     `QueuePanel.axaml`'s `TaskQueueList` declares `ItemContainerTheme` and that the
     `TaskCardItemTheme` in the theme file contains no `:pointerover`/`:pressed`/`:selected`
     setters — so the square chrome cannot silently return.
5. **Demo screenshot refresh (artifact for the human, not a gate).** Extend the demo seeding in
   `tools/VisualRelay.Screenshots/Program.cs` so the rendered queue shows all four card states
   at once (it already stages running+selected via `RestoreRunningTaskState` + `SelectedTask`;
   add a plain pending card, a selected-not-running card, and one visible `DayHeader`). Run the
   screenshot render (it is part of `./visual-relay check` anyway) so the human can compare the
   result against `archive-now.png` / `queue-now.png` after the task lands. Do NOT attempt to
   inspect the PNGs yourself — the render tests in step 4 are the gate.
6. **Full gate.** `./visual-relay check` (guards, format, build, tests, screenshot render).

## Done when

- All five defects (D1–D5) are pinned by the step-4 tests (chrome-free item templates under
  forced pseudo-states and real selection, radius invariants including the outer = card + ring
  relationship, header untouched by selection), and the demo screenshot scene covers pending,
  selected, running, running+selected, and a day header for human review after landing.
- The 4-state view-model matrix and the structural guard test pass; drag-reorder insertion lines
  and keyboard/click selection still work (selection state remains visible via the blue ring).
- No global `ListBoxItem` behavior changed outside the task list.
- `./visual-relay check` passes; every touched file stays under the 300-line guard
  (`QueuePanel.axaml` is at 276 — only the `ItemContainerTheme` attribute goes there).

## Guardrails

- Do not redesign the card layout, palette, or the layered ring model — this task is corners and
  chrome only. Minimal, diff-scoped edits.
- Do not "fix" square corners by hiding the highlight states; selected/hover feedback must remain
  visible, just rounded and card-scoped.
- The screenshots the verification produces are throwaway artifacts — do not overwrite README
  assets unless the existing README screenshot flow regenerates them anyway.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`).
