# Fix the Archive Day-Header Contrast, and Add the Missing Contrast Test

The archive day-group headers in the left sidebar (e.g. "Today ($3.31)", "Yesterday ($0.21)") are hard to
read — dim grey on a dark panel. Measured, the header text is `#5A6270` on the panel background `#12151B`,
a contrast ratio of **≈ 2.97:1** — below WCAG AA, which for this 11 px text requires **4.5:1**. Fix the
header to meet AA. And close the reason it slipped through: the repo has **no automated contrast test at
all**, so add one that catches this class of issue.

See `Screenshot-archive.png` in this folder — the dim "Today ($3.31)" header.

## Current state

**The header text.** Built by `src/VisualRelay.Core/Tasks/ArchiveDayGrouping.cs`, `HeadingFor(...)` — it
produces "Today" / "Yesterday" / a date, and appends the day's cost total like ` ($3.31)`. It is wired via
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs` (`row.DayHeader =
ArchiveDayGrouping.HeadingFor(...)`) onto the `DayHeader` property of `TaskRowViewModel`
(`src/VisualRelay.App/ViewModels/TaskRowViewModel.cs`).

**The failing render.** `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`, inside the
`ListBox.ItemTemplate` `DataTemplate` for `TaskRowViewModel`, the `Grid.Row="0"` header `TextBlock`:

```xml
<TextBlock Grid.Row="0" Text="{Binding DayHeader}" FontSize="11" FontWeight="SemiBold"
           Foreground="#5A6270" Margin="12,8,12,2"
           IsVisible="{Binding DayHeader, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>
```

- Foreground `#5A6270` = rgb(90, 98, 112). The header is a sibling of the task card, and the `ListBox` /
  `ListBoxItem` are transparent, so it shows through to the enclosing `<Border Classes="panel">` whose
  background is `#12151B` (from the `Border.panel` style in
  `src/VisualRelay.App/Styles/VisualRelayTheme.axaml`).
- 11 px SemiBold is **not** WCAG "large text" (which needs ≥18 px, or ≥14 px bold), so the required ratio is
  **4.5:1**. `#5A6270` on `#12151B` ≈ **2.97:1** → fails AA (and even the 3:1 UI floor).
- The **same** dim `#5A6270` literal is reused for two more elements in that same file — the "STATUS" flyout
  label and the "⤢" expand glyph — so they share the too-dim-grey problem and should be fixed together.

**Why no automated test caught it.** There is **no contrast / WCAG test anywhere in the repo** — grepping
`src` + `tests` for contrast / wcag / luminance / `0.2126` / `ContrastRatio` / `4.5` finds nothing. A prior
effort that was supposed to add one — centralizing colors into design tokens plus a WCAG contrast test and
an accessibility audit — **was marked complete but its code never landed**: there is no
`src/VisualRelay.App/Theme/` directory, no `Colors.axaml` / `Typography.axaml`, no `DesignToken*Tests.cs`,
`App.axaml` still has only `<Application.Styles>` (no `<Application.Resources>`), and there are zero
`DynamicResource` usages. So the palette is still ~235 raw inline hex literals and nothing checks their
contrast. (Making this header readable does **not** require doing that whole token overhaul — see scope.)

## What to build

1. **Fix the contrast.** Lighten the `DayHeader` foreground in `QueuePanel.axaml` from `#5A6270` to a grey
   that clears **≥ 4.5:1 on `#12151B`**. The `panelTitle` grey `#9AA3B1` already used for the "ARCHIVE" /
   section titles directly above it gives ~6:1 and is a natural, in-palette choice — but any value meeting
   4.5:1 that looks right against the panel is fine. Apply the same fix to the two sibling `#5A6270` usages
   (the STATUS label and the ⤢ glyph) so the too-dim grey isn't left elsewhere in the file.

2. **Add a real contrast test** so this can't regress and similar issues get caught. Create a test (e.g.
   `tests/VisualRelay.Tests/ContrastTests.cs`) with a small relative-luminance / WCAG contrast-ratio helper
   (sRGB → linear using the 0.2126 / 0.7152 / 0.0722 coefficients, then `(L1 + 0.05) / (L2 + 0.05)`), and
   assert the archive-header pair — and the other key text-on-panel pairs — meet 4.5:1 (or 3:1 only where
   the text genuinely qualifies as large).
   - **Design note learned from this bug:** a curated table keyed on *token brushes* structurally cannot
     catch an **untokenized inline literal** like `#5A6270`. So either (a) enumerate the *actual* inline
     fg/bg pairs (including this header and its two siblings) explicitly in the test, or — better — (b) have
     the test scan `QueuePanel.axaml` (and peer views) for `Foreground="#…"` on text, pair each with its
     resolved panel background, and assert the ratio, so a newly-added too-dim inline literal fails
     automatically. Prefer (b) if practical; at minimum do (a) and include the archive-header pair.

## Constraints & done criteria

- The archive day header (and the two sibling `#5A6270` usages) meet WCAG AA (**≥ 4.5:1**) on the `#12151B`
  panel; the test computes and asserts the chosen color's ratio.
- The contrast test genuinely guards this pair: it must **fail** on `#5A6270`-on-`#12151B` (confirm by
  temporarily reverting the color) and pass on the fixed color — i.e. it covers the inline literal, not a
  token that doesn't apply to it.
- **Scope is the contrast fix + the contrast test.** Full palette tokenization / typography / a full
  accessibility audit (the larger effort that never landed) is explicitly **out of scope** here — this task
  only has to make the header readable and add a test that catches this class of problem. (If the team wants
  the full token + accessibility overhaul, that is a separate, larger piece of work.)
- Keep every edited file within the **≤300-line** gate. Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.App/Views/Controls/QueuePanel.axaml` — lighten the `DayHeader` foreground and the two
  sibling `#5A6270` usages.
- `tests/VisualRelay.Tests/ContrastTests.cs` (new) — WCAG contrast-ratio helper + assertions covering the
  archive-header pair (and other text-on-panel pairs).
- (reference, no change) `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` (`Border.panel` `#12151B`),
  `src/VisualRelay.Core/Tasks/ArchiveDayGrouping.cs` (builds the header text),
  `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs` (`DayHeader`).
