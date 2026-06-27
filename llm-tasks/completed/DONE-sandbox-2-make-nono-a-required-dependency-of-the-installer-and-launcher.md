# Nothing ensures `nono` is installed, so the guard from sandbox-1 silently does nothing on a fresh machine

`sandbox-1-run-swival-under-the-nono-guard-sandbox.md` makes Visual Relay pass
`--sandbox nono --nono-profile vr-guard` to Swival, but nothing guarantees the `nono` binary,
the `vr-guard` profile, or the `swival` base profile it extends are actually present. The
launcher's prerequisite block (`visual-relay:17-34`) only checks for `dotnet`/`nix`; the
backend only checks/provisions `uv`+`litellm` (`tools/backend/backend.sh:62-101`); and the
Homebrew formula in `installer-5-distribute-via-homebrew-formula.md` only `depends_on "uv"`.
On a machine without nono, the sandbox flags would error or be skipped and the delete-protection
would be absent **without anyone noticing**.

Per the product decision, nono is **not** a best-effort enhancement with a builtin fallback —
it is a **required** part of the install/launch path. The only supported way to run without it
is the explicit `bypassSandbox` opt-out (the UI checkbox in `sandbox-3-...`), never an automatic
downgrade.

## Goal

Make the `nono` sandbox a first-class prerequisite: the installer depends on it, the launcher
verifies it (and the `vr-guard` profile + `jedisct1/swival` pack) and **hard-errors with install
instructions** when the sandbox is enabled and nono is missing, and provisioning of the pack +
profile is idempotent. No silent fallback to Swival's `builtin` sandbox.

## Approach (suggested)

- **Homebrew formula** (`packaging/visual-relay.rb`, created in `installer-5`): add
  `depends_on "nono"`. `nono` installs via `brew install nono`; confirm the formula/tap name and
  add the tap to the formula if `nono` is not in homebrew-core. (Reference: the `uv` dependency
  handling in `installer-5`.)
- **Launcher prerequisite check** (`visual-relay`, alongside the `dotnet`/`nix` block at lines
  17-34): for the dotnet-running subcommands, when the sandbox is enabled (i.e. `.relay/config.json`
  does not set `bypassSandbox: true`), run `command -v nono`; if absent, print an install hint and
  **exit non-zero** — mirror the style of `missing_toolchain_message()` (`backend.sh:88-101`). Do
  **not** fall back to `--sandbox builtin`.
- **Idempotent provisioning** (a new step in the launcher's startup, or `tools/VisualRelay.Init`):
  - `nono pull jedisct1/swival` — installs the `swival` base profile (which `vr-guard` extends)
    plus the `nono-sandbox` skill that teaches Swival to self-diagnose denials. Sigstore-signed;
    skip if already installed.
  - Copy `packaging/nono/vr-guard.json` →
    `${XDG_CONFIG_HOME:-$HOME/.config}/nono/profiles/vr-guard.json` if missing or stale.
  - Keep this idempotent and quiet on the happy path (health-check style, like `backend.sh start`).
- **Respect the bypass opt-out**: when `bypassSandbox` is set, skip the nono requirement and the
  provisioning — that is the *only* no-nono path, and it is an explicit user choice, not a
  fallback.

## Files

- `packaging/visual-relay.rb` (formula `depends_on "nono"`; coordinate with `installer-5`).
- `visual-relay` (launcher: nono prereq hard-check + idempotent pack/profile provisioning).
- `tools/VisualRelay.Init/Program.cs` (optional: provision pack/profile during `init`).
- `packaging/nono/vr-guard.json` (consumed here; created in `sandbox-1`).
- `README.md` / onboarding docs (state nono is required; `brew install nono`; the bypass opt-out).

## Tests (write the failing tests first)

- **Launcher guard** (bats/shell test, mirroring how the repo tests `visual-relay`): with the
  sandbox enabled and `nono` absent from `PATH`, the launcher exits non-zero and prints a message
  naming `nono` and the install command; with `bypassSandbox` set, it runs without requiring nono.
- **Provisioning idempotence**: running the provisioning step twice installs the profile once and
  is a no-op the second time; an existing user-modified profile is not clobbered (or is updated
  only when stale — pick one and test it).

## Sequencing

Depends on `sandbox-1` (the `--sandbox nono` flags, the `bypassSandbox` config key, and the
`packaging/nono/vr-guard.json` artifact). Coordinates with
`installer-5-distribute-via-homebrew-formula.md` (same formula file) — land after or alongside it.

## Done when

- A fresh install path ensures `nono`, the `jedisct1/swival` pack, and the `vr-guard` profile are
  present.
- With the sandbox enabled and `nono` missing, `./visual-relay launch` (and `run-task`) exit
  non-zero with a clear install message — **no** silent `builtin` fallback.
- Setting `bypassSandbox` is the only way to run without nono, and it is honored.
- Provisioning is idempotent and quiet on the happy path.
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.

## Notes

- `nono` is Apache-2.0 and supports macOS (Seatbelt) and Linux (Landlock); `brew install nono`.
- The `nono pull jedisct1/swival` step needs network + a one-time Sigstore trust verification at
  install time (the backend install already performs network work, so this fits the existing
  model). **Alternative to consider:** make `vr-guard` self-contained by extending `default`
  instead of `swival` and inlining the swival config-dir grants (rw to `~/.config/swival`,
  `~/.local/share/swival`, `~/.config/nono/profile-drafts`; read `~/.config/nono/packages`,
  `~/.config/nono/profiles`) — this removes the pack-pull network dependency, at the cost of the
  bundled `nono-sandbox` skill. The verified profile in `sandbox-1` extends `swival`; if you
  switch to `default`, re-verify the guard behavior.
- Keep the nono requirement gated on "sandbox enabled" so the documented `bypassSandbox` opt-out
  still lets a user run on a machine without nono.
