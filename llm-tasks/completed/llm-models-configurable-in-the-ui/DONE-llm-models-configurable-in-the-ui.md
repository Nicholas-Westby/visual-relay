# LLM Models Configurable in the UI

Let the user pick which concrete model each tier uses, from a dropdown per tier in Settings → "Live Tiers". Today that panel is read-only: it shows the auto-resolved model per tier but offers no way to change it. Add a curated dropdown (≤6 options per tier, drawn only from the five provider APIs already in use — Hugging Face, DeepSeek, Moonshot, Anthropic, OpenAI), persist the choice per-repo, and wire it through so the generated backend config actually honors it.

## Current state (researched)

- **Tiers** live in `BackendConfigGenerator.Chains` (`src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs`), a `Dictionary<string, List<(string Model, string RequiredKey)>>` keyed by `cheap`, `balanced`, `frontier`, `vision`, `claude`, `fallback`.
- **Real models** are the `model_name` entries in `tools/backend/litellm-config.yaml` `model_list`: `glm-5.2`(HF), `kimi-k2`(Moonshot), `deepseek-v4-pro`/`deepseek-v4-flash`(DeepSeek), `hf-qwen3-coder-next`(HF), `hf-qwen3-vl-235b`/`hf-qwen3-vl-30b`(HF), `claude-opus-1m`/`claude-sonnet`(Anthropic), `gpt-5`(OpenAI). All five providers are represented; `gpt-5` is defined and priced (`RelayPricing.Default["gpt-5"]`) but currently sits in no tier chain.
- **Resolution** is automatic: `BackendConfigGenerator.ResolveTiers(ISet<string> presentKeys)` picks each tier's first chain candidate whose key is present; `GetTierRows(presentKeys)` and `Generate(presentKeys, templatePath)` take **only** `presentKeys` today — there is no override path.
- **UI**: `SettingsPanel.axaml` "Live Tiers" `ItemsControl` binds `LitTierRows` with a `DataTemplate x:DataType="cfg:BackendConfigGenerator+TierConfigRow"` rendering `Tier`/`Model`/`ProviderName`/`KeyPresent` as static `TextBlock`s (no input). `TierConfigRow` is an immutable `record`.
- **VM**: `MainWindowViewModel.Keys.cs` — `LitTierRows` is `ObservableCollection<BackendConfigGenerator.TierConfigRow>`; `RefreshLitTiers()` calls `GetTierRows` (+ `Generate` for the summary). `RefreshKeyStatesAsync()` rebuilds it.
- **Effect path**: `BackendLifecycle.LaunchProxyAsync` → `BackendConfigStep.ResolveAsync(_paths, _options.RepoRoot, …)` → `BackendConfigStep.Generate` reads env keys and calls `BackendConfigGenerator.Generate(present, template)`, writing `.relay-scratch/litellm-config.generated.yaml`. Applied at backend start.
- **Persistence pattern**: `RelayConfigWriter.UpsertX` does read-modify-write on `.relay/config.json` preserving all other keys (e.g. `UpsertCommitProofArtifacts`); `RelayConfigLoader.TryLoadAsync` reads into `RelayConfig`; the VM's `OnXChanged` calls the writer. `RelayConfig` already has `TierProfiles` (tier→profile name).

## What to build (TDD-first)

1. **Curated selectable lists.** Add to `BackendConfigGenerator` a `SelectableModels` map: tier → ordered list of `model_name`s (≤6 each, only the five providers, only real `model_list` models). Use exactly these (defaults match today's auto-resolution):
   - `frontier`: `glm-5.2`, `kimi-k2`, `deepseek-v4-pro`, `claude-opus-1m`, `gpt-5`, `hf-qwen3-coder-next`
   - `balanced`: `deepseek-v4-pro`, `kimi-k2`, `deepseek-v4-flash`, `gpt-5`, `hf-qwen3-coder-next`, `claude-sonnet`
   - `cheap`: `deepseek-v4-flash`, `deepseek-v4-pro`, `hf-qwen3-coder-next`, `gpt-5`
   - `vision`: `hf-qwen3-vl-235b`, `hf-qwen3-vl-30b`, `kimi-k2`
   - `claude`: `claude-opus-1m`, `claude-sonnet`
   - `fallback`: `hf-qwen3-coder-next` — **not user-editable** (it is the always-available HF floor; keep it display-only).
2. **Override-aware resolution.** Thread an optional `IReadOnlyDictionary<string,string>? overrides` through `BackendConfigGenerator.ResolveTiers` / `GetTierRows` / `Generate`. Semantics, final: when a tier has an override **and** that model's required key is present, the override is the alias and the fallback chain is the remaining survivors (still terminating in `fallback` for non-`claude` tiers). When the override's key is absent, **ignore it** and auto-resolve — preserving the existing dead-primary-elimination guarantee so a missing key never breaks boot. `GetTierRows` rows additionally expose `SelectableModels` and `IsEditable` (`false` only for `fallback`).
3. **Persist + load.** Add `RelayConfig.TierModelOverrides` (`IReadOnlyDictionary<string,string>?`, default null). `RelayConfigLoader.TryLoadAsync` reads a new `tierModelOverrides` object from `.relay/config.json`, dropping any entry whose value is not in that tier's `SelectableModels`. Add `RelayConfigWriter.UpsertTierModelOverrides(rootPath, IReadOnlyDictionary<string,string>)` mirroring `UpsertCommitProofArtifacts` (read-modify-write, preserve other keys).
4. **VM wiring** (`MainWindowViewModel.Keys.cs`): replace the bare `TierConfigRow` binding with a per-tier observable row carrying two-way `SelectedModel`, `SelectableModels`, `IsEditable`, `ProviderName`, `KeyPresent`. Hydrate `SelectedModel` from the loaded `RelayConfig.TierModelOverrides` (falling back to the auto-resolved model) on `LoadInitialAsync`/`OpenSettingsAsync`. On `SelectedModel` change: `RelayConfigWriter.UpsertTierModelOverrides` then `RefreshLitTiers`.
5. **UI** (`SettingsPanel.axaml`): in the "Live Tiers" `DataTemplate`, replace the static `Model` `TextBlock` with a `ComboBox` (`ItemsSource` = `SelectableModels`, `SelectedItem` two-way = `SelectedModel`, `IsEnabled` = `IsEditable`). Keep the tier name + provider/key dot as today.
6. **Effect path**: `BackendConfigStep.Generate` loads `RelayConfig.TierModelOverrides` from the repo root (or receives them from `BackendLifecycle.LaunchProxyAsync`, which already has `_options.RepoRoot`) and passes them to `BackendConfigGenerator.Generate`, so the generated `litellm-config.generated.yaml` honors overrides at the next backend (re)start. State this "applies on next backend start" in the UI (a short note under Live Tiers).

**Test order (write red first):** `BackendConfigGeneratorTests` — override wins when key present; override ignored when key absent; chain still terminates in `fallback`; `SelectableModels` per-tier shape + ≤6; `GetTierRows` exposes `IsEditable`/`SelectableModels`. `RelayConfigLoader`/`RelayConfigWriter` — round-trip of `tierModelOverrides`, invalid entries dropped, other keys preserved. `SettingsPanelUiTests` (`[AvaloniaFact]`) — a `ComboBox` per editable tier with the right options; changing `SelectedModel` writes `tierModelOverrides` to `.relay/config.json` and survives reload; `fallback` row's combo is disabled.

## Done when

- Each editable tier (`cheap`, `balanced`, `frontier`, `vision`, `claude`) shows a dropdown with ≤6 options, all from the five in-use providers' real `model_list` models; `fallback` is display-only.
- Selecting a model persists `tierModelOverrides` to `.relay/config.json`, survives a config reload, and is reflected as the tier's alias in `BackendConfigGenerator.Generate` output **when its provider key is present**.
- An override whose key is absent is ignored at resolution (tier auto-resolves; boot never breaks).
- The fallback chain for every non-`claude` tier still terminates in `fallback`.
- `./visual-relay check` is green (file-size guard, format, build, tests, screenshot).

## Guardrails

- Run the full gate: `./visual-relay check`. Keep C# and Avalonia XAML source ≤300 lines — `BackendConfigGenerator.cs` (~269) and `SettingsPanel.axaml` (~295) are near the limit; split into partials (e.g. `BackendConfigGenerator.Selectable.cs`) or a small sub-control rather than bloating them.
- Core tests use xUnit v3 `[Fact]`; UI tests use `[AvaloniaFact]` + `[Collection("Headless")]`; `HeadlessUnitTestSession` is banned. **`BackendConfigGeneratorTests` is an oversized family**: any new `[Fact]` added there requires bumping `const int baseline = 159` in `SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline` (UI `[AvaloniaFact]`s are not counted).
- Conventional Commits directly on `main` (no PRs); the `commit-msg` hook enforces subject rules. Minimal diffs — change only what this task needs; do not reformat unrelated code.
- No new provider APIs or keys; reuse the existing five. No pricing changes (the run report's `model` is the tier/profile name, not the alias target, so pricing is unaffected and out of scope).
