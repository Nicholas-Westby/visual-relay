# Add a configurable `fallback` model tier (the always-available Hugging Face floor)

Visual Relay's pipeline targets named tiers — `cheap`, `balanced`, `frontier`,
`vision` — defined in three places that must stay in sync:

- the LiteLLM aliases in `tools/backend/litellm-config.yaml`
  (`router_settings.model_group_alias`: `frontier: kimi-k2`,
  `balanced-kimi: deepseek-v4-pro`, `cheap-kimi: deepseek-v4-flash`,
  `vision: hf-qwen3-vl-235b`, `claude: claude-opus-1m`),
- the Swival profiles in `src/VisualRelay.Core/Execution/SwivalProfileSession.cs`
  (`DefaultToml`, lines 44-105; each `model = "<alias>"`), and
- the default tier map in `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`
  (`Defaults`, lines 14-20) backed by `RelayConfig.TierProfiles`.

There is no tier guaranteed to work regardless of which paid provider keys exist.
The adaptive-routing task needs one: a single, configurable **floor** tier that
always resolves to a Hugging Face model (HF being the one required key — see
`installer-4-require-hugging-face-and-add-key-setup-panel.md`), so every other tier can fall
through to it.

## Goal

Introduce a first-class `fallback` tier, configurable exactly like the others.
Its default model is the HF Novita coder already wired in `litellm-config.yaml` as
`hf-qwen3-coder-next` (`huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct`,
≈ $0.38 in / $1.55 out per 1M tokens). A run can target `fallback` like any tier,
and `.relay/config.json` `tierProfiles` can re-point it.

## Approach (suggested)

- `tools/backend/litellm-config.yaml`: add `fallback: hf-qwen3-coder-next` to
  `router_settings.model_group_alias`. The model is already in `model_list`.
- `SwivalProfileSession.DefaultToml`: add a `[profiles.fallback]` block with
  `model = "fallback"`, `base_url = "{ModelBackend.BaseUrl}"`, and
  `max_context_tokens = 256000` (matching the existing `[profiles.qwen-coder]`).
- `RelayConfigLoader.Defaults`: add `["fallback"] = "fallback"` to the
  `TierProfiles` dictionary. No `RelayConfig` shape change is needed —
  `TierProfiles` is already a dictionary, and the loader already merges a
  `tierProfiles` object from `.relay/config.json` over the defaults (lines 78-85),
  so the tier is overridable out of the box.
- README "Model Backend" section: document the `fallback` tier and its default model.

## Files

- `tools/backend/litellm-config.yaml` (`model_group_alias`).
- `src/VisualRelay.Core/Execution/SwivalProfileSession.cs` (`DefaultToml`; keep under 300 lines).
- `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (`Defaults` `TierProfiles`).
- `README.md`.

## Tests (write the failing tests first)

- **RelayConfigLoader**: `Defaults().TierProfiles` contains `fallback` → `fallback`;
  a `.relay/config.json` with `tierProfiles.fallback` overrides it (extend the
  existing `tierProfiles`-merge coverage).
- **SwivalProfileSession**: a prepared `swival.toml` contains a `[profiles.fallback]`
  block with `model = "fallback"` and the centralized `base_url`, asserted against
  the produced TOML (mirror how the session is exercised today).

## Sequencing

Prerequisite for `installer-3-generate-backend-config-from-present-keys.md`, which routes
key-less tiers to this `fallback` tier and appends it under every tier. Keep the
alias name `fallback` **stable** — that task depends on it.

## Done when

- `fallback` is a configurable tier across the litellm aliases, the swival
  profiles, and the default tier map; its default resolves to the HF Novita coder.
- A request for the `fallback` tier reaches the HF coder via the backend.
- `tierProfiles.fallback` in `.relay/config.json` re-points it.
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.

## Notes

This change is purely **additive** — existing tiers and runs behave exactly as
before. The alias-name sync between `litellm-config.yaml` and `SwivalProfileSession`
is load-bearing (see the comment at `SwivalProfileSession.cs:41-43` and the
`litellm-config.yaml` header) — add `fallback` to **both**.
