# Make the Cost Per LLM Model panel show concrete models, correct fallbacks, and honest local times

## Problem

The Settings → "Cost Per LLM Model" panel (`src/VisualRelay.App/Views/Controls/CostPerModel.axaml`, populated by `PopulateModelCostRows` in `src/VisualRelay.App/ViewModels/MainWindowViewModel.CostPerModel.cs`) has a cluster of defects, all visible in the running app:

1. **Tier aliases are rendered as if they were models.** The panel shows cards titled "cheap", "balanced", "frontier", and "vision" alongside real model names like "glm-5.2". "frontier" and "glm-5.2" appear as two separate cards with byte-identical rates because the pricing table duplicates the GLM 5.2 numbers under both keys.
2. **The pricing data model itself is the root cause and violates the panel's DRY goal.** `RelayPricing.Default` (`src/VisualRelay.Core/Costs/RelayPricing.cs`) mixes tier aliases and concrete model names as keys:

   ```csharp
   ["cheap"] = new(0.14, 0.28, 0.0028, 0.14) { Windows = DeepseekPeakWindows },
   ...
   ["frontier"] = new(1.40, 4.40, 0.26),
   // GLM 5.2 via HF (zai-org), 2026-07-07; same as frontier
   ["glm-5.2"] = new(1.40, 4.40, 0.26),
   ```

   The tier entries are hand-copied snapshots of whatever concrete model the tier resolved to on the day they were written. Tier→model resolution is actually dynamic: `BackendConfigGenerator` (`src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs`, `Chains` + `ResolveTiers`) re-targets tiers based on which provider keys are present and on user overrides persisted from the Live Tiers UI (`TierModelOverrides`). If the user re-points `cheap` at `kimi-k2` in Live Tiers, the `"cheap"` pricing entry silently keeps DeepSeek rates — both the panel and the cost estimator then display/charge the wrong numbers with no error. The intent was "what is displayed is what is configured in the code"; the current shape is a second, drift-prone copy of the configuration.
3. **Peak-window times are shown in 24-hour format with a confusing timezone label.** The window header renders as `18:00 – 21:00 ( Asia/Shanghai → PST8PDT ) — 2× multiplier`. The times shown are already converted to local time, but the `source → dest` arrow makes them read as Shanghai times; `PST8PDT` is a raw POSIX zone identifier leaked from `TimeZoneInfo.Local.Id`; and the times use `ToString("HH:mm")` (24-hour) instead of AM/PM. The culprit is `ConvertWindowTimeToLocal` returning `TimeOnly.FromDateTime(localDateTime).ToString("HH:mm")` and the row exposing `DisplayTimezoneLabel = TimeZoneInfo.Local.Id`.
4. **No indication of which models are currently active.** The panel gives no hint which cards correspond to the models the tiers currently resolve to (the same information the Live Tiers section already computes via `BackendConfigGenerator.GetTierRows`).
5. **"same as input" is applied inconsistently.** `FormatCacheWriteDisplay` prints "same as input" only when `CacheWrite` is `null` (glm-5.2), but prints a bare `$0.14` for cheap/deepseek-v4-flash where `CacheWrite` is explicitly equal to `Input` — same economic fact, two different renderings, and the numeric one gives no hint.
6. **The vision card renders "Cached input: $ per 1M tokens".** `CachedInputRate` is `double?`; when `null` the XAML `StringFormat='${0} per 1M tokens'` binding produces an empty amount. The correct semantics (per `RelayCostEstimator`) is that a `null` `CachedInput` bills at the Input rate.
7. **Peak cached input disagrees with the estimator.** `PeakCachedInputRate = (pricing.CachedInput ?? 0) * window.Multiplier` — the estimator uses `pricing.CachedInput ?? pricing.Input`. A windowed model with `null` `CachedInput` would display `$0` while being billed at the input rate.
8. **Rate formatting is culture-sensitive and inconsistent.** All rates go through XAML `StringFormat='${0} per 1M tokens'` or `$"${rate.Value} per 1M tokens"` — raw `double.ToString()` in the current culture. Under a comma-decimal locale this renders `$0,0028`; and values render with whatever precision the double happens to have (`$1.4` vs `$0.003625`).
9. **The null-fallback semantics are duplicated instead of shared.** `RelayCostEstimator.EstimateReport` has its own `var cachedRate = pricing.CachedInput ?? pricing.Input; var cacheWriteRate = pricing.CacheWrite ?? pricing.Input;` while the view model re-implements (and partially gets wrong, see 7) the same rules.
10. **An unreachable pricing entry.** `["claude-haiku"]` exists in `RelayPricing.Default` but appears nowhere in `tools/backend/litellm-config.yaml`'s `model_list`, in `BackendConfigGenerator.Chains`/`SelectableModelsByTier`, or in `SwivalProfileSession.DefaultToml` — no report can ever carry that model name.

Ground truth about what needs pricing: stage reports record the *requested* model name, which in practice is a tier alias — a sweep of this repo's `.relay/*/stage*.json` reports shows exactly four distinct values: `balanced`, `cheap`, `frontier`, `vision`. So the estimator must keep pricing tier-alias names; it just must do so by *resolving* them, not by duplicated table rows.

## Fix

One direction, three layers. Pricing becomes concrete-models-only; tier aliases resolve through the configuration that already owns tier→model mapping; the panel renders concrete models with tier badges and locale-proof strings.

### 1. Core: concrete-model pricing + shared effective rates

In `src/VisualRelay.Core/Costs/RelayPricing.cs`:

- Re-key `RelayPricing.Default` to concrete `model_list` names only. Replace the four tier-alias entries with their concrete equivalents, keeping the exact same rates and comments:
  - `"cheap"` → `"deepseek-v4-flash"` (0.14, 0.28, 0.0028, 0.14, `DeepseekPeakWindows`)
  - `"balanced"` → `"deepseek-v4-pro"` (0.435, 0.87, 0.003625, 0.435, `DeepseekPeakWindows`)
  - `"frontier"` → delete (duplicate of `"glm-5.2"`, which stays)
  - `"vision"` → `"hf-qwen3-vl-235b"` (0.20, 0.88)
  - Delete `"claude-haiku"` (unreachable, see Problem 10). Keep every other entry (`glm-5.2`, `claude-opus-1m`, `claude-sonnet`, `gpt-5`, `hf-qwen3-coder-next`, `kimi-k2`) unchanged.
- Add effective-rate members to `ModelPricing` so the null-fallback rule lives in exactly one place:

  ```csharp
  public double EffectiveCachedInput => CachedInput ?? Input;
  public double EffectiveCacheWrite => CacheWrite ?? Input;
  ```

In `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs` (main file, next to `Chains`), add the default tier→concrete-model map — the head of each chain, which is by construction today's auto-resolution:

```csharp
/// <summary>Default tier-alias → concrete model resolution (head of each
/// chain; the "fallback" pseudo-model maps to the HF floor). Used to price
/// reports whose recorded model is a tier alias.</summary>
public static IReadOnlyDictionary<string, string> DefaultTierResolution { get; } =
    Chains.ToDictionary(
        kv => kv.Key,
        kv => kv.Value[0].Model == FallbackTier ? FallbackFloorModel : kv.Value[0].Model,
        StringComparer.Ordinal);
```

In `src/VisualRelay.Core/Costs/RelayCostEstimator.cs`, `EstimateReport`:

- Replace the single dictionary lookup with concrete-first, then tier-alias resolution:

  ```csharp
  if (!RelayPricing.Default.TryGetValue(model, out var pricing) &&
      !(BackendConfigGenerator.DefaultTierResolution.TryGetValue(model, out var concrete) &&
        RelayPricing.Default.TryGetValue(concrete, out pricing)))
  {
      return new RelayCostEstimate(model, 0, false, ...); // unchanged unpriced path
  }
  ```

  Keep `RelayCostEstimate.Model` as the originally requested name. Document in the method's XML doc that tier aliases are priced at the tier's *default* resolution — per-run overrides are not recorded in reports, so a user-overridden tier is an accepted approximation (still strictly better than the previous hand-copied snapshot, which had the same staleness plus drift).
- Replace `pricing.CachedInput ?? pricing.Input` and `pricing.CacheWrite ?? pricing.Input` with `pricing.EffectiveCachedInput` / `pricing.EffectiveCacheWrite`.

All existing estimator cost assertions must keep passing with identical dollar values: `cheap`/`balanced`/`vision` now resolve to entries with the same rates they had before.

### 2. View model: badges, effective rates, preformatted strings

In `MainWindowViewModel.CostPerModel.cs`:

- Change the signature to `public void PopulateModelCostRows(IReadOnlyDictionary<string, string>? tierAssignments = null)`. When `null`, use `BackendConfigGenerator.DefaultTierResolution`. The card list is the union of `RelayPricing.Default.Keys` and the assignment values, so an override pointing a tier at a model with no pricing entry still yields a card (marked unpriced) instead of silently vanishing.
- Per card compute `TierBadges` — the tiers (assignment keys) whose assigned model is this card's model, ordered by a fixed tier order:

  ```csharp
  private static readonly string[] TierOrder =
      ["cheap", "balanced", "frontier", "vision", "claude", "fallback"];
  ```

  Sort cards: models with badges first (by their first badge's `TierOrder` index), then the unassigned rest by ordinal model name. `IsActive` = has at least one badge. `IsPriced` = has a `RelayPricing.Default` entry; unpriced cards carry badges and the name but no rate rows.
- Move ALL rate rendering into the view model as preformatted, invariant-culture strings; the XAML must bind only ready-made strings (this fixes Problems 6 and 8 in one stroke). Helpers:

  ```csharp
  private static string FormatRate(double rate) =>
      "$" + rate.ToString("0.######", CultureInfo.InvariantCulture) + " per 1M tokens";

  // Cached-input and cache-write rows: always numeric; annotate when the
  // effective rate equals the input rate (covers BOTH the null fallback and
  // an explicit value equal to input — one consistent rendering).
  private static string FormatRateRelativeToInput(double effective, double input) =>
      effective == input ? FormatRate(effective) + " (same as input)" : FormatRate(effective);
  ```

  Base rows: `InputDisplay = FormatRate(pricing.Input)`, `OutputDisplay = FormatRate(pricing.Output)`, `CachedInputDisplay = FormatRateRelativeToInput(pricing.EffectiveCachedInput, pricing.Input)`, `CacheWriteDisplay = FormatRateRelativeToInput(pricing.EffectiveCacheWrite, pricing.Input)`. Delete `FormatCacheWriteDisplay` and the raw `double`/`double?` rate properties from `ModelCostRow` (`InputRate`, `OutputRate`, `CachedInputRate`, `CacheWriteRate`) — expose only the display strings plus `ModelKey`, `TierBadges`, `IsActive`, `IsPriced`, `HasWindows`, `Windows`. Drop `DisplayName` (it was always identical to `ModelKey`).
- Peak rows use the same helpers on effective rates: `PeakInputDisplay = FormatRate(pricing.Input * m)`, `PeakOutputDisplay = FormatRate(pricing.Output * m)`, `PeakCachedInputDisplay = FormatRateRelativeToInput(pricing.EffectiveCachedInput * m, pricing.Input * m)`, `PeakCacheWriteDisplay = FormatRateRelativeToInput(pricing.EffectiveCacheWrite * m, pricing.Input * m)`. This eliminates the `?? 0` bug (Problem 7).
- Window header: replace the eleven-TextBlock strip and the `SourceTimezoneLabel`/`DisplayTimezoneLabel`/`StartTimeDisplay`/`EndTimeDisplay`/`Multiplier` properties with two strings built in the view model:
  - `Headline` — local times, 12-hour, explicitly "your time": `6:00 PM – 9:00 PM your time — 2× peak pricing`. Times come from the existing conversion (`ConvertWindowTimeToLocal`, unchanged math) but formatted with `ToString("h:mm tt", CultureInfo.InvariantCulture)`. Multiplier formatted `window.Multiplier.ToString("0.#", CultureInfo.InvariantCulture) + "× peak pricing"`.
  - `SourceNote` — the provider's own schedule for transparency: `(9:00 AM – 12:00 PM in Asia/Shanghai)`, same `h:mm tt` format, using the raw `RateWindow` times and `TimeZoneId`.
  - If `TimeZoneInfo.FindSystemTimeZoneById` throws (existing catch path), the `Headline` uses the source times with `in Asia/Shanghai` in place of `your time`, and `SourceNote` is empty. Never display `TimeZoneInfo.Local.Id` or any platform timezone identifier — that is where `PST8PDT` came from.
- Wire live refresh: at the end of `RefreshLitTiersAsync` in `MainWindowViewModel.Keys.cs` (after the `LitTierRows` rebuild loop and `_suppressLitTierPersist = false;`), rebuild the cost cards from the just-computed resolution so the panel always mirrors Live Tiers, including user overrides:

  ```csharp
  var assignments = LitTierRows
      .Where(r => !string.IsNullOrWhiteSpace(r.SelectedModel) && r.SelectedModel != "(key missing)")
      .ToDictionary(
          r => r.Tier,
          r => r.SelectedModel == "fallback"
              ? BackendConfigGenerator.DefaultTierResolution["fallback"]
              : r.SelectedModel,
          StringComparer.Ordinal);
  PopulateModelCostRows(assignments);
  ```

  Keep the existing parameterless-style call in `LoadInitialAsync` (`MainWindowViewModel.cs`) as `PopulateModelCostRows()` so the panel is populated with the default resolution before keys load.

### 3. XAML: badges, active styling, unpriced state

In `CostPerModel.axaml`:

- Card header becomes a horizontal row: the model name TextBlock (bind `ModelKey`) followed by one small badge chip per `TierBadges` entry (ItemsControl with horizontal StackPanel; chip = `Border` `CornerRadius="8"` `Padding="6,1"` `Background="#2E4B6E"` containing the tier name at `FontSize="10"` `Foreground="#CFE0F5"`).
- Active cards keep the current border; inactive cards (`IsActive` false) get the muted header `Foreground="#9AA3B1"` instead of `#F2F5FA` so configured models stand out. Do this with two TextBlocks toggled by `IsVisible` bindings on `IsActive` (Avalonia DataTemplates here have no style triggers; keep it dumb).
- The four rate `Grid` rows bind the new display strings directly (`InputDisplay`, `OutputDisplay`, `CachedInputDisplay`, `CacheWriteDisplay`) with **no `StringFormat`** anywhere in this control. Wrap the rate rows + windows section in a container with `IsVisible="{Binding IsPriced}"`, and add a sibling TextBlock `pricing not configured for this model` (muted `#7F8794`, `FontSize="12"`) with `IsVisible="{Binding !IsPriced}"`.
- The window header strip becomes two TextBlocks: `Headline` (existing amber `#E0A458`) and `SourceNote` (`FontSize="10"` `Foreground="#7F8794"`, `IsVisible` bound to `SourceNote` non-empty via `StringConverters.IsNotNullOrEmpty`). Peak rows bind the four new peak display strings.

### Consistency test updates (required, do not delete)

- `tests/VisualRelay.Tests/BackendConfigGeneratorAliasConsistencyTests.cs` currently asserts tier names are `RelayPricing.Default` keys. Rewrite that invariant to the new shape: every tier alias in `BackendConfigGenerator.Chains.Keys` must have a `DefaultTierResolution` entry, and every `DefaultTierResolution` value must have a `RelayPricing.Default` entry (keep the swival-toml tier-name assertions as they are). Also assert the inverse hygiene rule: `RelayPricing.Default.Keys` contains none of `Chains.Keys` (no tier alias may reappear as a pricing key).
- `RelayPricingRateTests`, `RelayPricingScheduleTests`, `RelayPricingScheduleEdgeTests`, `RelayCostEstimatorTests`: update any lookups that index `RelayPricing.Default` by tier alias to the concrete key; report-JSON fixtures that set `"model": "cheap"` etc. must stay as tier aliases and keep asserting the same dollar values (they prove alias resolution works).

## Rejected approaches — do not do these

- Do NOT keep tier-alias keys in `RelayPricing.Default` (or add a second tier-keyed table). The whole point is a single pricing row per concrete model.
- Do NOT invent rates for models that have none (e.g. `hf-qwen3-vl-30b`). Unpriced cards say "pricing not configured for this model"; adding verified rates is out of scope.
- Do NOT parse the generated LiteLLM YAML at runtime to discover tier assignments — `BackendConfigGenerator` is the source that *generates* that YAML; use `DefaultTierResolution`/`GetTierRows`.
- Do NOT display `TimeZoneInfo.Local.Id`, `TimeZoneInfo.Local.DisplayName`, or attempt timezone-abbreviation lookup (PDT/PST); cross-platform abbreviation data is unreliable. The phrase is the literal "your time".
- Do NOT use XAML `StringFormat` for any currency or time value in this control; all such strings are built in the view model with `CultureInfo.InvariantCulture`.
- Do NOT change `tools/backend/litellm-config.yaml`, `BackendConfigGenerator.Chains`, `SelectableModelsByTier`, or `SwivalProfileSession.DefaultToml`.
- Do NOT make `PopulateModelCostRows` async or have it load config itself — it stays a pure projection; `RefreshLitTiersAsync` already owns the async config/keys work.

## Tests

Rewrite `tests/VisualRelay.Tests/CostPerModelTests.cs` around the new shape (the old raw-rate assertions no longer compile). Cover at minimum, via `new MainWindowViewModel()` + `PopulateModelCostRows(...)`:

- No card's `ModelKey` is a tier alias (`cheap`/`balanced`/`frontier`/`vision`/`claude`/`fallback`), and there are no duplicate `ModelKey` values.
- Default resolution badges: `deepseek-v4-flash` carries the `cheap` badge, `deepseek-v4-pro` carries `balanced`, `glm-5.2` carries `frontier`; `kimi-k2` and `gpt-5` have no badges and `IsActive == false`.
- Ordering: every badged card precedes every unbadged card, and the first card is the `cheap` tier's model.
- Explicit assignments win: `PopulateModelCostRows(new Dictionary<string,string> { ["cheap"] = "kimi-k2" })` puts the `cheap` badge on `kimi-k2` and leaves `deepseek-v4-flash` unbadged.
- Unpriced assignment: `["vision"] = "hf-qwen3-vl-30b"` yields a card with that key, `IsPriced == false`, and a `vision` badge.
- `CachedInputDisplay` for `hf-qwen3-coder-next` (null `CachedInput`) is `"$0.3 per 1M tokens (same as input)"` — numeric, never a blank amount.
- `CacheWriteDisplay` for `deepseek-v4-flash` (explicit 0.14 == input) ends with `"(same as input)"`; for `claude-opus-1m` it is exactly `"$6.25 per 1M tokens"` with no annotation.
- Peak rows on `deepseek-v4-flash`: `PeakCachedInputDisplay` starts with `"$0.0056"` (effective × 2, not `?? 0`), `PeakCacheWriteDisplay` carries the `"(same as input)"` annotation.
- Culture safety: set `CultureInfo.CurrentCulture` to `de-DE` for the populate call (restore in `finally`) and assert a fractional rate string still contains `"."` and no `","`.
- Window `Headline` matches `^\d{1,2}:\d{2} [AP]M – \d{1,2}:\d{2} [AP]M your time — 2× peak pricing$` and `SourceNote` equals `"(9:00 AM – 12:00 PM in Asia/Shanghai)"` for the first DeepSeek window (source times are fixed; local times vary by machine, hence the pattern).
- Idempotency: calling `PopulateModelCostRows()` twice yields the same card count.

Add to `tests/VisualRelay.Tests/RelayCostEstimatorTests.cs` (keep existing facts):

- A report with `"model": "cheap"` and one with `"model": "deepseek-v4-flash"`, identical token stats, produce identical `CostUsd` and both `Priced == true`.
- A report with `"model": "frontier"` prices at glm-5.2 rates.
- `"model": "nonexistent-model-xyz"` remains `Priced == false` (existing fact keeps passing).

Effective-rate unit facts (in `RelayPricingRateTests` or alongside): `EffectiveCachedInput`/`EffectiveCacheWrite` return the explicit value when set and `Input` when null.

## Constraints

- `dotnet build VisualRelay.slnx` must succeed; the full test suite must pass.
- No new NuGet dependencies; no changes outside the files named above plus the listed tests.
- Keep the Settings expander header text "Cost Per LLM Model" (`SettingsPanel.axaml`) unchanged.
- If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove coverage to satisfy it.
