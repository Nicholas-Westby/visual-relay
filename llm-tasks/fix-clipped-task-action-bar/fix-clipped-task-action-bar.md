# Task: Stop the task header clipping its action bar

The TASK panel header is a two-column grid: a `TASK` panel title on the left and
the action bar (focus toggle, metric chip, status chip, Run selected, Resume,
Mark done, Reset) on the right. The action bar is a non-wrapping horizontal
`StackPanel` sitting in an `Auto` column, so it reports its full one-line width
no matter how little room the panel has. The `*` column holding the title is
handed whatever is left over, which is nothing.

The result at the standard 1440x900 render is that the title is squeezed to zero
width and never paints at all, and the bar runs straight off the right-hand side
of the panel. The panel's `ClipToBounds="True"` then cuts the overflow dead: the
"Mark done" button loses its right border, its corner radius and most of its
label, leaving a stray "Ma" against the panel edge. There is no ellipsis and no
overflow affordance — the pixels simply stop.

Every other panel in the app uses the same header idiom and looks correct,
because their `Auto` columns hold a chip and a 26px chevron rather than six
controls. Nothing is wrong with the title, the buttons or the clip; the fault is
the column sizing plus a panel that cannot wrap.

### Evidence (2026-08-20)

- `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:17` is
  `<Grid ColumnDefinitions="*,Auto">`. The `TASK` `TextBlock` is at lines 18-20 in
  the `*` column; `<controls:TaskActionBar Grid.Column="1"/>` is at line 21 in the
  `Auto` column.
- `src/VisualRelay.App/Views/Controls/TaskActionBar.axaml:1-8` is a
  `StackPanel` with `Orientation="Horizontal"` (line 7) and `Spacing="8"`
  (line 8). Its six children are the focus `IconButton` (9-13), the metric chip
  (14-21, `MaxWidth="220"`), the status chip (22-30, `MaxWidth="260"`),
  `Run selected` (31-33), `Resume` (34-36), `MarkDoneButton` (37-41) and
  `ResetButton` (42-46). A `StackPanel` never wraps.
- `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:8-9` wraps the whole
  panel in `<Border Classes="panel" ClipToBounds="True">`, and line 14 sets the
  header `Padding="16,14"`.
- Measured on `.relay/colour-run-log-attention-rows/visual-review/main.png`
  (1440x900, produced by the configured `visualRenderCmd`). The panel's 1px
  `Border.panel` frame (`src/VisualRelay.App/Styles/VisualRelayTheme.axaml:130-132`,
  `#252A33`) occupies **x=340** and **x=1059**, so the interior is x=341..1058 and
  the padded header content box is **x=357..1042, 686 px wide**.
- On scanline y=140 the action bar's children and their 8px gaps are:
  **357-390** focus toggle (34 px), gap 391-398, **399-547** metric chip (149 px),
  gap 548-555, **556-812** status chip (257 px, already ellipsised), gap 813-820,
  **822-931** `Run selected` face (110 px), gap 933-940, **942-1019** `Resume` face
  (78 px), gap 1021-1028, then the `Mark done` face starting at **x=1030**.
- The bar's first child begins at **exactly x=357**, the header content box's left
  edge. The `*` column is therefore **0 px wide** and the title has nowhere to
  paint.
- The `Mark done` button is genuinely clipped, not ellipsised. Its face runs
  1030-1040 flat `#414449`, glyph ink starts at x=1041, and the **last painted
  column is x=1059** (`#7A7D82`, anti-aliased ink mid-'r'); **x=1060 is `#0C0E12`**,
  outside the panel. There is no right border, no 8px corner radius and no `…`.
  Comparable rows confirm x=1059 is the clip edge, not the button edge: at y=118,
  y=158 and y=300 that same column is the panel's `#252A33` border.
- macOS Vision OCR of the render returns `Run selected` at (835,146), `Resume` at
  (952,147) and a stray **`Ma` at (1038,146)**. It returns **no `TASK` run anywhere
  in the image**, while it does return `QUEUE` (27,144), `ACTIVITY` (1234,142) and
  `STAGES` (355,607).
- The one-line bar cannot fit by a wide margin. Children through `Resume` end at
  x=1020, so `Mark done` starts at x=1029. The narrowest text button in the bar,
  `Resume`, measures 78 px (13 px padding each side, ink 955..1006), and
  `Mark done` is a longer label — so the bar's natural one-line width is **at least
  751 px against a 686 px box, an overflow of at least 65 px**, before the title and
  before `Reset`.
- `ResetButton` is entirely invisible whenever it is shown. It is laid out after
  `Mark done`, so its left edge is at x >= 1030 + 78 + 8 = **1116**, past the panel's
  right border at x=1059. It is hidden in this particular render only because the
  harness task at `tools/VisualRelay.Screenshots/Program.cs:104` has no
  `ReviewReason`, so `IsResetButtonVisible`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Reset.cs:32`) is false.
- The defect is stable, not a one-off capture: the header geometry on
  `.relay/show-stage-tier-on-stage-cards/visual-review/main.png` (2026-08-19) is
  identical run-for-run — same gaps at 391-398, 548-555, 813-820, 933-940,
  1021-1028, same face start at 1030, same cut at 1059/1060.
- The `Auto` column measures its child unconstrained. The rendered proof is that
  the bar is arranged at >= 703 px (x=357 through the clip at x=1059) inside a
  686 px box — only possible if measure passed it more width than existed.
- The title itself is fine and the idiom is fine.
  `src/VisualRelay.App/Views/Controls/StageBoard.axaml:14` uses the same
  `<Grid ColumnDefinitions="*,Auto,Auto">` with a `panelTitle` in the `*` column
  (lines 16-18), and `STAGES` renders ink at **x=357..403 (47 px)** — the very x
  the detail panel's action bar starts at. `TASK` is four characters in the same
  12px SemiBold `#9AA3B1` style (`VisualRelayTheme.axaml:125-129`) and needs on
  the order of 32 px.
- Avalonia is **12.0.5** (`src/VisualRelay.App/VisualRelay.App.csproj:18`). Its
  `WrapPanel` exposes `Orientation`, `ItemSpacing`, `LineSpacing`, `ItemsAlignment`
  (`Start`/`Center`/`End`), `ItemWidth` and `ItemHeight` — confirmed in
  `~/.nuget/packages/avalonia/12.0.5/ref/net10.0/Avalonia.Controls.xml`. There is
  no `Spacing` property; `Spacing="8"` does not carry over.
- No style selector targets `StackPanel` anywhere under
  `src/VisualRelay.App/Styles/`, so changing the bar's panel type cannot drop a
  style.
- `tests/VisualRelay.Tests/TaskActionBarLayoutTests.cs` (151 lines) is the only
  test that touches the bar. Its three facts resolve the button via
  `taskActionBar.FindNameScope()?.Find("MarkDoneButton")` (lines 46, 97, 144) and
  assert `IsVisible` and `Command`. It never names `StackPanel`, `Spacing`, or any
  bounds, so it survives this change unmodified.
- No test pins the header's geometry or the title. The literal `"TASK"` appears in
  exactly one place in the repository: `TaskDetailPanel.axaml:18`.
- `TaskDetailScrollBottomReachabilityTests`, `TaskDetailRemoveButtonLayoutTests`
  and `TaskDetailAttachmentRevealButtonLayoutTests` assert relative offsets inside
  the markdown and attachment areas, not the header, so a taller header does not
  break them.

### What to build

Make the task header fit: the `TASK` title always renders, and the action bar
wraps onto a second line instead of overflowing the panel.

Two edits are required and **neither works alone**. An `Auto` column measures its
child at infinite width, so a `WrapPanel` placed there computes a single-line
desired width and never wraps — swapping the panel by itself changes nothing.
Equally, a column swap by itself leaves a non-wrapping `StackPanel` in a finite
`*` column, which still reports its full one-line desired width and is still
clipped. The in-repo precedent is `StageBoard.axaml:42`, where the `WrapPanel`
sits inside a `ScrollViewer` (lines 35-38) in a `*` row — a finite-width measure.

1. **Column swap.** In `TaskDetailPanel.axaml:17`, change
   `ColumnDefinitions="*,Auto"` to `ColumnDefinitions="Auto,*"` and add
   `ColumnSpacing="12"`. Fold both onto the existing `<Grid ...>` tag — do not add
   a line (see Constraints). The `TASK` `TextBlock` stays where it is and now sits
   in the `Auto` column, so it always gets its natural ~32 px. `TaskActionBar`
   already carries `Grid.Column="1"` at line 21, so that attribute is unchanged;
   it now sits in the `*` column and is measured at the real remaining width.
2. **Wrapping bar.** In `TaskActionBar.axaml`, change the root element from
   `StackPanel` to `WrapPanel`. Keep `Orientation="Horizontal"`. Replace
   `Spacing="8"` (line 8) with `ItemSpacing="8"` and `LineSpacing="8"`. Add
   `HorizontalAlignment="Right"` so the bar stays flush right when it does fit on
   one line. Leave all six children, their bindings, their order, their
   `MaxWidth`s and their tooltips exactly as they are.
3. **Code-behind base type.** `src/VisualRelay.App/Views/Controls/TaskActionBar.axaml.cs:5`
   declares `public partial class TaskActionBar : StackPanel`. Change the base type
   to `WrapPanel`. The XAML root type and the code-behind base type must agree or
   the Avalonia XAML compiler fails the build. `.OfType<TaskActionBar>()` in the
   tests is unaffected.
4. **Test the contract, headlessly and scoped.** Add a fact that fails today and
   passes after. Construct the panel directly — do **not** boot a `MainWindow`
   (see Constraints):

   ```
   var vm = new MainWindowViewModel
   {
       StatusText = "Pause armed: finishing add-multiply-helper before stopping",
       SelectedTaskMetricLabel = "12 stages  2m 18s  $0.07"
   };
   var panel = new TaskDetailPanel { DataContext = vm };
   var window = new Window { Content = panel, Width = 620, Height = 420 };
   window.Show();
   Dispatcher.UIThread.RunJobs();
   ```

   Do not set `RootPath` or `SelectedTask`: assigning `SelectedTask` kicks an async
   disk load (see the comment at `src/VisualRelay.App/DesignTime/DesignData.cs:41-45`).
   With no `SelectedTask` the `Mark done` and `Reset` buttons are hidden, and the
   five always-visible children still measure ~664 px against the ~586 px content
   box a 620 px panel leaves, so the overflow reproduces. Then assert both halves
   of the defect:
   - the `TextBlock` whose `Text` is `"TASK"` has `Bounds.Width > 0`;
   - the `TaskActionBar`'s top-right corner, translated into the panel, has
     `X <= panel.Bounds.Width` — i.e. the bar does not overflow. Use the
     `TranslatePoint(new Point(control.Bounds.Width, 0), container)` pattern from
     `tests/VisualRelay.Tests/TaskDetailRemoveButtonLayoutTests.cs:112`.

   Do not assert an exact wrapped line count, an exact header height, or exact
   pixel widths — those are font-metric dependent and brittle.

**What the 1440x900 visual-review render must show once fixed.** The render is
`dotnet run --project tools/VisualRelay.Screenshots -- {outDir}/main.png` at its
default 1440x900 (`tools/VisualRelay.Screenshots/Program.cs:13-14`), with the
detail panel spanning x=340..1059:

- A grey `TASK` label (`#9AA3B1`, 12px SemiBold) at the header's left content
  edge, x ≈ 357 — the same x at which `STAGES` renders in the panel below.
- The complete label `Mark done` legible, with the button's rounded right edge
  and 1px border fully inside the panel: its rightmost painted pixel at or before
  x=1058, not touching the border column at x=1059.
- The action bar wrapped onto a second line, right-aligned beneath the first. The
  header is correspondingly taller and the markdown area below is shorter. That is
  the intended outcome, not a regression.
- `Reset` still hidden — the harness task has no `ReviewReason`.
- No text or button face cut off at the panel's right edge anywhere in the
  header, and nothing else in the window shifted horizontally: the queue rail, the
  splitter and the ACTIVITY column keep their present positions.

### Out of scope

- Do not remove or weaken `ClipToBounds="True"` at `TaskDetailPanel.axaml:9` (or
  the matching one at `StageBoard.axaml:8`). Clipping is correct; the overflow is
  the bug.
- Do not change the chips' `MaxWidth` values (220 at `TaskActionBar.axaml:15`, 260
  at line 23), their `MaxLines`, or their `TextTrimming="CharacterEllipsis"`.
- Do not change any button label, tooltip, `Command` binding, `Appearance`, or
  `IsVisible` binding, and do not reorder or remove any of the six children.
- Do not rename `TaskActionBar`, `MarkDoneButton` or `ResetButton` — the name
  scope is load-bearing for `TaskActionBarLayoutTests`.
- Do not introduce an overflow/kebab menu, an icon-only compact mode, responsive
  breakpoints, or any width-driven visibility logic in the view model.
- Do not touch `MainWindow.axaml` column sizing, the `ActivitySplitter`, or
  `ActivityColumnWidth`.
- Do not modify `tools/VisualRelay.Screenshots/Program.cs` or `.relay/config.json`
  to make the defect easier to see — the standard render already shows it.
- Do not change `StageBoard.axaml`'s header grid; it renders correctly today.
- Do not restyle `TextBlock.panelTitle` or `Border.panel` in
  `VisualRelayTheme.axaml`.

### Constraints

- Hard guard: no `.cs`/`.axaml` under `src/`, `tests/` or `tools/` may exceed
  300 lines (`tools/VisualRelay.Guards/FileSizeGuard.cs:13`; the check is
  `lines > limit` at line 35, so exactly 300 passes). Current counts:
  - `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` — **289 / 300, only
    11 lines of headroom.** Keep this file's edit to attribute changes on the
    existing `<Grid ...>` tag at line 17 so the count stays at 289: put
    `ColumnDefinitions="Auto,*"` and `ColumnSpacing="12"` on that one line. Do not
    add elements, comments or wrapper markup here. Anything that needs new markup
    belongs in `TaskActionBar.axaml`, which has 253 lines spare.
  - `src/VisualRelay.App/Views/Controls/TaskActionBar.axaml` — 47 / 300.
  - `src/VisualRelay.App/Views/Controls/TaskActionBar.axaml.cs` — 11 / 300.
  - `tests/VisualRelay.Tests/TaskActionBarLayoutTests.cs` — 151 / 300.
- New test placement. `SplitGuardVerificationTests.NoTestFile_BootsWholeAppOutsideAllowlist`
  flags `new MainWindow` in any test class not on the allowlist in
  `tests/VisualRelay.Tests/SplitGuardVerificationTests.WholeAppBoot.cs`.
  `TaskActionBarLayoutTests` is allowlisted at line 47; a new class would not be.
  Either add the new fact to `TaskActionBarLayoutTests.cs` (151/300, room for it)
  or create a new class that constructs `TaskDetailPanel` in a bare `Window` — but
  never `new MainWindow`, and never extend the allowlist.
- Headless UI tests use `[AvaloniaFact]` and `[Collection("Headless")]`;
  `HeadlessUnitTestSession` is banned by BannedApiAnalyzers and reintroducing it
  fails the build (`AGENTS.md:33-34`).
- Avalonia 12.0.5. Use `ItemSpacing`/`LineSpacing` on `WrapPanel`; `Spacing` does
  not exist there.
- No new packages or dependencies.
