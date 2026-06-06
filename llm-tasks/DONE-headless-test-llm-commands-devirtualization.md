# Add a headless regression test for the LLM COMMANDS list de-virtualization

The scrollbar-bounce fix (`fix: de-virtualize LLM COMMANDS list to stop
scrollbar bounce`) replaced the default `VirtualizingStackPanel` on the trace
`ListBox` with a non-virtualizing `StackPanel`, but it shipped **without a test**
because the pipeline waved XAML changes through the red gate. XAML is testable —
the project has an Avalonia.Headless harness — so this regression should be
guarded: if anyone reverts the trace list to a virtualizing panel, a test must
fail.

## Current state (researched)

- The LLM COMMANDS list lives in
  `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml`: a `ListBox` bound to
  `TraceEntries` whose items panel is now an explicit non-virtualizing
  `<StackPanel/>` (`<ListBox.ItemsPanel><ItemsPanelTemplate><StackPanel/>…`). The
  other `ListBox` in the same control (RUN LOG, bound to `Events`) is
  intentionally left virtualized and is **not** in scope.
- A headless UI test harness already exists and is the pattern to follow:
  `tests/VisualRelay.Tests/HeadlessTestApp.cs` (the `[AvaloniaTestApplication]`)
  and `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs` (an `[AvaloniaFact]`
  that builds the app, shows a window, and inspects the realized control tree).
- There is currently **no** test that observes `ActivityColumn`'s items panels,
  so a regression to virtualization would be silent.

## What to build

Add an `[AvaloniaFact]` headless test (mirroring `ConfigInitEmptyStateUiTests`)
that realizes the activity column and asserts the **LLM COMMANDS** trace list
uses a **non-virtualizing** items panel:

- Locate the trace `ListBox` (the one bound to `TraceEntries`) in the realized
  control tree. Prefer a robust lookup; adding a stable `x:Name` to that `ListBox`
  in `ActivityColumn.axaml` is acceptable if it makes the test clean.
- Assert its items panel is a `StackPanel` and **not** a `VirtualizingStackPanel`
  (a `VirtualizingStackPanel` is itself a `Panel`/`StackPanel`-adjacent type, so
  assert the concrete type is `StackPanel` and is not virtualizing — fail if the
  realized panel virtualizes).
- Do not assert anything about the RUN LOG list; it stays virtualized by design.

Keep it a focused regression guard: it should fail if the `ItemsPanel` is removed
or changed back to the virtualizing default, and pass against the current code.

## Done when

- A headless test fails when the trace `ListBox` uses a virtualizing items panel
  and passes with the current non-virtualizing `StackPanel`.
- The RUN LOG list is untouched and unasserted.
- The test runs under the existing headless harness (`HeadlessTestApp`) like
  `ConfigInitEmptyStateUiTests`, with no new flakiness.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
  Commit subjects.
