# LLM COMMANDS scrollbar jumps/resizes ("bounces") while scrolling

Scrolling the LLM COMMANDS pane makes the scrollbar thumb jump around and change
size instead of tracking smoothly — the scroll position feels unstable as you
drag or wheel through the trace cards.

## Root cause (researched)

The LLM COMMANDS list is a `ListBox` with **no explicit `ItemsPanel`**
(`src/VisualRelay.App/Views/Controls/ActivityColumn.axaml:89-135`), so Avalonia
(12.0.4 — see `src/VisualRelay.App/VisualRelay.App.csproj`) uses its **default
`VirtualizingStackPanel`** for the items.

The trace cards have **highly variable heights**: the card's `Content`
`TextBlock` (`ActivityColumn.axaml:123-130`) is `TextWrapping="Wrap"` with no
`MaxLines`, and `TraceEntry.Content` (`src/VisualRelay.Domain/TraceEntry.cs`)
ranges from a one-line `ToolCall` to a many-line `AssistantText`/`Thinking`
block (the live screenshot shows a single card spanning ~10 wrapped lines).

A virtualizing panel only realizes the visible items and **estimates** the total
scroll extent from the average height of those realized items. With non-uniform
item heights that estimate is recomputed every time new (taller/shorter) cards
realize during a scroll, so the scrollbar thumb's size and position keep
shifting — the "bounce." Avalonia 12 has no scroll-anchoring/height-caching to
compensate, so this is inherent to virtualizing a variable-height list.

This is the same hazard the STAGES board already avoids: `StageBoard.axaml:27-35`
deliberately renders its items in a non-virtualizing panel (`WrapPanel`) inside a
`ScrollViewer`, so its scroll extent is the true content size.

The RUN LOG list above it (`ActivityColumn.axaml:39-64`) shares the same default
virtualization, but its rows are mostly compact and only `warn`/`error` lines
wrap, so it bounces far less; the fix should at least consider it.

## Recommended fix

Stop virtualizing the LLM COMMANDS list so its `ScrollViewer` measures the real
content height and the scrollbar tracks it exactly:

- Give the LLM COMMANDS `ListBox` a non-virtualizing items panel:
  ```xml
  <ListBox.ItemsPanel>
    <ItemsPanelTemplate>
      <StackPanel/>
    </ItemsPanelTemplate>
  </ListBox.ItemsPanel>
  ```
  A `ListBox` keeps its built-in `ScrollViewer` (it wraps the `ItemsPresenter`),
  so swapping the panel only disables virtualization — scrolling still works, but
  the extent is now the true total height and the thumb stays stable.
- Apply the same to the RUN LOG list if it exhibits the same bounce.

Trade-off to weigh: a non-virtualizing panel realizes every item. That is
acceptable here because the trace set is bounded — a stage filter / the
latest-attempt selection (see `DONE-attempt-number-hardcoded-overwrites-reruns.md`)
keeps the visible session small (tens of cards, as the count chip shows). If a
single unfiltered session could grow into the thousands, prefer keeping
virtualization and instead mitigating (e.g. a sensible `MaxLines` cap on the card
`Content` so item heights are bounded/uniform, with hover/expand for the rest) —
call out which approach you took and why.

Watch for a confounder while verifying: confirm the bounce is the virtualization
estimate and not live trace insertion moving the viewport mid-scroll (traces are
appended as a run streams). If live insertion also shifts the scrollbar, note it
separately — the panel change fixes the manual-scroll bounce regardless.

## Done when

- Scrolling (wheel and drag) through a populated LLM COMMANDS pane keeps the
  scrollbar thumb proportional and stable — no jumping or resizing — and the
  list still scrolls to the first and last card.
- Whatever approach is chosen (de-virtualize vs. bound item height) is justified
  against the trace-set size in the commit/PR.
- Verify with `./visual-relay screenshot` (and, ideally, a manual scroll in a
  real `./visual-relay launch`, since a static screenshot can't show the bounce).
- This is presentation-only; no view-model logic changes are expected, so there
  is no unit test to write first — but if any VM/converter logic is added,
  cover it test-first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
