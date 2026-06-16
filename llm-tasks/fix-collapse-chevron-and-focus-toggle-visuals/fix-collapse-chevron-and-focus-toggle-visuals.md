# Fix the collapse/expand chevron + focus-toggle visuals (size, alignment, prominence)

`DONE-polish-collapse-expander-affordances` fixed the right things — the rail titles now size
correctly (`LayoutTransformControl`), the chevrons point the correct way for every panel, and the two
toggle styles were merged into one `collapseToggle`. But it introduced/left three **visual** defects
that still read as unpolished:

1. **Mixed glyph sizes.** The chevrons are raw Unicode triangles in two different families: the
   horizontal ones use the *large* `◀ ▶` (`U+25C0` / `U+25B6`, "BLACK …-POINTING TRIANGLE") while the
   vertical-fold ones use the *small* `▸ ▾` (`U+25B8` / `U+25BE`, "BLACK …-POINTING **SMALL**
   TRIANGLE"). At a single `FontSize="12"` the small triangles render visibly tinier and lighter, so a
   header that folds vertically (Stages, Run Log, LLM Commands) shows a dramatically smaller chevron
   than one that collapses horizontally (Queue). This is the "share one baseline and weight"
   requirement the prior task was given and missed.
2. **Off alignment.** Because these are font glyphs with per-glyph ink offsets, the small `▾` sits high
   relative to the pill beside it in the Run Log header but roughly centered in the Stages header —
   the same glyph lands at different heights, and none are truly optically centered in the 26×26
   button.
3. **The master Focus/Restore toggle is tiny.** The "collapse everything / focus the task" control
   (`FocusButtonIcon` `⤢`/`⤡`, `U+2922`/`U+2921`) reuses the *same* faint 26×26 `collapseToggle` style
   as the minor per-panel chevrons and carries no label, so the most important affordance is the least
   visible one.

This is an **app-UI task** for Visual Relay's own Avalonia front-end — it is *not* a harness change,
so being specific to this AXAML/Avalonia UI is correct. Do **not** generalize it into the
task-driving engine.

## Current state (researched)

> **Freshness contract.** The snippets below are a snapshot taken 2026-06-16 and may have drifted.
> **Locate every anchor by searching for the quoted code, not by line number.** If a quoted snippet no
> longer matches verbatim, treat this section as stale, re-read the whole file, and adapt to what is
> actually there.

### What already works — do NOT regress it

- **Direction/axis scheme is correct.** Leave *which way each chevron points* and all collapse
  behavior exactly as-is. The defects are size/weight/alignment/prominence only.
- **Rail title sizing** (`MainWindow.axaml`, the `LayoutTransformControl` wrapping each `railTitle`)
  is correct — keep it.
- **Collapse behavior** — `ApplyCenterSplit` / `ApplyRowSplit`, the `Auto`-track content-swap, and the
  `IsActivityColumnCollapsed` computed property — is out of scope. Do not touch it.

### The glyphs (the thing to change)

`src/VisualRelay.App/ViewModels/MainWindowViewModel.Layout.cs` returns glyph **strings**:

```csharp
public string QueueChevron => IsQueueCollapsed ? "▶" : "◀"; // ▶ ◀
public string QueueRailChevron => "▶"; // ▶
public string StagesChevron => IsStagesCollapsed ? "▸" : "▾"; // ▸ ▾
public string RunLogChevron => IsActivityColumnCollapsed ? "▶" : IsRunLogCollapsed ? "▸" : "▾";
public string LlmCommandsChevron => IsActivityColumnCollapsed ? "▶" : IsLlmCommandsCollapsed ? "▸" : "▾";
public string ActivityRailChevron => "◀"; // ◀
public string FocusButtonIcon => IsFocused ? "⤡" : "⤢"; // ⤡ ⤢
```

These are bound as `Content` into `Button.collapseToggle` in: `MainWindow.axaml` (the two rails),
`Views/Controls/StageBoard.axaml`, `Views/Controls/ActivityColumn.axaml` (Run Log + LLM Commands
headers), and `Views/Controls/QueuePanel.axaml` (Queue header). The focus toggle is in
`Views/Controls/TaskDetailPanel.axaml`:

```xml
<Button Classes="collapseToggle"
        Command="{Binding ToggleFocusCommand}"
        Content="{Binding FocusButtonIcon}"
        ToolTip.Tip="{Binding FocusButtonTooltip}"
        VerticalAlignment="Center"/>
```

The shared style is in `src/VisualRelay.App/Styles/VisualRelayTheme.axaml`:

```xml
<Style Selector="Button.collapseToggle">
  <Setter Property="Width" Value="26"/>
  <Setter Property="Height" Value="26"/>
  <Setter Property="Foreground" Value="#6F7785"/>
  <Setter Property="FontSize" Value="12"/>
  ...
</Style>
```

### Why it shipped green

`tests/VisualRelay.Tests/MainWindowViewModelLayoutTests.cs` (`Chevrons_FollowDirectionAndAxisScheme`,
`FocusButton_*`) pins the **exact glyph codepoints** (`◀`, `▶`, `▾`, `▸`, `⤢`,
`⤡`) and the tooltips; `CollapseAffordanceTests.cs` pins the rail-title bounds and tooltips.
Nothing asserts visual size/weight/alignment, so the mixed-size design passed CI. **You will be
rewriting these glyph assertions** to match the new representation (below) — that is expected, not a
regression.

### Stale screenshots

`docs/images/visual-relay-main.png` / `visual-relay-compact.png` were **never regenerated after the
chevron change** (last updated in `6805ebc`, before the `c67da44` fix), so they still show the old
uniform glyphs. Part of this task is to regenerate and commit them.

## What to build

### A. Replace the Unicode chevrons with one drawn vector chevron, uniform size + centered

Stop rendering chevrons as text glyphs. Render them as **vector geometry** (an Avalonia `Path` /
`PathIcon` / `StreamGeometry` inside a `Viewbox`) so every chevron is the **same pixel size and stroke
weight regardless of direction** and is **optically centered** in its button. One chevron shape,
rotated/pointed four ways (Left, Right, Up, Down).

Recommended shape and structure (Decision 1 + 3):

- Define **one** chevron geometry once (a shared `StreamGeometry`/`Geometry` resource, or a small
  reusable `ChevronIcon` control with a `Direction` property). Do not hand-author four near-duplicate
  paths inline in five files.
- Change the ViewModel to expose a **direction**, not a glyph string. Add an enum
  (e.g. `ChevronDirection { Left, Right, Up, Down }`) and one computed property per affordance
  returning it, mapping **exactly** the current scheme (Decision 2):
  - `QueueChevron` → `Left` expanded, `Right` collapsed; `QueueRailChevron` → `Right`.
  - `StagesChevron` → `Down` expanded, `Right` collapsed.
  - `RunLogChevron` / `LlmCommandsChevron` → `Right` when `IsActivityColumnCollapsed`; else `Down`
    expanded / `Right` collapsed.
  - `ActivityRailChevron` → `Left`.
  Keep the `[NotifyPropertyChangedFor(...)]` wiring on the collapse flags exactly as it is today so the
  directions update when state flips.
- Map direction → geometry in XAML via the reusable control's `Direction` property (preferred — it is
  unit-testable and keeps the five call sites tiny) or an `IValueConverter`. **Compiled bindings stay
  on**: bind only against each control's existing `x:DataType`, no `$parent[...]` reflection hops, no
  `x:CompileBindings="False"`.

Pick **one** target glyph size (e.g. a ~10–12 px chevron in a fixed `Viewbox`) and one stroke
weight/foreground for all chevrons. With vector geometry centered in the button there must be **no**
size difference between a horizontal and a vertical chevron, and **no** vertical-alignment drift
between panels.

### B. Keep the single unified toggle affordance

The chevron toggles stay one consistent control (one size, padding, foreground, `:pointerover`) in
both header and rail — do not reintroduce a second style. Ensure the button centers its vector content
(`HorizontalContentAlignment` / `VerticalContentAlignment` = `Center`).

### C. Make the master Focus/Restore toggle prominent (icon-only)

Give the focus/restore control its **own** style (do **not** reuse `collapseToggle`) so it reads as the
primary "collapse everything" affordance, clearly heavier than the subtle per-panel chevrons. Per the
settled decision it stays **icon-only** (no text label): use a larger hit-area and a stronger
foreground/contrast (and, if it reads better, a subtle background or border), and render its
expand/contract icon as a **crisp vector** at a legibly larger size — keep the existing diagonal
expand↔contract semantics (`⤢` focus / `⤡` restore today). Keep its tooltip (`FocusButtonTooltip`) and
keep `FocusButtonLabel`/`FocusButtonTooltip` flipping with `IsFocused` for accessibility. It must be
visually distinguishable at a glance from the chevrons.

## Tests

Write the failing test(s) first; mirror the existing patterns in `tests/VisualRelay.Tests/`.

- **VM direction (plain VM tests).** Rewrite `Chevrons_FollowDirectionAndAxisScheme` to assert the new
  **direction** per state for every affordance (the mapping in §A) instead of glyph codepoints — pin
  each so a regression to the old shared/again-mixed scheme fails. Keep
  `ChevronPropertyChanged_FiresOnFlagChange` (the notifications must still fire when the flags flip).
- **Uniform rendering (headless `[AvaloniaFact]`).** Add coverage that the chevron affordances render
  the **vector** chevron (a `Path`/`PathIcon`/`ChevronIcon`), not a `TextBlock` glyph, and that the
  rendered chevron size is **identical** across a horizontally-collapsing panel and a
  vertically-folding one (e.g. equal `Viewbox`/icon bounds, or all reference the one shared geometry at
  one size). If exact bounds are impractical headlessly, at minimum assert the visual tree uses the
  vector chevron control and the single shared geometry rather than a bound text glyph.
- **Focus toggle distinct + prominent.** Assert the focus/restore button does **not** carry the
  `collapseToggle` class (uses its own style) and renders at a larger size than the chevron toggles.
- **No behavior regression.** `CollapseAffordanceTests` (rail bounds + tooltips) and the existing
  `IsQueueCollapsed` / `IsStagesCollapsed` / `IsActivityColumnCollapsed` / `ToggleFocus` tests pass
  unchanged — this task touches glyph rendering + the focus-toggle style only, not collapse behavior.

## Done when

- Every collapse/expand chevron is a **drawn vector** of identical size and stroke weight, optically
  centered in its toggle; **no** mixed large/small glyphs and **no** raw Unicode triangle remains in
  any toggle. Horizontal and vertical chevrons are visibly the same size.
- Direction/axis semantics and tooltips are **unchanged** from today (Queue `Left`/`Right`, vertical
  folds `Down` expanded / `Right` collapsed, activity slide header `Right` / rail `Left`).
- The master Focus/Restore toggle is visibly **prominent and distinct** from the per-panel chevrons
  (its own larger, higher-contrast style), icon-only, tooltip retained.
- With nothing collapsed the window is otherwise visually consistent with before (only the
  chevron/focus restyle changes); `ApplyCenterSplit` / `ApplyRowSplit` / `Auto`-track behavior and all
  four collapse/restore paths are unchanged. Collapse state stays in-memory (nothing persisted to
  `.relay/config.json`).
- `docs/images/visual-relay-main.png` and `visual-relay-compact.png` are **regenerated** with
  `./visual-relay screenshot` and committed (the step the prior task skipped).
- Tests first; `./visual-relay check` green; compiled bindings clean (no reflection-hop, no
  `x:CompileBindings="False"`); every changed file under 300 lines; Conventional Commit subjects
  (e.g. `fix(ui): render collapse chevrons as uniform vector icons`, `fix(ui): make the task-focus
  toggle a distinct prominent control`).

## Decisions

These are **settled** — implement to them; no further design input is needed.

1. **Drawn vector chevrons, not Unicode.** Replace the text glyphs with `Path`/`PathIcon`/
   `StreamGeometry` geometry in a `Viewbox`, one size and one weight for all four directions. *Why:* the
   prior task's mixed `U+25B6/25C0` (large) + `U+25B8/25BE` (small) triangles are the root cause of the
   size/weight inconsistency, and font glyph metrics are why they won't center cleanly.
2. **Preserve the existing direction/axis scheme exactly.** Only the *rendering* changes — not which
   way any chevron points and not any collapse behavior. This keeps the task low-risk; the directions
   are already correct.
3. **ViewModel exposes a direction (enum/key) per affordance**, not a glyph string; XAML maps
   direction → the one shared geometry (via a reusable chevron control or a converter). Keep the
   `[NotifyPropertyChangedFor]` notifications.
4. **Master Focus/Restore toggle = prominent, icon-only, its own style** (not `collapseToggle`):
   larger and higher-contrast than the chevrons, crisp vector icon, tooltip retained.
5. **Regenerate and commit the screenshots** — the prior task's missing verification artifact.

## Notes

- `Path`, `PathIcon`, `Viewbox`, and `StreamGeometry` live in `Avalonia.Controls` / `Avalonia.Media`
  — all available in Avalonia 12.0.4 with no extra package.
- Do not edit `ApplyCenterSplit` / `ApplyRowSplit` or `IsActivityColumnCollapsed`; if you find yourself
  there, you are out of scope.
- Lineage: this is the visual-polish follow-up to `DONE-polish-collapse-expander-affordances`; that
  task's direction/axis and rail-sizing work is correct and must be preserved.
