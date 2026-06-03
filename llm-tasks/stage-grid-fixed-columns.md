# Stage grid is locked to 4 columns, truncating stage names

Stage names truncate ("Resear…", "Diagn…") while shorter names like "Plan" fit, because the
stage board uses a fixed 4-column grid that gives each card a narrow, width-independent slot.

`src/VisualRelay.App/Views/Controls/StageBoard.axaml:30`:

```xml
<ItemsControl.ItemsPanel>
  <ItemsPanelTemplate>
    <UniformGrid Columns="4"/>
  </ItemsPanelTemplate>
</ItemsControl.ItemsPanel>
```

With 11 stages forced into 4 fixed columns inside the center panel, each card is too narrow
for its name, and the name `TextBlock` (`:54`, `MaxLines="1" TextTrimming="CharacterEllipsis"`)
ellipsizes. The column count does not adapt to the available width.

## Recommended fix

Make the grid responsive instead of a fixed 4 columns: use a `WrapPanel` with a sensible card
`MinWidth`/`Width` (cards wrap to as many columns as fit and reflow as the panel resizes), or
a `UniformGrid` whose column count is computed from the available width. Cards should be wide
enough that the standard stage names ("Research", "Diagnose", etc.) display without
truncation at the supported window sizes.

## Done when

- At the supported window sizes, full stage names render without ellipsis; the grid reflows
  columns as the panel width changes rather than always showing 4.
- Verify with `./visual-relay screenshot` at desktop and compact widths.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
