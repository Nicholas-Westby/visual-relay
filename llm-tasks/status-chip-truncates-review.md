# Status chip truncates "… review" (fixed MaxWidth)

The blue status chip in the task detail header shows e.g. `2 pending · 1 revi…` — the queue
status is clipped even though the row has free space next to the "Run selected" button.

The chip has a hard `MaxWidth` that is too small for its text:

```xml
<!-- src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml:30 -->
<Border Classes="chip" MaxWidth="120">
  <TextBlock Text="{Binding StatusText}" ... TextTrimming="CharacterEllipsis"/>
</Border>
```

`StatusText` comes from `FormatQueueStatus` (`MainWindowViewModel.Helpers.cs:142`), which
produces `"{pending} pending · {review} review"` — wider than 120px, so it ellipsizes. The
sibling metric chip (`:22`, `MaxWidth="150"`) has the same fragile pattern.

## Recommended fix

Let the chip size to its content rather than a fixed cap: remove the `MaxWidth="120"` (and
review the `150` on the metric chip), allowing the chip to use the available width in that
header row; keep `TextTrimming` only as a last-resort fallback for genuinely long values. If
a cap is required for layout, raise it enough to fit the full "N pending · N review" text at
supported window sizes.

## Done when

- "2 pending · 1 review" (and similar) display in full without ellipsis at supported window
  sizes; the header row still lays out cleanly with the "Run selected" button.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
