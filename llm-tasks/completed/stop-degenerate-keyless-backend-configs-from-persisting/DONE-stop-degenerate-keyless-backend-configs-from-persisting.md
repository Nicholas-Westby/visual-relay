## Task: Stop degenerate keyless backend configs from being generated or persisting

When the backend config generator detects ZERO provider keys, it rewrites
every tier alias to the `fallback` pseudo-model and collapses every
fallback chain to `[fallback]`
(`BackendConfigGenerator.ResolveTiers`, the `chain.Count == 0` branch).
That routing funnels all traffic through one HF model on one provider —
and once written, it persists indefinitely: the launch path treats a
healthy proxy as a fast no-op, so nothing ever compares the config the
proxy is running against what generation would produce now.

A zero-key rewrite is strictly worse than the static template. Every
`model_list` entry (including the HF floor) resolves its key from
`os.environ` at request time, so with truly no keys NOTHING works under
either config — but when keys ARE present in the proxy's process env (the
spawn path layers them from `~/.config/visual-relay/.env` via
`BackendLifecycle.LoadProviderKeys`), the template gives full
provider-diversified routing while the degenerate rewrite silently
destroys it. Key detection at generation time
(`BackendConfigStep.Generate`, which builds the present-set from
`KeyEnvFile.Read()` + raw process env) and key provisioning at spawn time
can disagree — and did.

### Evidence (verified)

- Field incident: the proxy started 2026-07-15 18:38:42 ran a generated
  config with `frontier: fallback`, `balanced: fallback`, `cheap:
  fallback`, `fallbacks: [fallback]` for every tier and no vision/claude
  aliases — the exact zero-keys output — while
  `~/.config/visual-relay/.env` had held HF/DeepSeek/Moonshot keys since
  2026-07-07 and the proxy's own env had them (all requests worked, all
  through the single HF model). It ran that way for two days.
- 2026-07-17 (~21:07Z): Hugging Face retired the legacy route behind that
  single model; with zero routing diversity the whole pipeline failed
  (three stage-1 attempts, litellm: "no healthy deployments … Model
  Group=frontier … Available Model Group Fallbacks=['fallback']") even
  though DeepSeek, Moonshot, and GLM-5.2 upstreams were all healthy and
  the keys were valid. The diversified chains exist precisely to survive
  this event, and the stale degenerate config defeated them.
- Same day 21:40Z: regeneration through the app with the same `.env`
  produced the correct config (frontier→glm-5.2, balanced→deepseek-v4-pro,
  cheap→deepseek-v4-flash, full chains) — proving the keyless generation
  came from a transient environment-resolution failure at one particular
  start, not from bad key data.
- `BackendConfigStep.ResolveAsync` already prefers the static template on
  generation failure/timeout ("using static config") — the zero-key case
  belongs on that same path. It also discards the generator's summary
  (`var (yaml, _) = …`), so nothing durable records which keys generation
  actually saw.

### What to build

1. **Zero-key guard**: when the detected present-key set is empty,
   do not write or use a generated rewrite — launch with the static
   template and log why (same pattern as the existing
   generation-failure fallback). If the user-level key file exists and
   parses to a non-empty key set while detection still sees nothing,
   surface that loudly as an environment-resolution failure (backend
   panel / status text), not silently.
2. **Staleness check**: when the launch/start path finds the proxy
   already healthy, regenerate the config in memory and compare it to the
   generated file the proxy was started with. On drift, restart the proxy
   with the fresh config — gated on no active run (never mid-drain; defer
   with a status note instead). A matching config keeps today's fast
   no-op.
3. **Durable generation summary**: persist the generator's one-line
   summary (tier→model resolutions + detected keys) at every generation —
   e.g. into the backend scratch log — so the next incident shows what
   generation saw, when.

### Constraints

- No changes to `ResolveTiers` chain contents, tier names, or the
  template itself.
- Keys must never be logged — key NAMES only.
- Restart gating must use TimeProvider-friendly async patterns; no
  real-time waits in tests; 300-line file guard.

### Tests (red first)

- Generation with an empty present-set: static template path chosen, no
  generated file written — fails today (degenerate yaml is written).
- Key file non-empty but detection empty: loud surface triggered — fails
  today (silent).
- Healthy proxy whose on-disk generated config differs from a fresh
  generation: restart-with-fresh-config triggered when idle — fails today
  (no-op).
- Same scenario mid-run: no restart, deferred with a status note.
- Matching config: no restart (pins the fast no-op).
- Generation summary line lands durably on every generation, containing
  tier resolutions and key names only.

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: hand-write a degenerate generated config into scratch, start
  the app with a healthy proxy running it and valid keys on disk: the
  proxy restarts onto the corrected config and the summary line is in the
  log. Then relaunch once more: fast no-op (configs match).
