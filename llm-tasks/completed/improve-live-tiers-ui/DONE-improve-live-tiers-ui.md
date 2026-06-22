# Make the "Live Tiers" settings panel readable (it's one dense text blob)

> Original ask: *"The 'Live Tiers' UI is basically just a text box. Hard to interpret what it's even
> trying to convey. Improve the UI to make it more understandable."*

In Settings, the **"Live Tiers"** box is a single wrapped line that's hard to parse — it mixes tier
names, model names, key names, arrows and punctuation with no structure. Make it a scannable display
of which model each tier resolves to and whether its provider key is present. See `live-tiers-ui.png`.

## Current state (researched — verify before editing)

- **Value:** `MainWindowViewModel.Keys.cs` exposes `[ObservableProperty] string? _litTiersSummary`,
  built by `RefreshLitTiers()` via `BackendConfigGenerator.Generate(...)`. The string format
  (`src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs`) is, e.g.:
  ```
  backend: config generated — balanced→deepseek-v4-pro, cheap→deepseek-v4-flash,
  claude→(absent), frontier→glm-5.2, vision→hf-qwen3-vl-235b; keys: DEEPSEEK_API_KEY, HF_TOKEN
  ```
- **Render:** `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` — a bordered box titled
  "Live Tiers" containing one `TextBlock Text="{Binding LitTiersSummary}" TextWrapping="Wrap"`. That's
  the whole UI.
- **Concepts:** the generator resolves each **tier** (`cheap`, `balanced`, `frontier`, `vision`,
  `claude`, `fallback`) to the best **model** whose **provider key** is present, with per-tier fallback
  chains. Each tier therefore has: a resolved model, a provider (HF / DeepSeek / Moonshot / Anthropic),
  and a present/absent key — exactly the structure the UI should show.

> **Freshness contract.** Confirm by searching for `LitTiersSummary`, `RefreshLitTiers`,
> `BackendConfigGenerator`, and `Text="Live Tiers"`; adapt if they've moved.

## Goal

The Live Tiers box shows one **row per tier** a user can scan at a glance: **tier → resolved model**,
the **provider**, and a clear **key-present / key-missing** indicator (e.g. a green dot vs. a muted
"key missing"). No information lost vs. today's string; just legible.

## Approach (Plan/Implement to refine)

- Surface **structured** per-tier data from the generator/VM instead of only the flat string — e.g.
  an `ObservableCollection<TierRowVm>` with `Tier`, `Model`, `Provider`, `KeyPresent`, and the
  (optional) fallback chain. `BackendConfigGenerator` already computes the tier→model `aliases` and
  `keysDetected`; expose those as data rather than concatenating them.
- Replace the single `TextBlock` with a compact `ItemsControl`/grid: each row = tier label, resolved
  model, provider, status dot. Keep it dense enough for the Settings panel; a per-row tooltip can show
  the full fallback chain.
- Keep the existing refresh trigger (`RefreshLitTiers` runs after key changes / config regen) so rows
  update when keys change.

## Tests

- Unit: `BackendConfigGenerator` (or the new VM projection) returns the expected structured rows for a
  given set of present keys (tier→model, provider, keyPresent), including a tier whose key is absent
  (e.g. `claude`).
- Headless UI (style of `ConfigInitEmptyStateUiTests`): the Live Tiers section renders one row per tier
  with the right model text and a visible key-status indicator.

## Out of scope

- Tier→model resolution / fallback-chain logic (see `rewrite-opaque-failure-on-model-auth-error`).
- A broader settings redesign.

## Screenshot

- `live-tiers-ui.png` — today's single-text-blob Live Tiers box.
