# Harden the status-footer full-text flyout

Follow-up from the code review of `improve-bottom-left-status-text` (commit `5664cea`).
The feature works; these are robustness + test-strength improvements.

## Current state (researched)
`src/VisualRelay.App/Views/Controls/QueuePanel.axaml` footer adds a `⤢` button
(`StatusExpandButton`) opening a `Flyout > Border(MaxHeight=320, Width=340) > Grid(Auto,*) >
ScrollViewer > SelectableTextBlock{StatusText}`. The `ScrollViewer` has NO explicit height
bound — it relies on the `Border MaxHeight=320` + the `*` Grid row. In an edge case Avalonia
can measure the grid at infinite height and clip at the Border rather than scroll.
`tests/VisualRelay.Tests/StatusFooterFlyoutTests.cs` verifies the button/flyout/ScrollViewer
structure and visibility, but does NOT verify the `SelectableTextBlock.Text` binding actually
resolves to `StatusText`.

## What to build
1. Make the full-text view reliably SCROLL for very long text: give the `ScrollViewer` (or
   its content container) an explicit `MaxHeight` (≈ 280, i.e. Border 320 minus the "STATUS"
   header + padding), keeping `VerticalScrollBarVisibility="Auto"` and the inner
   `TextWrapping="Wrap"` + `HorizontalScrollBarVisibility="Disabled"`. Optionally add
   `VerticalAlignment="Stretch"` to the Grid. Verify a multi-paragraph message scrolls.
2. Strengthen the test: set `vm.StatusText = "<long unique string>"`, open the flyout
   (`expandButton.Flyout!.ShowAt(expandButton)`), `Dispatcher.UIThread.RunJobs()`, then assert
   the `SelectableTextBlock.Text` equals that string (binding actually resolves). Add an
   initial-state assertion so the visibility test isn't vacuous (note `StatusText` starts as
   "Idle", non-empty).
3. `./visual-relay check` green.

## Optional (Minor, from review)
- Consider whether the expand button should be hidden when `StatusText == "Idle"` (today it
  shows at launch because "Idle" is non-empty). Only if it reads as clutter; not required.

## Decisions (settled)
- Keep the existing MaxLines=4 + wrap + tooltip default behavior untouched.
