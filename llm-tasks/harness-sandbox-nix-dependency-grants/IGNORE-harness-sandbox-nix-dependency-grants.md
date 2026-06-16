> **DEFERRED (2026-06-16) — spec'd, not driven.** A real gap for nix-managed *target* projects, but
> not currently exercised: VR self-hosts via a pre-provisioned nix devshell and does not install nix
> deps mid-task, and no nix-managed target repo is in play. The daemon-socket grant can only be *truly*
> validated by a real in-sandbox `nix build` (the opt-in integration test below) against an actual nix
> project — so driving it now would commit a plausible-but-unvalidated grant set. Un-IGNORE and drive
> this **when a nix-managed project needs in-sandbox dependency installation**, validating with a real
> `nono run -p vr-guard -- nix build`. The store stays read-only regardless. Full spec below.

# Harness: let nix-managed projects install dependencies in-sandbox (daemon socket + nix state, NOT a writable store)

For a target project that uses **nix** to manage dependencies, an LLM task that adds a dependency
(edits `flake.nix`, runs `nix develop` / `nix build` / `nix profile install`) currently **fails under
the `vr-guard` sandbox**: the profile grants `/nix/store` read+exec (via `read:["/"]`) and network, but
**not** the way nix actually installs things. This task closes that gap — without making the store
writable (which is neither how nix works nor safe).

General harness/sandbox change (VR drives any codebase, incl. nix-managed ones). Platform-agnostic.

## Why "make /nix/store writable" is the wrong model

Verified on this host (2026-06-16): nix is a **multi-user (daemon) install** — `/nix/store` is owned
`root:nixbld` and is **not** user-writable. Nobody (sandboxed or not) writes the store directly; the
privileged **`nix-daemon`** does, on request over a **unix socket** (`/nix/var/nix/daemon-socket/socket
→ /var/run/nix-daemon.socket`). The store is content-addressed and immutable — a new dependency is a
**new** store path the daemon adds. So granting write to `/nix/store` would not enable dep-install
(the client can't write there even unsandboxed) and would violate nix's integrity model. (On a rare
single-user nix install the store IS user-writable, but the daemon-socket path below is the portable
mechanism; don't special-case a writable store.)

## What nix dependency operations actually need in-sandbox

1. **The nix daemon socket — read+write.** `/nix/var/nix/daemon-socket/socket` (macOS symlinks to
   `/var/run/nix-daemon.socket`; Linux: `/nix/var/nix/daemon-socket/socket`). The client talks to the
   daemon over this socket; the daemon performs the privileged store writes. The existing
   `unsafe_macos_seatbelt_rules` already include `(allow system-socket)` / ipc rules — verify whether
   the socket also needs an explicit `filesystem.allow` of the socket path and/or an additional
   Seatbelt rule, by running a real `nix build` in-sandbox (see Tests).
2. **Per-user nix state + cache — write.** `$HOME/.local/state/nix` (profiles, gcroots, history) and
   `$HOME/.cache/nix` (eval/fetcher cache). NOTE: the allowlist no longer grants all of `$HOME/.local`
   (narrowed in 67dcb10), so `~/.local/state/nix` must be granted **explicitly**. `~/.cache/nix` is
   not covered by the `XDG_CACHE_HOME→~/.config/swival/cache` redirect (nix uses `~/.cache/nix`
   directly), so grant it explicitly too. Also the per-user profile/gcroot roots:
   `/nix/var/nix/profiles/per-user/$USER` and `/nix/var/nix/gcroots/per-user/$USER` (write).
3. **Network** — already granted (`net outbound allowed`) for substituter downloads.
4. **`/nix/store` stays read-only** — read+exec only (already via `read:["/"]`); do NOT add it to
   `allow`/`write`.

## What to build

- Add a **nix toolchain** grant set to `packaging/nono/vr-guard.json`, alongside the existing per-
  ecosystem grants: the daemon socket (and its symlink target) as read+write; `$HOME/.local/state/nix`,
  `$HOME/.cache/nix`, `/nix/var/nix/profiles/per-user/$USER`, `/nix/var/nix/gcroots/per-user/$USER` as
  read+write; use `$HOME`/`when` predicates for the OS-specific socket path. Keep `/nix/store`
  read-only. Add any Seatbelt/IPC rule the daemon socket needs (determine empirically).
- This is consistent with how the profile already grants `~/.nuget`, `~/.swiftpm`, etc. for other
  ecosystems — it is the **nix** ecosystem's equivalent. Bake it into the shipped baseline (harmless
  for non-nix repos; the grants are simply unused).

## Threat-model note (for the sandbox-no-escape invariant)

Granting the daemon socket lets the agent run arbitrary `nix build`, which executes derivation build
code — but **inside nix's own build sandbox**, with outputs going only to `/nix/store` (immutable,
isolated). It does **not** grant write to the user's documents/credentials: the store stays
daemon-owned, and the `deny_*` groups (credentials/keychains/shell-configs/browser/macos-private) still
**override** every allow. So this widens *capability* (install nix packages) consistent with
"accident containment, not adversarial isolation" — it is **not** a new escape to the sensitive
surface. The only sanctioned no-sandbox path remains the `bypassSandbox` toggle.

## Tests

- **Unit / oracle (`nono why`, always-run cheap tier):** `nono why -p vr-guard --op readwrite --path
  /nix/var/nix/daemon-socket/socket` is **allowed**; `~/.local/state/nix`, `~/.cache/nix` are
  read+write-allowed; **`/nix/store/<x>` write stays DENIED**; and `~/.ssh`/keychains stay DENIED
  (the grants didn't open the sensitive surface).
- **Integration (opt-in `VR_RUN_NONO_INTEGRATION=1`, skipped by default):** in a scratch nix flake,
  `nono run -p vr-guard --allow-cwd -- /bin/sh -c "nix build .#<a-small-pkg>"` (or `nix develop
  --command true` that pulls a new input) **succeeds** in-sandbox, proving the daemon socket + state
  grants are sufficient. Do NOT add this to the default suite (keep verify fast — see
  [[vr-verify-loop-flag-diagnosis]]).

## Done when

- A nix-managed project can install a dependency in-sandbox: a real `nix build`/`nix develop` that
  fetches/builds a new store path succeeds under `vr-guard`, via the daemon — with `/nix/store`
  remaining read-only and the credential/keychain/etc. surface still write-denied.
- `vr-guard.json` grants the daemon socket + nix user state/cache (per-OS), not a writable store.
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit subjects; default
  `dotnet test` stays fast (the real nix build is opt-in only).
