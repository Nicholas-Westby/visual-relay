# Upgrade frontier tier from Kimi K2.7 Code to GLM 5.2 (Hugging Face / Z.AI)

## Background

**GLM 5.2** is the latest open-weight coding model from Zhipu AI. It is available
on Hugging Face Inference Providers via the **Z.AI** first-party provider
(`zai-org`) — the same HF_TOKEN-gated Inference Providers mechanism already used
by HF-backed models like `hf-qwen3-vl-30b` (which routes via a different provider).

This task switches the `frontier` tier alias from `kimi-k2` to a new `glm-5.2`
alias backed by Hugging Face (Z.AI `zai-org` provider). Kimi K2.7 Code remains in the
proxy as a **fallback** — it moves from primary to the first position in the
`frontier` fallback chain, so a Z.AI/HF outage or auth error cascades to
Kimi (different provider, different token) instead of dropping straight to
DeepSeek/HF-coder.

### Key facts

| Property | Value |
|---|---|
| **HF repo** | `zai-org/GLM-5.2` |
| **HF Inference Providers path** | `zai-org/GLM-5.2:zai-org` (provider-pinned to Z.AI) |
| **LiteLLM model string** | `huggingface/zai-org/GLM-5.2:zai-org` |
| **Provider key** | `HF_TOKEN` (already required — no new key needed) |
| **Context window** | 200K tokens (200 000) |
| **Input price** | $0.80 / 1M tokens |
| **Output price** | $2.40 / 1M tokens |
| **Cached input price** | $0.08 / 1M tokens |
| **Cache-write price** | $0.80 / 1M tokens (same as input — industry-standard fallback) |

The **current** frontier model is `moonshot/kimi-k2.7-code` (alias `kimi-k2`,
added 2026-06-12). The static `model_group_alias.frontier: kimi-k2` in
`litellm-config.yaml` and the first entry of `Chains["frontier"]` both point
at `kimi-k2`.

Sources:
- https://huggingface.co/zai-org/GLM-5.2
- https://huggingface.co/docs/inference-providers (Z.AI `zai-org` provider routing)
- https://huggingface.co/zai-org/GLM-5.2?inference_provider=zai-org (Z.AI provider pricing — confirm before pinning RelayPricing values)

## Current state (researched)

All references to the current frontier model live in the files below. Grep
targets: `kimi-k2`, `moonshot/kimi-k2.7-code`, `MOONSHOT_API_KEY`.

### 1. `tools/backend/litellm-config.yaml`

- **`model_list` — `kimi-k2` entry** (line ~20): the `model:` value is
  `moonshot/kimi-k2.7-code` with `api_key: os.environ/MOONSHOT_API_KEY`,
  `stream_timeout: 600`, `timeout: 480`, and `Connection: close`.
  **No change** to this entry — Kimi stays as a fallback target.

- **No `glm-5.2` model entry exists yet.** A new `model_list` entry must be
  added for the `glm-5.2` alias, routed via Hugging Face / Z.AI:
  ```yaml
  # --- GLM 5.2 (Zhipu AI) via Hugging Face / Z.AI -------------------
  # upstream model: GLM-5.2 (zai-org/GLM-5.2), served via HF Inference
  # Providers, provider-pinned to Z.AI (zai-org) via the ":zai-org"
  # suffix. One HF_TOKEN gates access.
  - model_name: glm-5.2
    litellm_params:
      model: huggingface/zai-org/GLM-5.2:zai-org
      api_key: os.environ/HF_TOKEN
      # Frontier tier: GLM 5.2 runs the Review stage, whose reasoning can
      # take up to ~410s before first token (observed p99 331s, max 412s on
      # the prior frontier model). A per-stream timeout above the global 60s
      # keeps the router from aborting a healthy slow Review; the frontier
      # 660s watchdog is the backstop. Matches the kimi-k2 frontier precedent.
      stream_timeout: 600
      # Non-streaming ceiling: 480s (8 min) > 412s observed worst-case healthy
      # Review; the wedge-breaker, not a latency budget. Same rationale as
      # kimi-k2.
      timeout: 480
      # Fresh TCP per request — see the kimi-k2 note on litellm's per-provider
      # httpx client cache + pooled-connection wedge. GLM 5.2 is the frontier
      # primary, so reliability here is critical.
      extra_headers:
        Connection: close
  ```

  **Where to place it**: in the `model_list` section, immediately before the
  existing `# --- Moonshot (Kimi) ---` comment block. This keeps the frontier
  primary at the top of the model list, consistent with its tier role.

- **`router_settings.model_group_alias`** (static template, line ~115):
  ```yaml
  frontier: kimi-k2
  ```
  Change to:
  ```yaml
  frontier: glm-5.2
  ```
  The static template is the fallback when `BackendConfigGenerator` is
  unavailable (no `dotnet`); the generated config is what actually runs.

- **`router_settings.fallbacks`** (static template, line ~124):
  ```yaml
  - frontier: [deepseek-v4-pro, hf-qwen3-coder-next, fallback]
  ```
  Change to (Kimi inserted as first fallback — different provider, different
  token):
  ```yaml
  - frontier: [kimi-k2, deepseek-v4-pro, hf-qwen3-coder-next, fallback]
  ```

### 2. `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs`

- **`Chains["frontier"]`** (line ~46):
  ```csharp
  ["frontier"] =
  [
      ("kimi-k2", "MOONSHOT_API_KEY"),
      ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
      ("hf-qwen3-coder-next", "HF_TOKEN"),
      ("fallback", "HF_TOKEN"),
  ],
  ```
  Change to (GLM 5.2 becomes primary, Kimi drops to first fallback):
  ```csharp
  ["frontier"] =
  [
      ("glm-5.2", "HF_TOKEN"),
      ("kimi-k2", "MOONSHOT_API_KEY"),
      ("deepseek-v4-pro", "DEEPSEEK_API_KEY"),
      ("hf-qwen3-coder-next", "HF_TOKEN"),
      ("fallback", "HF_TOKEN"),
  ],
  ```

  **Important**: GLM 5.2 requires `HF_TOKEN`, which is the same key as the
  `fallback` floor and the `hf-qwen3-coder-next` model. When only `HF_TOKEN` is
  present (no Moonshot, no DeepSeek), the generator resolves `frontier` →
  `glm-5.2` (HF key present) with fallbacks `[hf-qwen3-coder-next, fallback]`
  — both also HF. This is correct: GLM 5.2 is the better HF model, and the
  cross-provider Kimi/DeepSeek fallbacks engage when their keys are present.

  **Watch**: the `FallbackFloorModel` constant is `"hf-qwen3-coder-next"`. The
  generator has special logic: when the first surviving model for a non-fallback
  tier *is* `hf-qwen3-coder-next`, it points the alias at `"fallback"` instead.
  GLM 5.2 is a *different* model_name (`glm-5.2`), so this special-casing does
  **not** trigger — `frontier` resolves to `glm-5.2` directly. Verify this with
  the `HfOnly_DefaultTiersResolveToFallbackFloor` test (see Tests section below).

### 3. `src/VisualRelay.Core/Costs/RelayPricing.cs`

```csharp
["frontier"] = new(0.95, 4.0, 0.16, 0.95),
```
Change to GLM 5.2 pricing ($0.80 in / $2.40 out / $0.08 cached / $0.80
cache-write):
```csharp
["frontier"] = new(0.80, 2.40, 0.08, 0.80),
```

Also add a new pricing entry for the `glm-5.2` alias (matches `frontier` since
the alias resolves to the same upstream model):
```csharp
["glm-5.2"] = new(0.80, 2.40, 0.08, 0.80),
```

The existing `["kimi-k2"]` entry stays unchanged — Kimi is still a fallback
target and its pricing must remain accurate when it serves a request.

### 4. `src/VisualRelay.Core/Execution/SwivalProfileSession.cs` — `DefaultToml`

- **`[profiles.frontier]` block** (line ~135):
  ```toml
  [profiles.frontier]
  provider = "generic"
  base_url = "{ModelBackend.BaseUrl}"
  model = "frontier"
  max_context_tokens = 128000
  ```
  The `model = "frontier"` references the tier alias (not the upstream model
  id), so it stays unchanged. GLM 5.2 has a 200K context window vs the current
  128K. Update:
  ```toml
  max_context_tokens = 200000
  ```

- **`[profiles.kimi]` block** (line ~193): references `model = "kimi-k2"` and
  `max_context_tokens = 256000`. **No change** — Kimi stays as a fallback and
  its profile is still valid.

- **No `[profiles.glm]` profile exists.** Add one so the GLM 5.2 alias is
  directly addressable (matching the pattern of `[profiles.kimi]` for the
  kimi-k2 alias). Place it immediately before `[profiles.kimi]`:
  ```toml
  [profiles.glm]
  provider = "generic"
  base_url = "{ModelBackend.BaseUrl}"
  model = "glm-5.2"
  max_context_tokens = 200000
  ```

### 5. `.env.example`

The `MOONSHOT_API_KEY` comment (line 20) currently says:
```
# Moonshot — backs the `frontier` tier (kimi-k2 → Kimi K2.7 Code) and the kimi-k2 alias.
```
Update to reflect that Moonshot now backs a **fallback** for frontier, not the
primary:
```
# Moonshot — backs the kimi-k2 alias (frontier fallback) and the kimi profile.
```

The `HF_TOKEN` comment (line 24) currently says:
```
# Hugging Face Inference Providers — one token, many upstreams. Backs the
# `vision` tier and the hf-qwen3-coder-next alias.
```
Update to note it now backs the frontier primary:
```
# Hugging Face Inference Providers — one token, many upstreams. Backs the
# `frontier` tier (GLM 5.2 via Z.AI), the `vision` tier, and the
# hf-qwen3-coder-next alias.
```

### 6. `README.md`

Line 90 lists the model aliases:
```
(`cheap`, `balanced`, `frontier`, `vision`, `claude`, `claude-opus-1m`, `claude-sonnet`, `gpt-5`, `hf-qwen3-coder-next`, `kimi-k2`, `fallback`)
```
Add `glm-5.2` to the list:
```
(`cheap`, `balanced`, `frontier`, `vision`, `claude`, `claude-opus-1m`, `claude-sonnet`, `gpt-5`, `hf-qwen3-coder-next`, `kimi-k2`, `glm-5.2`, `fallback`)
```

Line 110 example summary:
```
(e.g. `backend: config generated — cheap→deepseek-v4-flash, balanced→deepseek-v4-pro, frontier→kimi-k2, …; keys: HF_TOKEN, DEEPSEEK_API_KEY, MOONSHOT_API_KEY`)
```
Update to show frontier→glm-5.2:
```
(e.g. `backend: config generated — cheap→deepseek-v4-flash, balanced→deepseek-v4-pro, frontier→glm-5.2, …; keys: HF_TOKEN, DEEPSEEK_API_KEY, MOONSHOT_API_KEY`)
```

### 7. Tests

#### `tests/VisualRelay.Tests/BackendConfigGeneratorTests.cs`

- **`HfOnly_DefaultTiersResolveToFallbackFloor`**: with only `HF_TOKEN`, frontier
  currently resolves to `fallback`. After this change, frontier resolves to
  `glm-5.2` (GLM's required key is `HF_TOKEN`, which is present). Update:
  ```csharp
  // Before:
  Assert.Equal("fallback", aliases["frontier"]);
  Assert.DoesNotContain("kimi-k2", aliases.Values);
  // After:
  Assert.Equal("glm-5.2", aliases["frontier"]);
  Assert.DoesNotContain("kimi-k2", aliases.Values);
  ```

- **`HfPlusDeepSeek_CheapFlash_BalancedPro_FrontierPro`**: with `HF_TOKEN` +
  `DEEPSEEK_API_KEY`, frontier currently resolves to `deepseek-v4-pro` (Kimi key
  absent). After this change, frontier resolves to `glm-5.2` (HF key present,
  GLM is first in chain). Update:
  ```csharp
  // Before:
  Assert.Equal("deepseek-v4-pro", aliases["frontier"]);
  Assert.DoesNotContain("kimi-k2", aliases.Values);
  // After:
  Assert.Equal("glm-5.2", aliases["frontier"]);
  Assert.DoesNotContain("kimi-k2", aliases.Values);
  ```

- **`Trio_FrontierKimi_ChainTerminatesInFallback`**: with `HF_TOKEN` +
  `DEEPSEEK_API_KEY` + `MOONSHOT_API_KEY`, frontier currently resolves to
  `kimi-k2`. After this change, resolves to `glm-5.2`, with Kimi as first
  fallback. Update:
  ```csharp
  // Before:
  Assert.Equal("kimi-k2", aliases["frontier"]);
  // ...
  var chain = fallbacks["frontier"];
  Assert.Contains("deepseek-v4-pro", chain);
  Assert.Contains("hf-qwen3-coder-next", chain);
  Assert.Equal("fallback", chain[^1]);
  // After:
  Assert.Equal("glm-5.2", aliases["frontier"]);
  // ...
  var chain = fallbacks["frontier"];
  Assert.Contains("kimi-k2", chain);
  Assert.Contains("deepseek-v4-pro", chain);
  Assert.Contains("hf-qwen3-coder-next", chain);
  Assert.Equal("fallback", chain[^1]);
  ```

- **`ShapeGuard_ParsesAndEveryTierHasNonEmptyChainEndingInFallback`**: asserts
  `Assert.Contains("kimi-k2", yaml, …)` — this still passes (kimi-k2 remains in
  model_list). Optionally also assert `Assert.Contains("glm-5.2", yaml, …)`. No
  **required** change, but adding the glm-5.2 assertion strengthens the guard.

- **`HfPlusAnthropic_ClaudeLit_OtherTiersFallback`**: with `HF_TOKEN` +
  `ANTHROPIC_API_KEY`, frontier currently resolves to `fallback`. After this
  change, resolves to `glm-5.2` (HF key present). Update:
  ```csharp
  // Before:
  Assert.Equal("fallback", aliases["frontier"]);
  // After:
  Assert.Equal("glm-5.2", aliases["frontier"]);
  ```

- **`EmptyKeySet_DoesNotCrash_AndEveryTierHasAlias`**: with no keys, frontier
  resolves to `fallback` (degenerate case). **No change** — GLM's key is absent
  so the generator falls through to the `fallback` tier alias. Verify this
  still passes.

#### `tests/VisualRelay.Tests/BackendConfigGeneratorTests.AliasConsistency.cs`

- **`TierAliasNames_AreConsistentAcrossBackendConfigPricingAndSwivalProfile`**:
  this test cross-checks that tier alias names in `BackendConfigGenerator.Chains`
  exist in `RelayPricing.Default` and in `SwivalProfileSession.DefaultToml`.
  After adding `glm-5.2` to `Chains["frontier"]` candidates, the test does
  **not** automatically check candidate model names — it checks `Chains.Keys`
  (tier names like `frontier`, `cheap`), not candidate model names. **No
  required change**, but if the test is extended to check candidate models,
  ensure `glm-5.2` appears in all three sources (it will, after the pricing and
  swival profile additions).

#### `tests/VisualRelay.Tests/BackendConfigGeneratorTests.KimiK2_7Upstream.cs`

- **`KimiK2_UpstreamModel_IsKimiK2_7Code`**: asserts the kimi-k2 alias points
  at `moonshot/kimi-k2.7-code`. **No change** — the kimi-k2 entry is unmodified.

- **`KimiK2_GeneratedConfig_ContainsKimiK2_7Code`**: asserts the generated YAML
  contains `moonshot/kimi-k2.7-code`. **No change** — model_list is preserved
  verbatim.

- **`KimiK2_Template_DoesNotContainK2_6`**: **No change**.

#### `tests/VisualRelay.Tests/BackendConfigGeneratorTests.PerModelTimeout.cs`

- **`PerModelTimeout_AllNineModelsHaveExplicitCeiling`**: the `allModels` array
  lists nine models. After adding `glm-5.2`, there are **ten** models. Update:
  - Rename test to `PerModelTimeout_AllTenModelsHaveExplicitCeiling` (or keep
    the name and update the array — renaming is preferred for accuracy).
  - Add `"glm-5.2"` to the `allModels` array.

- **`PerModelTimeout_FrontierKimiK2Has480s`**: asserts `kimi-k2` has 480s.
  **No change** to kimi-k2. Add a parallel test for the new frontier primary:
  ```csharp
  [Fact]
  public void PerModelTimeout_FrontierGlm52Has480s()
  {
      var yaml = File.ReadAllText(TemplatePath);
      var timeouts = ParseModelTimeouts(yaml);

      Assert.True(timeouts.TryGetValue("glm-5.2", out var t),
          "glm-5.2 must have a per-model timeout");
      // 480s (8 min) — same frontier ceiling as kimi-k2; > 412s observed
      // worst-case healthy Review, far below the 40-min stage cap.
      Assert.Equal(480, t);
  }
  ```

- **`PerModelTimeout_GeneratedYamlPreservesAllCeilings`**: asserts all nine
  models' timeouts survive into the generated YAML. Add `glm-5.2`:
  ```csharp
  Assert.Equal(480, timeouts["glm-5.2"]);
  ```

#### `tests/VisualRelay.Tests/SwivalProfileSessionPinningTests.KimiMaxContext.cs`

- **`DefaultToml_KimiProfile_MaxContextTokensIs256000`**: **No change** — the
  kimi profile is unmodified. Add a parallel test for the new GLM profile:
  ```csharp
  [Fact]
  public void DefaultToml_GlmProfile_MaxContextTokensIs200000()
  {
      var toml = SwivalProfileSession.DefaultToml;
      var maxTokens = ParseSwivalProfileMaxContextTokens(toml);

      Assert.True(maxTokens.TryGetValue("glm", out var glmMax),
          "DefaultToml must contain a [profiles.glm] block with max_context_tokens");
      Assert.Equal(200000, glmMax);
  }
  ```

  Also add a test for the frontier profile's updated context window:
  ```csharp
  [Fact]
  public void DefaultToml_FrontierProfile_MaxContextTokensIs200000()
  {
      var toml = SwivalProfileSession.DefaultToml;
      var maxTokens = ParseSwivalProfileMaxContextTokens(toml);

      Assert.True(maxTokens.TryGetValue("frontier", out var frontierMax),
          "DefaultToml must contain a [profiles.frontier] block with max_context_tokens");
      Assert.Equal(200000, frontierMax);
  }
  ```

## What to build

1. **`tools/backend/litellm-config.yaml`** — add a new `glm-5.2` model entry in
   `model_list` (before the Moonshot block) with
   `model: huggingface/zai-org/GLM-5.2:zai-org`, `api_key: os.environ/HF_TOKEN`,
   `stream_timeout: 600`, `timeout: 480`, `Connection: close`. Change the static
   `model_group_alias.frontier` from `kimi-k2` to `glm-5.2`. Change the static
   `fallbacks` frontier chain from `[deepseek-v4-pro, hf-qwen3-coder-next, fallback]`
   to `[kimi-k2, deepseek-v4-pro, hf-qwen3-coder-next, fallback]`.

2. **`src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs`** — in
   `Chains["frontier"]`, prepend `("glm-5.2", "HF_TOKEN")` as the first candidate.
   Kimi moves to second position.

3. **`src/VisualRelay.Core/Costs/RelayPricing.cs`** — update `["frontier"]`
   pricing to `(0.80, 2.40, 0.08, 0.80)`. Add `["glm-5.2"]` entry with the same
   values. Leave `["kimi-k2"]` unchanged.

4. **`src/VisualRelay.Core/Execution/SwivalProfileSession.cs`** — in
   `DefaultToml`, update `[profiles.frontier]` `max_context_tokens` from
   `128000` to `200000`. Add a new `[profiles.glm]` block with
   `model = "glm-5.2"` and `max_context_tokens = 200000`.

5. **`.env.example`** — update the `MOONSHOT_API_KEY` comment to say it backs
   the kimi-k2 alias as a frontier fallback. Update the `HF_TOKEN` comment to
   note it now backs the frontier tier (GLM 5.2 via Z.AI).

6. **`README.md`** — add `glm-5.2` to the alias list on line 90. Update the
   example summary on line 110 from `frontier→kimi-k2` to `frontier→glm-5.2`.

7. **Tests** — update existing tests per the detailed list above:
   - `BackendConfigGeneratorTests.cs`: update frontier resolution assertions in
     `HfOnly_…`, `HfPlusDeepSeek_…`, `Trio_…`, and `HfPlusAnthropic_…`.
   - `BackendConfigGeneratorTests.PerModelTimeout.cs`: add `glm-5.2` to
     `allModels`, rename `AllNineModels` → `AllTenModels`, add
     `PerModelTimeout_FrontierGlm52Has480s`, add `glm-5.2` to the generated-YAML
     preservation test.
   - `SwivalProfileSessionPinningTests.KimiMaxContext.cs`: add tests for the
     GLM profile and updated frontier profile context windows.

## Done when

- [ ] `tools/backend/litellm-config.yaml`: a `glm-5.2` model entry exists with
  `model: huggingface/zai-org/GLM-5.2:zai-org` and `api_key: os.environ/HF_TOKEN`.
- [ ] `tools/backend/litellm-config.yaml`: static `model_group_alias.frontier`
  is `glm-5.2`.
- [ ] `tools/backend/litellm-config.yaml`: static `fallbacks` frontier chain is
  `[kimi-k2, deepseek-v4-pro, hf-qwen3-coder-next, fallback]`.
- [ ] `BackendConfigGenerator.Chains["frontier"]` has `("glm-5.2", "HF_TOKEN")`
  as the first candidate.
- [ ] `RelayPricing.Default["frontier"]` is `(0.80, 2.40, 0.08, 0.80)`.
- [ ] `RelayPricing.Default` contains a `["glm-5.2"]` entry.
- [ ] `SwivalProfileSession.DefaultToml` `[profiles.frontier]` has
  `max_context_tokens = 200000`.
- [ ] `SwivalProfileSession.DefaultToml` contains a `[profiles.glm]` block with
  `model = "glm-5.2"` and `max_context_tokens = 200000`.
- [ ] `.env.example` comments updated for `MOONSHOT_API_KEY` and `HF_TOKEN`.
- [ ] `README.md` alias list and example summary updated.
- [ ] `./visual-relay check` (or `dotnet test`) is green — all existing and new
  tests pass.
- [ ] No stale `frontier: kimi-k2` primary reference remains in the static
  template or `Chains` (verify with `grep -n "frontier: kimi-k2" tools/backend/litellm-config.yaml`
  — should only appear in fallbacks, not in `model_group_alias`).

Conventional Commit subject: `feat(backend): upgrade frontier tier to GLM 5.2 via Hugging Face Z.AI`
