# ACTIVITY panel: remove the awkward vertical line between the tab groups

In the right-hand **ACTIVITY** panel's tab bar, there is a vertical line separating
**Run Log | Commands** from **System | Input | Output**. It looks awkward and should be removed.
See `activity-tab-bar.png`.

## Facts established (bake in)

The line is a deliberate left-border on the **System** tab, used to group the first two
(run-scoped) tabs apart from the last three (stage-scoped) tabs. Two spots, both must go:

- `src/VisualRelay.App/Views/Controls/ActivityColumn.axaml` — the `System` `TabItem` carries
  `Classes="stageDivider"`:
  ```xml
  <TabItem Header="System"
           Classes="stageDivider">
    <controls:StageSystemView/>
  </TabItem>
  ```
- `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` — the style that draws it:
  ```xml
  <Style Selector="TabItem.stageDivider">
    <Setter Property="BorderBrush" Value="#3A404A"/>
    <Setter Property="BorderThickness" Value="1,0,0,0"/>
  </Style>
  ```

`stageDivider` is used **nowhere else** (verified by grep), so both the class usage and the style
can be deleted cleanly.

> **Freshness contract.** Confirm by searching for `stageDivider` in both files before editing; if a
> second usage has appeared since, adapt rather than leaving a dangling reference.

## Goal

No vertical divider line between the tab groups; the five tabs read as one even row. No other tab
styling changes.

## Approach

Remove `Classes="stageDivider"` from the `System` `TabItem`, and delete the now-unused
`TabItem.stageDivider` style. Leave tab spacing/selected-underline as they are.

## Tests

Lightweight headless check (style of `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs`):
build `ActivityColumn`, resolve the `System` `TabItem`, and assert its `BorderThickness` is `0`
(no left border) and that it carries no `stageDivider` class. Optional but cheap; the change itself
is tiny.

## Out of scope

Tab order, headers, the selected-tab underline, and the panel header row.

## Screenshot

- `activity-tab-bar.png` — the tab bar with the line to remove (between "Commands" and "System").
