# Raise the Default Stage Timeout to 45 Minutes

Two consecutive execute runs on 2026-07-06 were killed at exactly the 1800000 ms (30 min)
absolute ceiling while demonstrably productive and close to done:

- A **Fix stage** re-implementing an entire feature was killed with ~190 model completions behind
  it and 629 inserted lines across ~25 files sitting in its kill-time flagged-work bundle — CPU
  and model traffic were active less than a second before the kill.
- An **Implement stage** (`upgrade-nono`) was killed during final verification with all four
  manifest files already edited, including a complete `flake.nix` nono 0.66.0
  `buildRustPackage` override carrying **real** discovered `hash`/`cargoHash` values — proof the
  nix source fetch and cargo vendor cycles had genuinely run. Its watchdog heartbeats show
  silences up to ~102 s (multi-minute local nix downloads/builds between model turns), and model
  calls continued until seconds before the kill.

Neither stage was hung — the 30-minute wall clock is simply too tight for implement/fix stages
whose legitimate work includes compiles, dependency vendoring, or hash-discovery build cycles.
Raise the effective ceiling to 45 minutes (2700000 ms) and make the code default match.

## Current state (researched)

- **The 30-minute value exists in exactly one place**: this repo's tracked `.relay/config.json`
  sets `"subagentTimeoutMs": 1800000`. No source file, test, or doc contains `1800000`.
- **The code default is effectively no backstop**: `RelayConfigLoader.cs` `Defaults(...)` sets
  `SubagentTimeoutMilliseconds: 12_000_000` (200 minutes), used only when the config key is
  absent (`SubagentTimeoutMilliseconds = OptionalInt(root, "subagentTimeoutMs",
  defaults.SubagentTimeoutMilliseconds)`). The field comment in
  `src/VisualRelay.Domain/RelayConfig.cs` documents that stale value: "Default is 12_000_000
  (200 turns × 60 s). Scaled by 10× for tasks in BoostTurnsTaskIds. Set to 0 to disable (not
  recommended)."
- **How the value is consumed** (context, unchanged by this task): `RelayDriver.Invocation.cs`
  `BuildInvocation` passes it as the per-stage absolute ceiling; tasks in `boostTurnsTaskIds`
  get it ×10 (`SaturatingBoost`), so 45 min → 7.5 h boosted — intended; escalated re-runs of a
  non-boosted stage scale it ×2/×4 (`StageEscalation`), so 45/90/180 min across escalations.
- **Hang protection is unaffected**: real hangs are caught much earlier by the per-tier
  first-output watchdog (120 s cheap/balanced, 660 s frontier) and inactivity watchdog (600 s /
  1200 s) — the absolute ceiling is only the hard backstop, and config is read at run start, so
  the new value applies from the next task run after it lands.
- **No existing test asserts the default** — nothing in `tests/` references `12_000_000`.

## What to build

1. **Repo config**: in `.relay/config.json`, change `"subagentTimeoutMs": 1800000` to
   `"subagentTimeoutMs": 2700000`.
2. **Code default**: in `RelayConfigLoader.cs` `Defaults(...)`, change
   `SubagentTimeoutMilliseconds: 12_000_000` to `SubagentTimeoutMilliseconds: 2_700_000`, and
   update the `RelayConfig.cs` field comment to match ("Default is 2_700_000 (45 min). Scaled by
   10× for tasks in BoostTurnsTaskIds. Set to 0 to disable (not recommended).").
3. **Test**: add a defaults assertion so the value is pinned — a small test (pattern of the
   per-key loader test files such as `RelayConfigLoaderCommitProofArtifactsTests.cs`) asserting
   that a config document without `subagentTimeoutMs` loads with
   `SubagentTimeoutMilliseconds == 2_700_000`, and that an explicit value still wins.

## Done when

- `.relay/config.json` carries `"subagentTimeoutMs": 2700000` and the next run's stage ceiling
  message reports the 45-minute value.
- `RelayConfigLoader` defaults `SubagentTimeoutMilliseconds` to `2_700_000` when the key is
  absent, the `RelayConfig.cs` comment says so, and a test pins both behaviors.
- `./visual-relay check` passes (file-size guard, format verification, build, full test suite,
  README screenshot render).

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset). See
  `docs/commit-messages.md` and `AGENTS.md` — e.g. `chore(config): raise stage timeout ceiling
  to 45 minutes`.
- Touch nothing else in the timeout family: `testTimeoutMs`, `firstOutputTimeoutMsByTier`,
  `inactivityTimeoutMsByTier`, `maxTurns`, the ×10 boost, and `StageEscalation` scaling all stay
  exactly as they are.
- Preserve every other key in `.relay/config.json` byte-for-byte; this is a one-value edit.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
