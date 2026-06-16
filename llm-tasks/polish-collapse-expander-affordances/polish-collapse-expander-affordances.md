# Polish the panel collapse/expand affordances (rails, chevrons, toggles)

Visual Relay's main window lets the operator collapse each surrounding panel — Queue (left),
Stages (center-bottom), Run Log and LLM Commands (right) — added in
`DONE-04-collapsible-panels-and-task-focus-mode`. The mechanism works (space is reclaimed
correctly), but the **affordances themselves look unpolished**: the collapsed-rail titles are
cramped/misaligned, the chevron glyphs point the wrong way for some panels so it is unclear what a
click will do, and the toggle buttons are styled two different ways. This task makes the
collapse/expand controls feel **deliberate and legible** without changing the layout behavior that
already works.

This is an **app-UI task** for Visual Relay's own Avalonia front-end — it is *not* a harness change,
so being specific to this AXAML/Avalonia UI is correct and expected. Do **not** generalize it into
the task-driving engine.

## Current state (researched)

> **Freshness contract.** Line numbers and quoted snippets below are a snapshot taken 2026-06-15 and
> may have drifted. **Locate every anchor by searching for the quoted code, not by line number.** If
> a quoted snippet no longer matches verbatim, treat this section as stale, re-read the whole file,
> and adapt to what is actually there before editing.

### What already works — do NOT regress it

Space reclamation on collapse is implemented in code-behind and is correct; **leave its logic
intact**:

- `src/VisualRelay.App/Views/MainWindow.axaml.cs` — `ApplyCenterSplit()` sets the center
  `CenterGrid` rows to `*,Auto` when `IsStagesCollapsed` (TASK fills, Stages folds to its header) and
  back to `1.45*,*` when expanded.
- `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml.cs` — `ApplyRowSplit()` sets the two
  activity rows to `Auto`/`*` combinations so collapsing one sub-panel gives its height to the other,
  and `Auto`/`Auto` when both are collapsed (the column then swaps to the right rail).
- The left Queue column and the right Activity column use **content-swap in an `Auto` track**: the
  full panel and a 36px `Border Classes="rail"` share the cell, toggled by `IsVisible`
  (`MainWindow.axaml`, the two `<Panel Grid.Column=...>` blocks). With nothing collapsed the window
  must stay pixel-identical to today.

These are the parts to preserve. The defects below are all in the *presentation* of the toggles.

### Defect 1 — collapsed-rail titles use a render-only rotation (the "sizing wonky")

In `src/VisualRelay.App/Views/MainWindow.axaml`, each collapsed rail renders its title like:

```xml
<TextBlock Classes="railTitle" Text="QUEUE" TextAlignment="Center">
  <TextBlock.RenderTransform>
    <RotateTransform Angle="90"/>
  </TextBlock.RenderTransform>
</TextBlock>
```

(the same shape repeats for `RUN LOG` and `LLM CMDS` in the right rail.) `RenderTransform` is applied
**after** layout: the `TextBlock` is still *measured and arranged at its un-rotated horizontal size*
(e.g. "RUN LOG" ≈ 50×16 px), then merely painted rotated. Inside a 36 px rail (`Border.rail` has
`Padding="6"` → ~24 px of content width) the 50 px-wide layout box overflows the rail and the
rotated text is off-center / collides with the chevron. That mismatch is the "sizing is wonky"
the screenshots show. The rail width (`Width="36"`), padding, and the `StackPanel` spacing/margins
also differ between the two rails (Queue rail `Spacing="8" Margin="0,14,0,0"` vs Activity rail
`Spacing="10" Margin="0,16,0,0"`), so the two rails don't match.

### Defect 2 — chevron glyphs ignore the panel's collapse direction/axis (the "intention unclear")

All four chevrons come from one shared formula in
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Layout.cs`:

```csharp
public string QueueChevron       => IsQueueCollapsed       ? "▶" : "◀"; // ▶ : ◀
public string StagesChevron      => IsStagesCollapsed      ? "▶" : "◀";
public string RunLogChevron      => IsRunLogCollapsed      ? "▶" : "◀";
public string LlmCommandsChevron => IsLlmCommandsCollapsed ? "▶" : "◀";
```

Each glyph is bound into **both** the panel header's `Button.collapseToggle` (visible while expanded)
and the collapsed rail's `Button.railToggle` (visible while collapsed). Because the formula is
identical for every panel, the direction is wrong wherever the panel is not on the left edge:

- **Queue (left edge)** — header shows `◀` (collapse toward the left edge), rail shows `▶` (expand
  rightward). **Correct** — leave the *semantics* but adopt the unified scheme below.
- **Run Log / LLM Commands (right edge)** — header shows `◀` but the panel collapses to the **right**;
  rail shows `▶` but it re-expands to the **left**. **Both backwards.** (See `ActivityColumn.axaml`
  `Button.collapseToggle` bound to `RunLogChevron` / `LlmCommandsChevron`, and the right rail
  `Button.railToggle` blocks in `MainWindow.axaml`.)
- **Stages (folds vertically to a header strip)** — uses the horizontal `◀`/`▶` for a **vertical**
  collapse. **Wrong axis** — it should read as a fold (down/up), not a sideways move.
  (`StageBoard.axaml`, `Button.collapseToggle` bound to `StagesChevron`.)

A single shared string also can't be right for the dual-mode right panels: when only one of Run
Log / LLM Commands is collapsed it folds **in place** to a header strip (vertical), but when both are
collapsed the column becomes the right **rail** (horizontal). The header button and the rail button
need *different* glyphs — they currently share one prop.

### Defect 3 — two divergent toggle button styles

`src/VisualRelay.App/Styles/VisualRelayTheme.axaml` defines `Button.collapseToggle` (26×26) for the
in-header toggles and a separate `Button.railToggle` (24×24) for the rail toggles, with their own
hover rules. They should be one consistent affordance (size, hit-area, foreground, hover) so the
control reads the same everywhere. `TextBlock.railTitle` (FontSize 10, Foreground `#6F7785`) and the
header `TextBlock.panelTitle` (FontSize 12, Foreground `#9AA3B1`) are also gratuitously different.

### Conventions to honor

- ViewModel state is `[ObservableProperty]`; computed glyph props depend on the collapse flags via
  `[NotifyPropertyChangedFor(...)]` already wired on each flag in `Layout.cs`. Keep the new glyph
  props notified the same way.
- Avalonia 12.0.4, compiled bindings **on**, warnings-as-errors (`Directory.Build.props`,
  `VisualRelay.App.csproj`). Bind only against each control's existing
  `x:DataType="vm:MainWindowViewModel"` — no `$parent[...]` reflection hops, no
  `x:CompileBindings="False"`.
- Shared visuals live in `Styles/VisualRelayTheme.axaml`. Every changed file must stay **under 300
  lines**.

## What to build

Three coordinated changes. **Do not** alter `ApplyCenterSplit` / `ApplyRowSplit` or the `Auto`-track
content-swap — only the toggle/rail presentation.

### A. Fix the collapsed-rail title rendering so it sizes correctly

Make the rotated rail title participate in layout (so the rail sizes to it and it centers cleanly),
**or** drop rotation entirely for a cleaner vertical affordance. Recommended (Decision 1): wrap the
rotated title in a `LayoutTransformControl` —

```xml
<LayoutTransformControl>
  <LayoutTransformControl.LayoutTransform>
    <RotateTransform Angle="90"/>
  </LayoutTransformControl.LayoutTransform>
  <TextBlock Classes="railTitle" Text="QUEUE"/>
</LayoutTransformControl>
```

— because `LayoutTransformControl` rotates the *layout* bounds, so a 50×16 label becomes a 16×50 box
the rail can actually contain and center. Apply it to all three rail titles (QUEUE, RUN LOG, LLM
CMDS). Acceptable alternative if it reads cleaner: remove the rotated text and show an icon-only rail
(a small panel glyph + the chevron) with the full title in `ToolTip.Tip`. Either way the outcome must
be: **no clipped, overflowing, or off-center rail labels**, and the rail content vertically centered
within a consistent rail width.

Unify the two rails' metrics: identical `Width`, `Border.rail` padding, `StackPanel` `Spacing` and top
`Margin`, and title style, so the left and right rails are visually consistent.

### B. Make every chevron point the right way (clear intention)

Replace the single shared `IsCollapsed ? ▶ : ◀` formula with **direction- and axis-correct** glyphs,
decoupling the header (collapse) control from the rail (expand) control where a panel has both. The
scheme (Decision 2):

- **Edge-docked panels that slide to a side rail** — Queue (left) and the Activity column (right):
  the chevron points **toward the docking edge to collapse** and **away to expand**. Queue: header
  `◀` (collapse left), rail `▶` (expand right). Activity rail: `◀` (expand left, back from the right
  edge); and the Run Log / LLM Commands **headers**, when their click slides the whole column to the
  right rail, read `▶`.
- **Panels that fold in place to a header strip** — Stages always, and Run Log / LLM Commands when
  folded individually (sibling still open): use a **vertical disclosure** chevron following the
  standard tree convention — open/expanded shows `▾` (`▾`, "click to fold up"), collapsed shows
  `▸` (`▸`, "click to unfold"). The collapsed header strip stays visible, so its chevron flips
  `▾`↔`▸` in place.

Because Run Log / LLM Commands are dual-mode (fold-in-place when the sibling is open, slide-to-rail
when both are collapsed), do **not** reuse one string in two places. Provide the header control its
own fold glyph and the rail control its own expand glyph (e.g. a dedicated rail-expand glyph property,
or literal glyphs in the rail markup). Derive header glyphs from the collapse flags (and, for the
dual-mode panels, from `IsActivityColumnCollapsed`) so they stay correct as state changes; keep them
`[NotifyPropertyChangedFor]`-notified like the existing props. Pick **one** glyph set and one font
size for all chevrons so they share a baseline and weight.

Update the existing `ToolTip.Tip` strings to match the resolved action ("Collapse Queue" /
"Expand Queue", etc.) so hover text and glyph agree.

### C. Unify the toggle affordance

Collapse `Button.collapseToggle` and `Button.railToggle` into **one** consistent style (single size,
padding, foreground, and `:pointerover`) — either one shared class applied in both places or one
restyled to match the other. The collapse/expand control must look identical whether it sits in a
panel header or a rail. Keep the rail title style consistent with this unified treatment.

## Tests

Write the failing test(s) first. Follow the existing patterns in
`tests/VisualRelay.Tests/` for layout/VM tests (the `04` task added VM + headless `[AvaloniaFact]`
coverage — mirror it; search for `IsQueueCollapsed` / `Chevron` / `ToggleFocus`).

- **VM glyph direction (plain VM tests).** For each panel, assert the header glyph and the
  rail/expand glyph for both collapsed and expanded states match the scheme in §B: Queue header `◀`
  expanded and rail `▶`; Activity rail expand glyph `◀` and the Run Log / LLM Commands header glyph
  `▶` when the click slides the column to the rail; Stages `▾` expanded and `▸` collapsed. Whatever
  property shape you choose (new computed props, or two props per dual-mode panel), pin the exact glyph
  per state so a regression to the old shared formula fails. Assert the relevant `OnPropertyChanged`
  fires when the underlying flag flips (mirror how the existing `*Chevron` notifications are tested).
- **No behavior regression.** Existing tests for `IsQueueCollapsed` / `IsStagesCollapsed` /
  `IsActivityColumnCollapsed` / `ToggleFocus` still pass unchanged — this task does not touch collapse
  *behavior*, only glyphs/markup/styles.
- **Headless render (where a control is involved).** Add/extend an `[AvaloniaFact]` that loads
  `MainWindow` with a panel collapsed and asserts the rail title control is present and laid out with
  a non-zero, in-bounds size (e.g. the `LayoutTransformControl`/title's bounds fit within the rail
  width) — i.e. the rotated label is no longer overflowing. If a full render assertion is
  impractical headlessly, at minimum assert the visual tree contains the unified toggle style and the
  `LayoutTransformControl` (or icon-rail) replacement rather than a bare `RenderTransform` on the
  `TextBlock`.

## Done when

- Collapsed rails (Queue, Run Log, LLM Commands) show their titles cleanly: vertically centered,
  not clipped/overflowing, with both rails sharing one width, padding, spacing, and title style.
- Every collapse/expand chevron points the correct way: left-edge Queue collapses `◀`/expands `▶`;
  the right Activity column collapses `▶`/expands `◀`; Stages and any in-place fold use the vertical
  disclosure `▾`(expanded)/`▸`(collapsed). No panel uses a horizontal arrow for a vertical fold, and
  no right-edge panel points left to collapse. Tooltips agree with the glyphs.
- The in-header and in-rail toggle buttons are one consistent affordance (same size, padding,
  foreground, hover).
- With nothing collapsed, the window is visually identical to before this task (verify with
  `./visual-relay screenshot`); the `ApplyCenterSplit` / `ApplyRowSplit` / `Auto`-track behavior is
  unchanged and all four panels still collapse and restore exactly as before.
- Collapse state remains in-memory only (nothing persisted to `.relay/config.json`).
- Tests first; `./visual-relay check` green; compiled bindings clean (no reflection-hop, no
  `x:CompileBindings="False"`); every changed file under 300 lines; Conventional Commit subjects
  (e.g. `fix(ui): make panel collapse chevrons direction-correct`, `fix(ui): size collapsed rail
  titles with LayoutTransformControl`).

## Decisions

These are **settled** — implement to them; no further design input is needed.

1. **Rail title sizing → wrap the rotated `TextBlock` in a `LayoutTransformControl`** (so the rotation
   affects layout and the rail sizes/centers correctly), rather than leaving the render-only
   `RotateTransform`. Dropping rotation for an icon-only rail with a tooltip is an acceptable
   alternative *only if* it reads cleaner and still meets every "Done when" bullet; do not do both.
2. **Chevron scheme → direction + axis correct, header/rail decoupled.** Edge-docked panels point
   toward their docking edge to collapse and away to expand (Queue `◀`/`▶`; Activity column `▶`/`◀`);
   in-place folds use the vertical disclosure `▾`(expanded)/`▸`(collapsed). Do not reuse one glyph
   string across the header and the rail for the dual-mode right panels — give each context its own
   glyph. *Why:* a single shared `IsCollapsed ? ▶ : ◀` cannot encode both a left vs right docking edge
   and a horizontal-slide vs vertical-fold axis, which is exactly why the current control reads as
   ambiguous.
3. **One toggle style.** Merge `collapseToggle` and `railToggle` into a single consistent affordance
   rather than keeping two near-duplicate styles. *Why:* the collapse/expand control should look and
   feel identical wherever it appears; two styles is the kind of drift that reads as unpolished.

## Notes

- Pure presentation change: no new persisted config, no change to collapse *behavior* or the
  space-reclaim code-behind. If you find yourself editing `ApplyCenterSplit` / `ApplyRowSplit` or the
  `IsActivityColumnCollapsed` computed property, you have gone out of scope.
- `LayoutTransformControl` lives in `Avalonia.Controls`; it is available in Avalonia 12.0.4 with no
  extra package.
