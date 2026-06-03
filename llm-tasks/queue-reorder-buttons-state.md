# Queue "Up" / "Down" buttons read as disabled and ignore position

The reorder buttons look greyed-out/inactive even when a task is selected and reordering is
possible, and they give no feedback at the list boundaries.

In `src/VisualRelay.App/Views/Controls/QueuePanel.axaml:35` the buttons use the default
(unclassed) button style — low contrast on the dark panel, so an *enabled* control looks
disabled. Functionally their `CanExecute` is `HasSelection`
(`MainWindowViewModel.Commands.cs:121,144`; `HasSelection` at `...Helpers.cs:169`), which is
position-blind: `MoveUp` is enabled even when the top item is selected and `MoveDown` when
the bottom item is selected — both then no-op (`Commands.cs:122-157`).

## Recommended fix

Drive enablement from the selected task's position so the standard disabled styling becomes
meaningful: add `CanMoveUp` (selection exists and index > 0) and `CanMoveDown` (selection
exists and index < count - 1), use them as the `MoveUp`/`MoveDown` `CanExecute`, and raise
their change notifications when `SelectedTask` or the queue changes. Pair this with a
higher-contrast enabled style (e.g. apply the existing button class used elsewhere) so an
actionable button never looks disabled.

## Done when

- With the top task selected, "Up" is visibly disabled and "Down" is clearly enabled (and
  vice-versa at the bottom); a mid-list selection enables both.
- A `MainWindowViewModel` test asserts `CanMoveUp`/`CanMoveDown` track the selected index.
  Write the failing test first.
- `./visual-relay check` green; `./visual-relay screenshot` shows legible button states;
  files under 300 lines; Conventional Commit.
