# Stage number sits on its own line above the name, wasting vertical space

Each stage card stacks the two-digit number ("01") on its own line directly above the stage
name ("Ideate"), so every card spends a whole extra row on the ordinal. Putting the number on
the same line as the name — e.g. "01 Ideate" — frees that row and lets the cards (and the whole
STAGES board) be more compact.

`src/VisualRelay.App/Views/Controls/StageBoard.axaml:49`:

```xml
<Grid RowDefinitions="Auto,*,Auto,Auto">
  <TextBlock Text="{Binding Ordinal}"
             FontFamily="Menlo,Consolas,monospace"
             FontSize="11"
             FontWeight="Bold"
             Foreground="{Binding AccentBrush}"/>
  <TextBlock Grid.Row="1"
             Text="{Binding Name}"
             FontWeight="SemiBold"
             Foreground="#E7ECF3"
             MaxLines="1"
             TextTrimming="CharacterEllipsis"
             VerticalAlignment="Center"/>
  <!-- rows 2-3: StatusLabel, MetricLabel -->
</Grid>
```

The ordinal (`Ordinal`, accent-colored monospace) is on grid row 0 and the name (`Name`) on
row 1, so they render as two separate lines.

## Recommended fix

Render the number and name on a single line. Combine the two top `TextBlock`s into one
horizontal row — e.g. a `StackPanel Orientation="Horizontal"` with a small `Spacing`, or a
two-column inner `Grid` — holding the ordinal then the name, and drop the outer grid to three
rows (`Auto,Auto,Auto`) so the freed vertical space is actually reclaimed.

Keep the two pieces visually distinct, as today: the number stays accent-colored monospace/bold
(`AccentBrush`), the name stays `SemiBold`/`#E7ECF3`. The name `TextBlock` must keep
`MaxLines="1"` + `TextTrimming="CharacterEllipsis"` and take the remaining width so long names
still trim instead of wrapping or widening the fixed 165px card (`:39`). Vertically align the
number with the name (shared baseline or centered) so "01 Ideate" sits cleanly on one line.
With the row freed, lower the card `MinHeight="86"` (`:43`) so the board gets visibly shorter
rather than leaving the gap behind.

## Done when

- Each stage card shows the number and name on one line (e.g. "01 Ideate"), with the number
  still visually distinct (accent color / monospace) from the name.
- Long stage names still trim with an ellipsis and don't wrap or widen the 165px card.
- Cards reclaim the freed row — the STAGES board is visibly more compact, not taller.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
