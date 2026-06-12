# Without Nix the launcher dead-ends on missing tools; offer to install Determinate Nix — gated on an explicit y/N, defaulting to no

After `bootstrap-2-provision-launch-toolchain-via-nix-devshell.md`, a machine
*with* Nix self-provisions the whole launch toolchain through the devshell. A
machine *without* Nix still dead-ends: missing dotnet prints "Install .NET 10 or
run: nix develop" (`visual-relay:49-50`), missing nono prints the install text
and exits 127 (`visual-relay:77-91`). The one thing standing between that
machine and a fully automatic launch is Nix itself.

Nix is a **host-global install**, and the product rule for globals is explicit
per-prompt consent: ask y/N, default no, install only on an explicit yes —
never silently, never remembered as a blanket "always yes", and never in a
non-interactive context. (Determinate Nix specifically, for reliability: flakes
on by default, `determinate-nixd`, survives macOS updates.)

## Goal

On a TTY, when required tools are missing and Nix is absent, the launcher offers
exactly one question — install Determinate Nix? — and a "y" produces a fully
working sandboxed launch with no other interaction. "n", Enter, or a
non-interactive context changes nothing: today's clear failure messages, plus
the one-line manual install command.

## Approach (suggested)

- **Hook point**: the no-nix branch of the prerequisite gate built in
  bootstrap-2 (where today's hard errors fire). Only reached when a required
  tool is missing AND none of the nix probes (`visual-relay:36-42`) hit.
- **Prompt** (TTY only — `[[ -t 0 && -t 1 ]]`):
  `visual-relay: <tools> missing. Install Determinate Nix to provide them? [y/N]`
  Read one line; anything but an explicit `y`/`yes` is a no.
- **On yes**: run the official installer
  (`curl -fsSL https://install.determinate.systems/nix | sh -s -- install`),
  letting the installer keep its own interactive confirmations (do not pass
  `--no-confirm`). On installer success, locate nix at
  `/nix/var/nix/profiles/default/bin/nix` and re-exec the launcher with the
  original `cmd`/`ARGS` so the bootstrap-2 re-entry takes over.
- **On no / non-TTY**: print the existing error plus the manual one-liner and
  exit 127. Non-TTY must not even prompt.
- **Testability**: route the installer invocation through an overridable
  command (e.g. `VISUAL_RELAY_NIX_INSTALLER`, defaulting to the curl pipeline)
  so tests substitute a stub; never let tests reach the network.
- No `--yes` flag, no config key to pre-approve the install — per-prompt
  consent is the contract.

## Files

- `visual-relay` (prompt, installer invocation, re-exec).
- `tests/VisualRelay.Tests/` (launcher stub tests).
- `README.md` (first-run story: "no Nix? the launcher offers to install it").

## Tests (write the failing tests first)

Hermetic stubs throughout (`VISUAL_RELAY_NIX_INSTALLER` → stub recording argv;
PATH with no nix/nono/dotnet):

- stdin `y` on a simulated TTY → installer stub invoked once; with a stub nix
  planted at the probed path, the launcher re-execs with subcommand and
  arguments preserved.
- stdin `n` and stdin empty → installer **never** invoked, exit non-zero,
  output contains the manual `curl ... install.determinate.systems` line.
- stdin redirected from a file (non-TTY) → no prompt text appears at all,
  installer never invoked, exit non-zero with the existing messages.
- nix already present → prompt never appears regardless of other missing tools
  (bootstrap-2 re-entry path owns that case).

## Sequencing

After `bootstrap-2-provision-launch-toolchain-via-nix-devshell.md` — installing
Nix only helps because the re-entry then provisions everything else. Last task
in the `bootstrap-*` series.

## Done when

- Fresh no-Nix machine, interactive: one `y` at the prompt yields a sandboxed
  launch with a healthy backend; `n`/Enter yields a clean failure with manual
  instructions; nothing global is ever installed without the explicit yes.
- Non-interactive contexts (CI, app-spawned, piped) never prompt and never
  install — byte-identical behavior to today plus the manual hint.
- `./visual-relay check` green; C#/AXAML files under the 300-line guard;
  Conventional Commit subjects.

## Notes

- The Determinate installer is idempotent and handles macOS specifics (APFS
  volume, daemon); the launcher should not reimplement any of its checks beyond
  "is nix already present" (`visual-relay:36-42`).
- Keep the prompt copy honest about scope: installing Nix is the only global
  change; everything else lands in the nix store / per-user data dirs.
- If the user declines, do not re-ask within the same invocation; every new
  invocation may ask again (no persisted "asked already" state — that would be
  a hidden consent cache).
