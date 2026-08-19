# Task: Show where each Live Tiers model is hosted

Settings shows each tier's resolved model (`frontier` → `glm-5.2`) but never says
which provider serves it. That distinction is operationally real: some models are
reached through the Hugging Face Inference Providers aggregator and some through a
vendor's own first-party API, and the two differ in pricing, availability, and
which key gates them. A reader of the settings screen cannot tell `glm-5.2`
(Hugging Face) from `deepseek-v4-pro` (DeepSeek first-party).

The provider name is already computed and already unit-tested — it is simply not
rendered.

### Evidence (2026-08-19)

- `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs:71-77` — the
  `ProviderNames` map turns each required env var into a display name
  (`HF_TOKEN` → "Hugging Face", `DEEPSEEK_API_KEY` → "DeepSeek", and so on).
  `GetTierRows` (line 96) sets `TierConfigRow.ProviderName` from it.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs:227-236` — the row
  view-model already copies `ProviderName` across.
- `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml:204-254` — the Live
  Tiers `DataTemplate` binds `Tier`, `KeyPresent`, and `SelectedModel`, but
  nothing binds `ProviderName`. Commit `e576e19` dropped the column when it
  changed the grid from four columns to `Auto,Auto,*`.
- `tests/VisualRelay.Tests/KeySetupPanelUiTests.cs:179-195` asserts the provider
  names on the row view-models, so the data path is covered and correct; only the
  view lost it.
- Two blockers must be cleared before a line can be added:
  `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` is exactly 300 lines
  and `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs` is 298 — the
  file-size guard fails above 300.
- The displayed model and `ProviderName` can disagree. `TryApplyOverride`
  (`BackendConfigGenerator.Selectable.cs`) ignores an override whose key is
  absent and lets auto-resolution proceed, but `MainWindowViewModel.Keys.cs:219-224`
  still displays that override whenever it appears in `SelectableModels`. With
  only `HF_TOKEN` set and `tierModelOverrides.frontier = "gpt-5"`, the row shows
  `gpt-5` while `ProviderName` describes the auto-resolved `glm-5.2`.
- `PersistTierOverrideAsync` (`MainWindowViewModel.Keys.cs:257`) writes config
  and does not rebuild `LitTierRows`, so a label bound to a value set only at
  build time would go stale the moment the user picks a different model.

### What to build

1. Add `BackendConfigGenerator.Providers.cs` (a new partial file, following
   `BackendConfigGenerator.Selectable.cs`) and move the `ProviderNames`
   dictionary into it so the main file loses lines rather than gains them. Add:

       public static string? ProviderFor(string model)

   returning the display name for `"fallback"` (Hugging Face), for any model in
   `ModelToKey`, and for any model in `ModelToRequiredKey` — and `null` for
   anything else. It must not route through `GetRequiredKey`: that helper
   defensively defaults unknown names to `HF_TOKEN`, which would label the
   `claude` row's `"(key missing)"` placeholder as Hugging Face.

2. Make the row view-model's provider follow the model actually displayed.
   In `MainWindowViewModel.Keys.cs`, set
   `ProviderName = BackendConfigGenerator.ProviderFor(selected) ?? row.ProviderName`
   so the `null` case preserves today's behaviour for `"(key missing)"`. In
   `MainWindowViewModel.TierModelRow.cs`, extend `OnSelectedModelChanged` to also
   update `ProviderName` when `ProviderFor(value)` is non-null, so the label
   tracks the ComboBox without waiting for a refresh or a backend restart.

3. Extract the Live Tiers section (`SettingsPanel.axaml:204-254`) into a new
   `LiveTiersSettings` user control under `Views/Controls/`, modelled exactly on
   `TimeoutSettings.axaml` / `TimeoutSettings.axaml.cs` (same `UserControl` shell,
   `x:DataType="vm:MainWindowViewModel"`, no code-behind beyond
   `InitializeComponent`). Replace the removed block in `SettingsPanel.axaml` with
   `<controls:LiveTiersSettings/>`, placed where the Border was. Keep the
   `LitTierItems` name on the `ItemsControl`.

4. In the extracted template, render the provider as a muted label to the right of
   the model, in the established secondary style (`Foreground="#7F8794"`, the same
   font size as the model text). Prefix it so it reads as routing rather than as
   the model's author — "via Hugging Face", not "Hugging Face" — because
   `glm-5.2` is a Z.AI model reached through Hugging Face. Give the model column a
   fixed width instead of `Auto` so the labels start at the same x on every row:
   each row is its own Grid, so `Auto` columns do not align across rows. The width
   must fit the longest selectable model name (`hf-qwen3-coder-next`) without
   clipping the ComboBox chevron.

5. Point the existing Live Tiers test lookups at the new control. Add a helper to
   `SettingsTestHelpers` that finds the `LitTierItems` `ItemsControl` by walking
   the dialog's visual descendants, and use it at all five current call sites
   (four in `SettingsPanelUiTests.TierModelOverrides.cs`, one in
   `KeySetupPanelUiTests.cs:211`). `panel.FindControl` cannot cross into the new
   control's name scope. `KeySetupPanelUiTests.cs` is also at exactly 300 lines,
   so that edit must not grow it.

6. Update `LiveTiers_RendersOneRowPerTier_WithModelTextAndStatusDots`
   (`KeySetupPanelUiTests.cs:198-226`) for the new column count, replacing the
   "no child at column 3" assertion with one that requires the provider
   `TextBlock` in that slot and checks its text.

7. Add tests covering the two ways the label can lie:
   - with `HF_TOKEN` only and `tierModelOverrides.frontier = "gpt-5"`, the
     `frontier` row must report OpenAI, not Hugging Face;
   - assigning a new `SelectedModel` on a `TierModelRow` (for example
     `frontier` from `glm-5.2` to `kimi-k2`) must move `ProviderName` to
     Moonshot with no refresh call.
   Add a `ProviderFor` unit test in the Core test suite covering the
   `"fallback"` alias, a `Chains` model, a `SelectableModelsByTier`-only model
   (`gpt-5`), and an unknown name returning `null`.

### Out of scope

- Naming the upstream vendor behind a Hugging Face route (for example
  "via Hugging Face (Z.AI)"). That needs a new model→vendor table with no
  existing source of truth, and a second table to keep from drifting.
- Any change to tier resolution, to `Chains`, to `SelectableModelsByTier`, or to
  `tools/backend/litellm-config.yaml`. Resolution behaviour must be identical
  before and after; this task only renders what is already resolved.
- The cost-per-model panel, which has its own model list and its own tests.
- Reconciling the override/auto-resolve disagreement in step 2's evidence at the
  resolution layer. The label must describe the model on screen; whether that
  model should be on screen at all is a separate question.
