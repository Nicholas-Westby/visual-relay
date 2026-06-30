# Improve Recent Configurable Models Commit

Review the `feat(models): make models configurable` commit (current `HEAD~2`, `bb56580`), tighten the implementation, and remove the dead code it introduced. The visible symptom is the Settings → Live Tiers panel: when `ANTHROPIC_API_KEY` is absent, the `claude` tier still renders an enabled, empty ComboBox because its selected model is the placeholder string `"(key missing)"`, which is not in `SelectableModels`.

## Current state (researched)

- `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs` resolves tiers in `ResolveTiers` and omits `claude` when `ANTHROPIC_API_KEY` is absent, but `GetTierRows` adds it back with `Model = "(key missing)"` and `IsEditable = true`:
  ```csharp
  if (tier == "claude" && !aliases.ContainsKey("claude"))
  {
      rows.Add(new TierConfigRow(...)
      {
          SelectableModels = SelectableModels.TryGetValue("claude", out var sm) ? sm : [],
          IsEditable = true,
      });
  }
  ```
- `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` binds a `ComboBox` to `SelectableModels`/`SelectedModel` inside the `LitTierItems` `DataTemplate`; `IsEditable` only controls `IsEnabled`, so the missing-key `claude` row still draws an empty, enabled dropdown.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs` copies `IsEditable` from the `TierConfigRow` into `TierModelRow.IsEditable` and skips non-editable rows when persisting overrides.
- The commit added three files that are not referenced anywhere:
  - `src/VisualRelay.App/Views/Controls/StringSplitConverter.cs`
  - `src/VisualRelay.App/Views/Controls/OutputFieldKindEqualsConverter.cs`
  - `src/VisualRelay.Core/Execution/RelayDriver.ConvergenceGuard.cs`
- Existing tests assume every non-`fallback` row is editable:
  - `BackendConfigGeneratorTests.Selectable.GetTierRows_ExposesIsEditableAndSelectableModels`
  - `SettingsPanelUiTests.TierModelOverrides.LiveTiers_HasComboBoxPerEditableTier`

## What to build

1. **Add failing tests first.**
   - In `BackendConfigGeneratorTests.Selectable` (or a new partial), assert that `GetTierRows(presentKeys: ["HF_TOKEN"])` returns a `claude` row with `IsEditable == false` and `KeyPresent == false`.
   - In `SettingsPanelUiTests.TierModelOverrides`, assert that opening Settings with only `HF_TOKEN` yields no visible `ComboBox` for the `claude` row (i.e. the count of `ComboBox`es inside `LitTierItems` equals the count of editable rows, not the total row count).

2. **Update existing assumptions.**
   - Change `GetTierRows_ExposesIsEditableAndSelectableModels` so only tiers whose required key is present are expected to be editable; `fallback` and `claude` (when Anthropic is absent) may be non-editable.
   - Change `LiveTiers_HasComboBoxPerEditableTier` so the non-editable tier assertion accepts both `"fallback"` and `"claude"`.

3. **Fix tier-row editability.**
   - In `BackendConfigGenerator.GetTierRows`, when the `claude` key is missing set `IsEditable = false` (keep the row visible so users still see the tier exists, but it is not configurable until they add the key).

4. **Clean up the Live Tiers UI.**
   - In `SettingsPanel.axaml`, hide the `ComboBox` when `IsEditable` is false and show a muted `TextBlock` displaying `SelectedModel` instead. The `fallback` tier should also render as read-only text rather than a disabled dropdown.

5. **Remove dead code.**
   - Delete `StringSplitConverter.cs`, `OutputFieldKindEqualsConverter.cs`, and `RelayDriver.ConvergenceGuard.cs`.

6. **Run the suite.**
   - Run `./visual-relay test` and `./visual-relay check` and fix any failures.

## Done when

- `./visual-relay check` passes (build, file-size guard ≤300 lines, tests, screenshot render).
- `BackendConfigGenerator.GetTierRows` returns `IsEditable == false` for `claude` when `ANTHROPIC_API_KEY` is absent.
- The Settings Live Tiers panel renders non-editable tiers (`fallback`, missing-key `claude`) as read-only text, not as empty or disabled dropdowns.
- The three unused files listed above are gone.
- Commits follow the repo's Conventional Commit rules (`docs/commit-messages.md`) and remain focused; no unrelated files are touched.
