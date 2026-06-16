# Harness: let nix-managed projects install dependencies in-sandbox (nix cache + state writes, NOT a writable store)

For a target project that uses **nix** to manage dependencies, an LLM task that adds a dependency
(edits `flake.nix`, runs `nix develop` / `nix build` / `nix profile install`) **failed under the
`vr-guard` sandbox**: the profile grants `/nix/store` read+exec (via `read:["/"]`) and network, but
**not** the per-user cache/state directories nix writes while it works. This task closes that gap —
without making the store writable (which is neither how nix works nor safe).

General harness/sandbox change (VR drives any codebase, incl. nix-managed ones). Platform-agnostic.

## What actually blocked it (live red, 2026-06-16)

Running under the current profile:

```
nono run -p vr-guard --allow-cwd -- /bin/sh -c "nix build nixpkgs#hello"
nono run -p vr-guard --allow-cwd -- /bin/sh -c "nix shell nixpkgs#cowsay -c cowsay hi"
```

failed with:

- `attempt to write a readonly database (in '/Users/.../.cache/nix/fetcher-cache-v4.sqlite')`
- `error: opening lock file "/Users/.../.cache/nix/fetcher-locks/<hash>.lock": Operation not permitted`

So the blocker is the **nix cache/state directories**, NOT the daemon socket and NOT a writable store:

- **The daemon socket already works.** `nix store ping` succeeds in-sandbox; the existing
  `unsafe_macos_seatbelt_rules` (`(allow system-socket)` + ipc rules) are sufficient for the client to
  reach `nix-daemon`. No extra `filesystem.allow` of the socket path or new Seatbelt rule is needed.
  (An earlier spec over-emphasized the socket; the empirical red proved otherwise.)
- **`/nix/store` stays read-only and that is correct.** nix is a multi-user (daemon) install — the
  store is `root:nixbld`, content-addressed and immutable. Nobody (sandboxed or not) writes it
  directly; the privileged daemon adds new store paths on request. Granting write to `/nix/store`
  would neither enable dep-install nor be safe, so it must NOT be added to `allow`/`write` — it stays
  read+exec via the existing `read:["/"]`.
- **What was missing: the per-user cache + state writes.** `~/.cache/nix` holds the eval/fetcher cache
  and its lock files (the confirmed readonly-db + lock blocker). `~/.local/state/nix` holds per-user
  state (profiles, gcroots, history) needed for `nix profile install` / gcroot creation. Neither is
  covered by the profile's `XDG_CACHE_HOME → ~/.config/swival/cache` redirect (nix uses `~/.cache/nix`
  directly), and a prior change narrowed the `$HOME/.local` grant, so both must be granted explicitly.

## What was built

- Added a **nix toolchain** grant set to `packaging/nono/vr-guard.json`, alongside the existing
  per-ecosystem grants (`~/.nuget`, `~/.swiftpm`, `~/.cargo`, …):
  - `$HOME/.cache/nix` (read+write) — fetcher/eval cache + locks; the confirmed blocker.
  - `$HOME/.local/state/nix` (read+write) — per-user state: profiles, gcroots, history.
- These XDG paths are identical on macOS and Linux, so **no `when` predicate** is needed.
- `/nix/store` is left read-only; the daemon socket is left untouched (already works).
- This is consistent with how the profile already grants the other ecosystems — it is the **nix**
  ecosystem's equivalent. Baked into the shipped baseline (harmless for non-nix repos; simply unused).

## Threat-model note (for the sandbox-no-escape invariant)

Granting the nix cache/state lets the agent run arbitrary `nix build`, which executes derivation build
code — but **inside nix's own build sandbox**, with outputs going only to `/nix/store` (immutable,
daemon-owned, isolated). It does **not** grant write to the user's documents/credentials: the store
stays read-only, and the `deny_*` groups (credentials/keychains/shell-configs/browser/macos-private)
still **override** every allow. So this widens *capability* (use nix packages) consistent with
"accident containment, not adversarial isolation" — it is **not** a new escape to the sensitive
surface. The only sanctioned no-sandbox path remains the `bypassSandbox` toggle.

## Tests

- **Structural (always-run, cheap):** `NonoProfileStructureTests.VrGuardProfile_HasNixEntries` asserts
  `filesystem.allow` contains `$HOME/.cache/nix` and `$HOME/.local/state/nix`. Runs in the default
  `dotnet test` suite (pure JSON parse, no nono shell-out).
- **Oracle (`nono why`, runs when nono is on PATH, skipped otherwise):**
  `NonoWhyOracleTests.NonoWhy_NixCache_AllowedReadWrite` and `NonoWhy_NixState_AllowedReadWrite` assert
  those paths are read+write-allowed; `NonoWhy_NixStore_DeniedWrite` asserts `/nix/store/<x>` write
  stays **DENIED** (the credential/keychain surface stays denied via the existing `NonoWhy_*Denied`
  tests).
- **Real in-sandbox build (orchestrator-run red/green, NOT a default-suite test):**
  `nono run -p vr-guard --allow-cwd -- /bin/sh -c "nix build nixpkgs#hello"` (and a `nix shell …`)
  **succeeds** in-sandbox, proving the cache/state grants are sufficient and the store stays
  read-only. Deliberately kept out of the default suite (too heavy — keep verify fast).

## Done when

- A nix-managed project can install/use a dependency in-sandbox: a real `nix build` / `nix shell` that
  fetches/builds into a new store path succeeds under `vr-guard`, via the daemon — with `/nix/store`
  remaining read-only and the credential/keychain/etc. surface still write-denied.
- `vr-guard.json` grants the nix user cache (`~/.cache/nix`) + state (`~/.local/state/nix`), not a
  writable store and not the (already-working) daemon socket.
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit subjects; default
  `dotnet test` stays fast (the real nix build is orchestrator-run, opt-in only).
