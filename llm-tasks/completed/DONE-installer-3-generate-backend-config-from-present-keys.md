# The model backend uses a static config, so a missing provider key fails mid-run instead of routing around it

`tools/backend/backend.sh` launches LiteLLM against a **static**
`tools/backend/litellm-config.yaml` (`CONFIG="${SCRIPT_DIR}/litellm-config.yaml"`).
The tier aliases point at fixed primaries — `frontier: kimi-k2` (needs
`MOONSHOT_API_KEY`), `balanced-kimi: deepseek-v4-pro` and
`cheap-kimi: deepseek-v4-flash` (need `DEEPSEEK_API_KEY`), `vision: hf-qwen3-vl-235b`
(needs `HF_TOKEN`) — with provider-diversified `fallbacks`.

But LiteLLM's `fallbacks` only fire on a **runtime** error. If `MOONSHOT_API_KEY`
is unset, a `frontier` request still hits `kimi-k2` first, eats an auth error,
burns a retry, and only then cascades. Once Hugging Face is the only *required*
key (see `installer-4-require-hugging-face-and-add-key-setup-panel.md`), the common case is
**partial** keys — and every tier whose primary you lack pays that auth-retry tax
on every call, with no guarantee the cascade lands on a provider you actually have.

## Goal

Generate the LiteLLM config from the provider keys **actually present**, so each
tier's alias points **directly** at the best model whose key exists — the dead
primary never enters the chain — and every tier terminates in the always-available
`fallback` tier (HF; see `installer-2-add-configurable-fallback-tier.md`). With HF required,
every chain is guaranteed non-empty. Adding a key auto-upgrades the affected tiers
on the next backend start.

## Approach (suggested)

- Put the resolution logic in a **pure, unit-tested** Core component, e.g.
  `BackendConfigGenerator` in `src/VisualRelay.Core/Execution/`. Input: the set of
  present provider keys (resolved via the `KeyEnvFile` helper from
  `installer-1-relocate-provider-keys-to-user-config.md` + process env). Output: the LiteLLM
  config as YAML text.
- **Keep `model_list` and `litellm_settings` verbatim** from the in-repo
  `litellm-config.yaml` template (the single source for provider routes,
  `extra_headers`, `drop_params`, `json_logs`). The proxy boots fine with models
  whose key is unset — they're simply never selected once no alias/fallback points
  at them. So the generator only **rewrites `router_settings.model_group_alias`
  and `router_settings.fallbacks`**. This keeps it small and low-risk.
- Encode, per tier, an ordered candidate list of `(model, requiredKey)`, derived
  from today's primary + fallbacks:

  | tier | ordered candidates (model → key) |
  |------|----------------------------------|
  | cheap | deepseek-v4-flash·DEEPSEEK → deepseek-v4-pro·DEEPSEEK → **fallback (HF)** |
  | balanced | deepseek-v4-pro·DEEPSEEK → kimi-k2·MOONSHOT → deepseek-v4-flash·DEEPSEEK → **fallback (HF)** |
  | frontier | kimi-k2·MOONSHOT → deepseek-v4-pro·DEEPSEEK → hf-qwen3-coder-next·HF → **fallback (HF)** |
  | vision | hf-qwen3-vl-235b·HF → hf-qwen3-vl-30b·HF → kimi-k2·MOONSHOT → **fallback (HF)** |
  | claude | claude-opus-1m·ANTHROPIC → claude-sonnet·ANTHROPIC |
  | fallback | hf-qwen3-coder-next·HF *(the floor; HF required ⇒ always present)* |

- For each tier: **drop** candidates whose key is absent; point the alias at the
  **first survivor**; set `fallbacks[tier]` to the remaining survivors with the HF
  floor **last**. Always keep the HF floor reachable. (`claude` has no HF floor —
  it is an opt-in premium tier, not part of the default pipeline; if `ANTHROPIC_API_KEY`
  is absent, omit the `claude` alias rather than forcing HF.)
- Wire into `backend.sh start`: before launching litellm, produce the generated
  config (e.g. `.relay-scratch/litellm-config.generated.yaml`) and pass `--config`
  that file. Invoke the generator via the published binary (brew) or a thin
  subcommand in a source checkout (e.g. extend `tools/VisualRelay.RunTask` or add a
  `visual-relay gen-backend-config`). **Fall back to the static config** if
  generation is unavailable, so a degraded environment still boots.
- Emit a one-line stderr/log summary of which tier resolved to which model and
  which keys were detected, so "why did `frontier` run on HF?" is answerable.

## Files

- New `BackendConfigGenerator` in `src/VisualRelay.Core/Execution/` (+ a thin
  invoker / `visual-relay` subcommand).
- `tools/backend/backend.sh` (generate-then-`--config`; static fallback).
- `tools/backend/litellm-config.yaml` (kept as the model-definition template).
- `README.md` (document the generated config + the resolution summary).

## Tests (write the failing tests first)

Drive `BackendConfigGenerator` with explicit key sets and assert the emitted
aliases/fallbacks (the matrix settled in the installer brainstorm):

- **HF only**: `cheap`/`balanced`/`frontier` aliases resolve to the HF floor
  (`fallback`); `vision` → `hf-qwen3-vl-235b`; **no** candidate requiring an absent
  key appears as a primary anywhere.
- **HF + DeepSeek**: `cheap` → flash, `balanced` → pro, `frontier` → pro (kimi
  dropped) then HF floor, `vision` → hf.
- **trio (HF + DeepSeek + Moonshot)**: `cheap` → flash, `balanced` → pro,
  `frontier` → kimi-k2, `vision` → hf; the HF floor terminates every chain.
- **HF + Anthropic**: `claude` tier lit; `cheap`/`balanced`/`frontier` → HF floor.
- **shape guard**: the generated YAML parses; every default tier has a non-empty
  alias + fallback chain whose terminal entry is the HF floor.

## Sequencing

Depends on `installer-2-add-configurable-fallback-tier.md` (uses the `fallback` tier) and
`installer-1-relocate-provider-keys-to-user-config.md` (reads the same resolved key set). The
key-setup panel reuses this resolution to show which tiers are lit — keep the
resolver callable from the app.

## Done when

- With only `HF_TOKEN` set, a clean `backend.sh start` brings up a proxy where
  **every default tier resolves and runs** (cheap/balanced/frontier on the HF
  floor, vision on HF) with **no auth-error retry burn**.
- Adding `DEEPSEEK_API_KEY` re-points cheap/balanced/frontier to DeepSeek on the
  next start; the dead-primary-first-call tax is gone (proven by the generated
  aliases, not runtime cascade).
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.

## Notes

This is the heart of the adaptive design. Do **not** lean on LiteLLM runtime
`fallbacks` to paper over missing keys — resolve at generation time. Keep model
definitions in the YAML template (single source for provider routes); the
generator only chooses aliases/fallbacks. Pricing/floor settled in the installer
brainstorm: HF floor = Qwen3-Coder-480B via Novita ($0.38 / $1.55 per 1M).
