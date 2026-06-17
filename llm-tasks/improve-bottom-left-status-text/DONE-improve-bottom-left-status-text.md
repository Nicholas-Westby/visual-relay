# Improve Display of Bottom Left Text

There is a status line at the **bottom left** of the window (the green text in the QueuePanel
footer). In the attached screenshot it reads `Planning queue-text-cut-off…` and the user
perceives it as cut off. It should not be cut off — or there should be a mechanism to read the
full text (e.g. a button that opens it in a scrollable view). By default we should be able to
see **at least 3 lines** of that text.

See the attached screenshot (`Screenshot 2026-06-16 at 9.33.14 PM.png`) — the target is the
green `Planning queue-text-cut-off…` line at the very bottom-left.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not
> by line number; if a snippet has drifted, re-read the file and adapt. `QueuePanel.axaml` is
> edited by several tasks, so the footer may have moved.

**The footer already wraps — recent work landed here.** `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`,
the `Grid.Row="2"` border at the bottom of the panel:

```xml
<Border Grid.Row="2" BorderBrush="#252A33" BorderThickness="0,1,0,0" Padding="16,12"
        IsVisible="{Binding !ShowHfGate}">
  <TextBlock Text="{Binding StatusText}"
             ToolTip.Tip="{Binding StatusText}"
             Foreground="#8FE3A2"
             MaxLines="4"
             TextWrapping="Wrap"/>
</Border>
```

So as of the most recent code it is `MaxLines="4"` + `TextWrapping="Wrap"` + a hover tooltip
(this came from the `readable-status-and-hf-gate-banner` task — **build on it, do not revert it**).
The screenshot was taken from a **running** build that likely predates that change, which is why
it shows a single truncated line.

**The trailing `…` is a literal progress marker, not truncation.** `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs`
sets `StatusText = $"Planning {taskId}…";`. So `Planning queue-text-cut-off…` is the *complete*
message — the ellipsis means "in progress", it is not the UI clipping the string. **Do not strip
these markers.**

**The real, still-unsolved gap: long messages.** Several status strings can far exceed 4 lines
and are then silently dropped, readable *only* by hovering for the tooltip (undiscoverable, no
scroll). All in `MainWindowViewModel.Execution.cs`:
- `StatusText = SwivalSubagentRunner.MissingToolsMessage(missingTools);` (multi-line tool list)
- `StatusText = $"Couldn't reach the model backend: {ex.Message}";`
- `StatusText = validation.RejectionReason ?? "test command validation failed";`

A 4-line cap with hover-only overflow is not a real "read the whole thing" affordance — that is
what this task fixes.

**Patterns to reuse for a full-text view (don't invent a framework):**
- `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` — the `IsVisible="{Binding HasSelectedTaskError}"`
  box uses a `ScrollViewer` + `TextWrapping="Wrap"`. Mirror that for a scrollable full-text view.
- `src/VisualRelay.App/Views/Controls/TopBar.axaml` — the Settings UI is a `Button.Flyout`. A
  `Flyout`/`Popup` anchored to the footer is the lightest "scrollable modal" and matches the app.

## What to build

XAML-led; add a VM member only if your approach needs one (e.g. a toggle/command), in which case
cover it with a headless test.

1. **Keep ≥3 lines visible by default.** The current `MaxLines="4"` already satisfies this —
   verify the footer `Border` auto-grows and renders 3–4 wrapped lines; do **not** drop below 3.
2. **Add an always-available full-text view** for overflow — not hover-only. Preferred minimal
   approach: make the footer clickable (or add a small affordance, e.g. a `⤢` / "Details"
   button) that opens a `Flyout`/`Popup` showing the full `StatusText` in a `ScrollViewer` with
   `TextWrapping="Wrap"` (reuse the `HasSelectedTaskError` styling). Keep the existing tooltip as
   a bonus, not the primary mechanism.
3. **Leave the literal `…` progress markers intact.**

## Tests / verification

- If you introduce a VM member (toggle bool / command), add a headless test for it. If the change
  is purely XAML, verify by inspection plus the manual smoke below and note it in the PR.
- **Manual smoke:** drive a long status (e.g. simulate backend-unreachable so `StatusText`
  becomes a long sentence) → the footer shows ≥3 wrapped lines, and the full-text view shows the
  *entire* message scrollably. A short `Planning …` message still renders cleanly.
- `./visual-relay check` green.

## Decisions (settled)

1. **Default ≥3 lines AND an explicit full-text view** (not hover-only). *Why:* the user asked
   for both — "at least 3 lines" and "a button that displays it in a scrollable modal." A tooltip
   alone is undiscoverable and unscrollable.
2. **Reuse the existing `ScrollViewer`/`Flyout` patterns; no toast/notification framework.**
   *Why:* a readable footer plus one expandable view solves the reported problem; a queue/toast
   system is out of proportion and explicitly out of scope.
3. **The trailing `…` is intentional, not a bug.** *Why:* it is a literal progress marker from
   `Execution.cs` ("Planning {id}…"), not UI clipping.

## Notes

- The screenshot is from a live/running build that may predate the `MaxLines="4"` + tooltip
  change; treat the current code (above) as the baseline and improve on it.
- **Coordination:** locate the footer `Border` by the quoted `Grid.Row="2"` / `Binding StatusText`
  string and re-read `QueuePanel.axaml` if another task reshaped it first.
