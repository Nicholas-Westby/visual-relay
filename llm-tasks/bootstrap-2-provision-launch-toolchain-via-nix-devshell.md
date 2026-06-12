# The devshell can't satisfy launch prerequisites: nono and uv are missing from the flake, and nix re-entry only triggers on missing dotnet

On a machine with Nix installed, `./visual-relay launch` should be able to
self-provision everything it needs without installing anything globally — that is
what the devshell is for. Today it can't, for two reasons:

1. The devshell (`flake.nix:19-32`) carries `dotnet-sdk_10`, git, bash, icu,
   openssl, zlib — but not `nono` (the hard sandbox prerequisite from
   `sandbox-2-make-nono-a-required-dependency-of-the-installer-and-launcher.md`)
   and not `uv` (which `tools/backend/backend.sh` needs to provision litellm).
2. The launcher only re-enters nix when **dotnet** is missing
   (`_require_dotnet`, `visual-relay:30-51`). If dotnet happens to be on PATH,
   nix is never consulted, and a missing nono hard-fails at
   `_require_nono` (`visual-relay:72-92`) while a missing uv leaves the backend
   down.

Evidence (2026-06-11, host): Determinate Nix 3.14.0 was installed and idle;
dotnet was on PATH, so re-entry never fired; launch hard-exited on missing nono
after first burning the backend-start attempt (the `launch` case starts the
backend at `visual-relay:191` *before* the nono gate at `visual-relay:195-198`).
Verified the same day at the pinned nixpkgs rev (`4100e830`, flake.lock
2026-05-27): `pkgs.nono` = 0.53.0, `pkgs.uv` = 0.11.16, `pkgs.python313` =
3.13.13 — all present.

## Goal

On a machine with Nix and nothing else, `./visual-relay launch` runs fully
sandboxed with a healthy backend and **zero global installs**: the devshell
supplies dotnet, nono, uv, and python 3.13; the launcher re-enters the devshell
(once) whenever any required tool is missing; prerequisite checks run before the
backend start so failures are fast and the devshell's uv is visible to
backend.sh.

## Approach (suggested)

- **flake.nix**: add `uv`, `nono`, and `python313` to the devshell packages
  (python313 so uv binds the devshell interpreter instead of downloading one;
  `backend.sh` already pins `--python 3.13`).
- **Launcher — one prerequisite gate**: extract the per-command requirements
  into a `_missing_required_tools()` helper: `dotnet` (unless `HAS_PUBLISHED`),
  `nono` (only when the sandbox is enabled per `_read_bypass_sandbox`,
  `visual-relay:60-70`), plus `uv` as a *soft* want for `launch|run`. If any are
  missing and nix is available (reuse the probe order in `visual-relay:36-42`),
  re-exec once into `nix develop` exactly as `_require_dotnet` does today —
  preserving `ARGS` byte-for-byte (the fix from
  `launcher-nix-reentry-drops-arguments.md`). Export a marker
  (e.g. `VISUAL_RELAY_NIX_REENTRY=1`) on the exec; if the marker is set and a
  hard tool is still missing, fall through to the existing per-tool errors
  (`_require_nono` message, dotnet 127) instead of looping. Missing-uv alone
  never hard-fails — backend degradation stays graceful as today.
- **Ordering** (`launch|run` case, `visual-relay:186-203`): run the gate (and
  re-entry) first, then `_provision_nono`, then the backend start, then the app.
  This both fixes the "wait for the backend, then die on nono" sequence and
  ensures backend.sh sees the devshell's uv.
- Apply the same gate to `run-task` (`visual-relay:223-229`).

## Files

- `flake.nix` (devshell packages).
- `visual-relay` (prerequisite gate, re-entry, ordering).
- `tests/VisualRelay.Tests/` (launcher stub tests).
- `README.md` / `TROUBLESHOOTING.md` ("have Nix? everything else is automatic").

## Tests (write the failing tests first)

Reuse the hermetic stub pattern from the `launcher-nix-reentry-drops-arguments`
regression test (crafted PATH, stub `nix` recording argv):

- PATH with dotnet but **no nono**, sandbox enabled, stub nix → `launch`
  re-execs via `nix develop` with subcommand and arguments (including a
  space-containing one) intact.
- `bypassSandbox: true`, dotnet present, no nono → **no** re-entry, launch
  proceeds (nono not required).
- Re-entry marker set, nono still missing → exits 127 with the existing
  `_require_nono` message; stub nix records **no** second invocation (no loop).
- No nono and no nix on PATH → exits before the backend script runs (stub
  `backend.sh` records it was never invoked) — proves the ordering change.

## Sequencing

Pairs with `bootstrap-1-relocate-per-machine-backend-state-to-user-data-dir.md`
(devshell uv + per-home venv together make the backend self-provision on the
host). Builds on the DONE `launcher-nix-reentry-drops-arguments` fix.
`bootstrap-3-offer-determinate-nix-install-with-consent.md` extends the no-nix
branch.

## Done when

- A machine with Nix, no nono, no uv, no dotnet: `./visual-relay launch` enters
  the devshell automatically and comes up sandboxed with a healthy backend;
  nothing is installed outside the nix store.
- Re-entry happens at most once; without nix the existing clear errors remain.
- The nono gate runs before any backend work.
- `./visual-relay check` green; C#/AXAML files under the 300-line guard;
  Conventional Commit subjects.

## Notes

- **nono version skew**: pinned nixpkgs has nono 0.53.0; homebrew currently
  ships 0.62.0. Verify the features used by sandbox-1/2 (`nono pull
  jedisct1/swival`, `--sandbox nono --nono-profile vr-guard`) work on 0.53.0; if
  not, bump the flake.lock pin or pin a newer nono via a small fetch derivation
  in the flake. Do not silently weaken the sandbox to accommodate an old nono.
- **litellm stays uv-provisioned — decision, 2026-06-11.** nixpkgs' top-level
  `litellm` (1.86.0 at our pin and current master) does wire in the proxy
  extras (`bin/litellm`, `bin/litellm-proxy`), but it does **not build** on
  aarch64-darwin: no binary-cache hit, and the source build fails in
  `a2a-sdk` 0.3.26's e2e tests (`AttributeError: Can't get local object
  'FastAPI.setup.<locals>.openapi'` — macOS spawn-pickling; unfixed on master).
  An overlay disabling a2a-sdk's checks builds and runs, but that is a patch we
  would own in the launch path, and nixpkgs litellm trails PyPI (1.86.0 vs
  1.88.1) by weeks. **Flip condition**: when plain
  `nix build nixpkgs#litellm` is green on aarch64-darwin at the repo's pin,
  consider replacing the uv venv with the devshell package and deleting the
  provisioning layer.
- Determinate Nix enables flakes by default; do not add channel-era fallbacks.
