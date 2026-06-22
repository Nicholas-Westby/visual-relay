# Right-panel (ACTIVITY) resizer is hard to grab, has no resize cursor, and doesn't look draggable

Resizing the right-hand **ACTIVITY** column is flaky: the user can only sometimes grab it, the mouse
cursor only sometimes changes to a resize cursor, and nothing about the divider line signals that it
is draggable. See `full-window.png` (the seam between the centre panels and the ACTIVITY column).

## Facts established (bake in)

All three symptoms trace to one tiny, invisible, mis-placed splitter.

- **The splitter is a 3 px, transparent strip.** `src/VisualRelay.App/Views/MainWindow.axaml`:
  ```xml
  <GridSplitter x:Name="ActivitySplitter"
                Grid.Column="1"
                Width="3"
                Background="Transparent"
                HorizontalAlignment="Right"
                IsVisible="{Binding !IsActivityColumnCollapsed}"/>
  ```
  A 3 px hit target is the direct cause of "only sometimes works" and "cursor only sometimes
  changes" — a GridSplitter sets the resize cursor on pointer-over, but you have to land on the
  3 px strip for it to trigger.
- **It is offset ~10 px from the line the user actually aims at.** `ContentGrid` has
  `ColumnSpacing="10"`. The splitter lives in `Grid.Column="1"` (the centre `*` column),
  `HorizontalAlignment="Right"`, so it sits at the *right edge of the centre column* — about 10 px
  to the **left** of the visible vertical seam, which is the **left border of the ACTIVITY panel**
  (`Grid.Column="2"`, a `<Border Classes="panel">`). The user drags the visible seam and misses the
  invisible hit strip beside it.
- **There is no GridSplitter style** (verified by grep) — so it has no visible line, no hover
  highlight, and no grip; `Background="Transparent"` makes the draggable region literally invisible.
- **Resize plumbing exists and works** (so keep it): `MainWindow.axaml.cs` wires
  `ActivitySplitter`'s `Thumb.DragCompletedEvent` to `OnActivitySplitterDragCompleted` (clamps to
  `[300, Bounds.Width-400]`, persists to `vm.ActivityColumnWidth`), and `SyncActivityColumnWidth`
  applies the width to `ContentGrid.ColumnDefinitions[2]`. The flakiness is the **target/affordance**,
  not (primarily) the math.

> **Freshness contract.** Confirm by searching for `ActivitySplitter`, `ColumnSpacing`, and
> `OnActivitySplitterDragCompleted`; `MainWindow.axaml` changes often, so adapt to the current
> structure rather than line numbers.
>
> ⚠️ **Cursor/"feel" needs live verification.** Whether the resize cursor appears on hover is a
> macOS-native behaviour; headless tests can assert geometry/properties but cannot confirm the
> on-hover cursor. Verify the actual feel by dragging in the running app.

## Goal

The seam between the centre area and the ACTIVITY column is an obvious, easy-to-grab drag handle:
it looks draggable, shows a horizontal-resize cursor whenever the pointer is over the visible seam,
and reliably resizes on the first try. The collapse/expand behaviour and the persisted width
(`vm.ActivityColumnWidth`, XDG-persisted) keep working.

## Recommended approach (Plan/Implement may refine)

- **Co-locate the hit target with the visible seam and widen it** to a comfortable grab area
  (≈ 8–10 px), so the user grabs where they aim. E.g. give the splitter its own narrow column at the
  centre/activity boundary, or align it to the ACTIVITY panel's left edge — and account for / remove
  the 10 px `ColumnSpacing` offset so the hit strip and the visible line coincide.
- **Make it look draggable:** add a `GridSplitter` style with a faint visible divider line, a
  `:pointerover` highlight, and an explicit horizontal-resize `Cursor` (e.g. `SizeWestEast`). A small
  centered grip is a nice touch but optional.
- Keep `x:Name="ActivitySplitter"`, the `DragCompleted` wiring, the clamp, and the
  `IsVisible="{Binding !IsActivityColumnCollapsed}"` gate.

## Tests

Headless (style of existing UI tests) — geometry/affordance is checkable even though hover-cursor
is not:
- Assert the splitter's hit width is ≥ the new minimum (not 3 px) and that it is positioned on the
  ACTIVITY seam (its X aligns with the panel boundary, not offset by `ColumnSpacing`).
- Assert it exposes the resize `Cursor` and a non-transparent visible divider style.
- Simulate a `DragCompleted` (or drive `OnActivitySplitterDragCompleted`) and assert
  `vm.ActivityColumnWidth` updates and is clamped — guarding the existing plumbing.
- **Manual:** in the running app, hover the seam (cursor changes) and drag it (resizes first try).

## Out of scope

The left QUEUE collapse rail, the collapse chevrons, and the centre Task/Stages split.

## Screenshot

- `full-window.png` — the app; the resizer seam is between the centre panels and the ACTIVITY column.
