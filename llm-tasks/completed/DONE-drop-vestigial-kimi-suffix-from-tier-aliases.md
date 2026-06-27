# The "-kimi" tier-alias suffix is vestigial — drop it for clean names

The balanced/cheap tier aliases are `balanced-kimi`/`cheap-kimi` although they resolve
to DeepSeek models. The suffix was a workaround for swival's reasoning-content
allowlist (`_needs_reasoning_content` matches substrings of the requested model id).

Empirically refuted 2026-06-10 with swival 1.0.30 against a sandboxed LiteLLM proxy
(subagent experiment, 33 calls): a request-level spy showed swival NEVER round-trips
real `reasoning_content` into resent assistant tool-call history on ANY alias — the
allowlist only gates injection of a `" "` placeholder, and on a generic-provider +
localhost route the stripping gate is inert regardless of alias. Multi-turn
tool-calling succeeded for plain/`-deepseek`/`-kimi` aliases alike (zero 4xx in 32
proxied calls), and DeepSeek upstream (hit directly, litellm bypassed) returns 200
for all three wire-shapes of tool-call history: rc absent, rc placeholder, real rc.
The suffix protects nothing on this stack.

## Goal

Tier aliases are the clean tier names: `balanced` and `cheap`. Nothing in the repo
references `balanced-kimi`/`cheap-kimi` anymore. Known reference sites (verify
completeness rather than trusting this list): `BackendConfigGenerator.cs`,
`SwivalProfileSession.cs` (DefaultToml profile model ids), `RelayPricing.cs`,
`RelayCostEstimator.cs`, `tools/VisualRelay.Screenshots/Program.cs`,
`tools/backend/litellm-config.yaml` (`model_group_alias`, `fallbacks` keys, comments).

Record the one watch-for in a comment where the aliases are defined: if DeepSeek ever
starts ENFORCING reasoning_content on tool-call history, the failure signature is
HTTP 400 from turn 2 of tool-calling stages — that day, swival's placeholder
injection (alias containing "deepseek") is the known mitigation.

## Approach (suggested)

- Mechanical rename + replace the now-obsolete "Swival quirk" comments with the
  empirical note above.
- Add a consistency test cross-checking the alias-name sets used by the
  backend-config generator, the swival profile template, and the pricing table, so a
  future re-target fails tests until every site is updated together.
- Do NOT restart or regenerate the live backend from the pipeline; the operator
  swaps the running backend at a drive boundary (old aliases keep serving in-flight
  drives until then).
