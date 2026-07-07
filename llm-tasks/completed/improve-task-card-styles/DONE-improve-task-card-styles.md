# Improve Task Card Styles

Task cards in the queue currently collapse the active (selected/blue) and in-progress (running/green) states into a single green border because the view model's running-wins ternary hides the selected style. Add a distinct outer active ring so a running selected card shows blue outside green, and keep every card state rounded.

## Current state (researched)

- `src/VisualRelay.App/Views/Controls/QueuePanel.axaml` renders each task row as a `Border` with `Classes="queueCard"` that binds `Background="{Binding CardBackgroundBrush}"`, `BorderBrush="{Binding BorderBrush}"`, `BorderThickness="{Binding CardBorderThickness}"`, and `BoxShadow="{Binding CardShadow}"`.
- `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` defines `Border.queueCard` with `CornerRadius="8"` and a default dark gray border.
- `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs` resolves each card property with running precedence:
  ```csharp
  public IBrush BorderBrush => IsRunning ? RunningBorderBrush : IsSelected ? SelectedBorderBrush : WaitingBorderBrush;
  ```
  The same pattern applies to `CardBackgroundBrush`, `CardBorderThickness`, and `CardShadow`.
- `tools/VisualRelay.Screenshots/Program.cs` seeds a combined running+selected task (`RestoreRunningTaskState(task.Id, 3, "Diagnose")` plus `SelectedTask = task`), and `docs/images/visual-relay-main.png` shows that card as green only, with no visible blue selected ring.

## What to build

1. **Add outer-ring properties to `TaskRowViewModel`.**
   - `OuterSelectedRingBrush` returns `SelectedBorderBrush` when `IsRunning && IsSelected`, otherwise `Brushes.Transparent`.
   - `OuterSelectedRingThickness` returns `new Thickness(2)` when both flags are true, otherwise `new Thickness(0)`.
   - Include both in `NotifyVisualStateChanged`.

2. **Write the tests first in `tests/VisualRelay.Tests/TaskRowViewModelTests.cs`.**
   - Default: `BorderBrush` is waiting gray `#ff2a303a`; outer ring is transparent `#00ffffff` with zero thickness.
   - Selected only: `BorderBrush` is blue `#ff3191ff`; outer ring is transparent/zero.
   - Running only: `BorderBrush` is green `#ff5ad47d`; outer ring is transparent/zero.
   - Combined running+selected: `BorderBrush` is green `#ff5ad47d`; `OuterSelectedRingBrush` is blue `#ff3191ff` and `OuterSelectedRingThickness` is `2`.
   - Add a private `ColorOf(IBrush)` helper that casts to `ISolidColorBrush` and returns `Color.ToString()`.

3. **Wrap the card with the active ring in `QueuePanel.axaml` and `VisualRelayTheme.axaml`.**
   - In `VisualRelayTheme.axaml`, add a `Border.queueCardSelectedRing` style with `CornerRadius="10"`, `Background="Transparent"`, and default `BorderBrush="Transparent"` / `BorderThickness="0"`.
   - In `QueuePanel.axaml`, wrap the existing `queueCard` `Border` in an outer `Border` with `Classes="queueCardSelectedRing"`, binding `BorderBrush="{Binding OuterSelectedRingBrush}"` and `BorderThickness="{Binding OuterSelectedRingThickness}"`. Keep the inner card's `queueCard` class and its `CornerRadius="8"` unchanged.

4. **Keep files under the 300-line guard.**
   - `QueuePanel.axaml`, `TaskRowViewModel.cs`, `VisualRelayTheme.axaml`, and the test file must all stay under 300 lines; refactor if any file would exceed.

## Done when

- `./visual-relay test` passes.
- `./visual-relay check` passes (build, tests, file-size guard, format verification, screenshot render).
- The regenerated `docs/images/visual-relay-main.png` shows the seeded running+selected task with a rounded blue border outside its rounded green border, and no task card has square corners.
- Only task-queue cards are modified; `StageRowViewModel` and its tests remain untouched.

## Guardrails

- Use a Conventional Commit subject ≤72 chars, lowercase after the prefix, no trailing period.
- Headless UI tests must use `[AvaloniaFact]`/`[AvaloniaTheory]`; do not reintroduce `HeadlessUnitTestSession`.
- Keep C# and Avalonia XAML source files under 300 lines.

## Manifest
llm-tasks/improve-task-card-styles/improve-task-card-styles.md
