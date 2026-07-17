## Task: Fix text contrast in the Run All protocol dropdown

The two-line Run All mode dropdown added by
`00-restart-between-tasks-run-protocol` renders with inaccessible text
contrast in the app's (forced-dark) theme, observed 2026-07-16 in the live
app:

- Collapsed: the selected protocol name ("Standard") renders near-black on
  the dark top bar — effectively invisible.
- Expanded: the description lines are very dim, and the unselected rows are
  inconsistent with each other (the "Sequential" row's name and description
  render noticeably dimmer than the "Restart Between Tasks" row's).

### Evidence (verified)

- `src/VisualRelay.App/Views/Controls/TopBar.axaml:132-151`: the item
  template's name and description TextBlocks and the selection-box name
  TextBlock all set `Foreground="{DynamicResource ThemeForegroundBrush}"`;
  the description additionally dims with `Opacity="0.6"` at `FontSize="11"`.
- `ThemeForegroundBrush` is defined NOWHERE in this repo (grep), and the app
  uses `FluentTheme` (`src/VisualRelay.App/App.axaml:12`) with
  `RequestedThemeVariant="Dark"` (`App.axaml:5`). `ThemeForegroundBrush` is a
  key from Avalonia's Simple theme — Fluent does not provide it. The lookup
  therefore never resolves and the intended color never applies; what renders
  is context-dependent fallback (near-black default in the selection box,
  state-inherited colors in the popup), which matches both observed symptoms,
  including the row-to-row inconsistency.
- There is no centralized brush palette to pick up: `Styles/VisualRelayTheme.axaml`
  (included at `App.axaml:13`) defines only `TaskCardItemTheme`. Sibling
  top-bar text uses inline palette hexes (`#F2F5FA` at 14px, `#7F8794` at
  11px, `#9AA3B1`, `#5A6370` — `TopBar.axaml:28-67`).
- Contrast math: 11-13px is WCAG "normal size" text, so AA requires ≥ 4.5:1
  (the 3:1 large-text allowance needs ≥ 24px, or ≥ 18.66px bold). White at
  60 % opacity composited over Fluent's dark flyout background lands well
  below 4.5:1, before any state background is applied.

### What to build

1. **Make the colors real.** Replace the unresolvable `ThemeForegroundBrush`
   references with brushes that exist: add keyed text brushes (primary +
   muted) to `Styles/VisualRelayTheme.axaml` as the start of a centralized
   palette, or use explicit hexes consistent with the surrounding top-bar
   text. No `DynamicResource` key that does not resolve under the app's
   actual theme.
2. **Meet AA contrast in every state.** Name and description must each hit
   ≥ 4.5:1 against the actual composited background of: the collapsed
   selection box, and popup items at rest / pointer-over / selected /
   selected + pointer-over. Replace the `Opacity="0.6"` dimming with an
   explicit muted color chosen for contrast — opacity compounds with state
   backgrounds unpredictably.
3. **Make unselected rows uniform.** All unselected popup rows must render
   identically at rest; brightness may vary only through deliberate state
   styling that itself meets the contrast floor.
4. **Guard the whole class of bug.** Add a headless test that collects every
   `{DynamicResource <key>}` referenced by the app's `.axaml` files under
   `src/VisualRelay.App` and asserts each key resolves (non-null) against the
   running app's resources/theme. This fails today on the three
   `ThemeForegroundBrush` references and prevents any future unresolvable
   key from shipping invisible text again.
5. **Preserve the shipped behavior** from task 00: two-line popup items,
   name-only collapsed state, `AutomationProperties.Name`, the three-mode
   tooltip, and keyboard navigation.

### Constraints

- Styling only — no change to drain/protocol behavior, view models, or the
  control API.
- The app forces the Dark variant today; do not build a light theme, but
  keep the chosen colors centralized so a future variant edits one place.
- Keep files under the 300-line guard; repo-agnostic (nothing here may
  depend on the target repo the pipeline runs against).

### Tests (red first)

- Extend `tests/VisualRelay.Tests/RestartBetweenTasksUiTests.cs`: realize the
  collapsed ComboBox and assert the selection-box name TextBlock's effective
  `Foreground` equals the intended palette brush — fails today (unresolved
  resource fallback).
- Contrast test: compute the WCAG relative-luminance contrast ratio for name
  and description brushes against each state's composited background color
  (enumerate the state/background pairs in the test) and assert ≥ 4.5 —
  description fails today.
- Resource-resolution guard from "What to build" #4 — fails today with
  exactly the three `ThemeForegroundBrush` hits.

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: screenshot the collapsed control and the open popup in the dark
  app; every name and description plainly legible.
