# Tier aliases carry a misleading "-kimi" suffix; rename to honest provider names

The balanced/cheap tier aliases are named `balanced-kimi`/`cheap-kimi` although they
resolve to DeepSeek models — a workaround for swival's `_needs_reasoning_content`
allowlist, which preserves `reasoning_content` only when the *requested model id*
contains an allowlisted substring. Through the LiteLLM proxy, swival requests the
ALIAS name, so the alias itself had to smuggle in an allowlisted word, and at the
time "kimi" was chosen.

Investigated 2026-06-10 against installed swival 1.0.28 and 1.0.30 (upstream
issue #25): the allowlist in BOTH versions already matches `"deepseek" in
model_lower` (the upstream fix shipped before 1.0.28). What is NOT possible is
dropping the provider hint entirely — a bare `balanced` alias matches nothing, the
reasoning gets stripped, and DeepSeek V4 thinking models 400 on multi-turn tool
calls (no `--preserve-reasoning` style override exists in 1.0.30). So the suffix
cannot be *removed*, but it can stop lying: `-kimi` → `-deepseek`.

## Goal

Every reference to the balanced/cheap tier aliases uses `balanced-deepseek` /
`cheap-deepseek`; nothing references `balanced-kimi`/`cheap-kimi` anymore. The
naming constraint is documented where the names are defined: an alias served to
swival must contain a substring of its true underlying provider that swival's
reasoning-content allowlist recognizes (currently "kimi", "mimo", "deepseek").

Known reference sites (verify completeness, don't trust this list):
`BackendConfigGenerator.cs`, `SwivalProfileSession.cs`, `RelayPricing.cs`,
`RelayCostEstimator.cs`, `tools/VisualRelay.Screenshots/Program.cs`,
`tools/backend/litellm-config.yaml` (static template; the generated config follows
the generator).

## Approach (suggested)

- Mechanical rename + comment updates explaining the provider-hint constraint
  (replacing the current "Swival quirk" comment, which after this change would
  describe a problem that no longer exists by its old name).
- Add a consistency test that cross-checks the alias name sets used by the
  backend-config generator, the swival profile templates, and the pricing table —
  so a future re-target (e.g. balanced moves off DeepSeek) fails tests until every
  site (and the provider hint!) is updated together.
- Do NOT restart or regenerate the live backend from the pipeline; the operator
  swaps the running backend at a drive boundary (old aliases keep serving
  in-flight drives until then).
