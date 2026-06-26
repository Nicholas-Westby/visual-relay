# Fix Rewrite-with-AI confirm button text alignment

When the "Rewrite with AI" confirmation opens, the **Rewrite and Replace** button's label is glued to the top of the button, while the adjacent **Cancel** button's label looks vertically centered. Vertically center the confirm button's label to match Cancel.

## Current state (researched)

The shared confirmation dialog is built entirely in code in `src/VisualRelay.App/App.axaml.cs` → `ShowConfirmationAsync(Window owner, string title, string message, string confirmLabel)`. Its button row has two buttons:

- **Cancel** — `new Button { Content = "Cancel", Width = 80, Height = 32 }`. No overrides, so it inherits the Fluent theme's `ButtonPadding` (which has a vertical component).
- **Confirm** — `new Button { Content = confirmLabel, MinWidth = 80, Padding = new Thickness(12, 0), Height = 32, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center }`. The `Padding = (12, 0)` and `HorizontalContentAlignment` were added in commit `e96a235` ("label rewrite confirm button as rewrite and replace") to fit the longer "Rewrite and Replace" label without clipping; the matching vertical alignment was omitted.

Root cause (confirmed against Avalonia 12.0.4): the Fluent `Button` `ControlTheme` sets the button's own `VerticalAlignment="Center"` but does **not** set `VerticalContentAlignment`, and `ContentControl.VerticalContentAlignmentProperty` is registered with no default, so it falls to `default(VerticalAlignment)` = `Top`. Both buttons therefore top-align their content — but Cancel's theme `ButtonPadding` insets the text so it *looks* centered, whereas the confirm button's `Padding = (12, 0)` zeroes the vertical padding and exposes the `Top` alignment, gluing the label to the top edge.

The dialog is shared: `MainWindowViewModel.Rewrite.cs` (`RewriteSelectedTaskAsync`) passes `"Rewrite and Replace"`; `MainWindowViewModel.Authoring.cs` (attachment-removal confirmation) passes `"Delete"`. Both use the same confirm button, so the fix corrects both.

## What to build (TDD-first)

1. **Failing test first.** Add `tests/VisualRelay.Tests/ConfirmationDialogButtonAlignmentTests.cs` with `[Collection("Headless")]` and an `[AvaloniaFact]` structural guard, mirroring the style of `TaskDetailRemoveButtonLayoutTests` (which asserts a layout property on a located button). Assert the confirm button has `VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center`, and that `HorizontalContentAlignment == Center`, `Height == 32`, and `Content` equals the passed label are preserved.
   - The dialog is a transient `ShowDialog` window not present in `MainWindow`'s tree, and tests fake `MainWindowViewModel.ShowConfirmationAsync`, so the confirm button is currently unreachable. Expose it: extract the confirm-button construction into `internal static Button CreateConfirmButton(string confirmLabel)` in `App.axaml.cs`, and have `ShowConfirmationAsync` add that button to the row (the existing `confirmBtn = (Button)buttons.Children[1]` retrieval still works). `InternalsVisibleTo("VisualRelay.Tests")` already exists in `src/VisualRelay.App/Properties/AssemblyInfo.cs`.
2. **The fix.** In `CreateConfirmButton`, set `VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center` alongside the existing `HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center`. Leave `MinWidth = 80`, `Padding = new Thickness(12, 0)`, and `Height = 32` unchanged — they prevent the long label from clipping. Do not touch the Cancel button.
3. Confirm the new test goes green and the existing confirmation tests (`RewriteMutualExclusionTests`, `AddAttachmentsVisibilityTests`) stay green — the `ShowConfirmationAsync` delegate signature is unchanged.

## Done when

- `./visual-relay check` passes.
- The confirm button in `App.ShowConfirmationAsync` (built via `CreateConfirmButton`) has `VerticalContentAlignment = VerticalAlignment.Center`; its label renders vertically centered, matching Cancel.
- The new `[AvaloniaFact]` structural guard passes and asserts the property.
- "Rewrite and Replace" still fits on one line (no clipping); `MinWidth`/`Padding`/`Height` unchanged from the prior fix.

## Guardrails

- `./visual-relay check` must pass.
- `App.axaml.cs` stays well under 300 lines (currently ~142; the extraction adds little) — no partials needed.
- Headless UI test uses `[AvaloniaFact]` and `[Collection("Headless")]` (Avalonia's process-global dispatcher runs serially).
- Conventional Commit subject: `fix(ui): center rewrite-confirm button text vertically`.
- Minimal diff: change only the confirm button's vertical alignment plus the test scaffolding to reach it; do not reformat the dialog or unrelated code.
- No per-machine state is involved; nothing is written to XDG or the repo by this change.
