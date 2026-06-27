# Always re-enter the nix devshell, not only when a required tool is missing

Running `./visual-relay launch` prints `visual-relay: missing required tools; entering nix
develop for this command` and only re-execs into `nix develop` when it detects a *missing*
tool. The devshell should be the canonical environment for **every** command: when nix is
available, always enter it (the host runs Determinate Nix; this repo is nix-first). Make the
re-entry unconditional rather than gated on a missing tool.

## Current state (researched)

Re-entry is gated on missing tools in two places in the launcher (`visual-relay`):

- **`_missing_required_tools()` (`visual-relay:113-151`)** computes `missing_any` from dotnet
  (hard unless `HAS_PUBLISHED`), nono (hard unless `bypassSandbox`), and uv (soft). Only when
  something is missing **and** `_find_nix` succeeds **and** `VISUAL_RELAY_NIX_REENTRY` is
  unset does it
  `exec env -u DOTNET_ROOT VISUAL_RELAY_NIX_REENTRY=1 ORIGINAL_CWD="$ORIGINAL_CWD" "$nix_bin" develop --command bash "$0" "$cmd" ${ARGS:+"${ARGS[@]}"}`
  (`:144-146`). The message the user sees is at `:144`. Called for `launch`/`run` (`:291`,
  with `check_uv`) and `run-task` (`:328`).
- **`_require_dotnet()` (`visual-relay:82-104`)** re-execs into `nix develop` when only dotnet
  is missing (`:95-96`) — but it does **not** set `VISUAL_RELAY_NIX_REENTRY` (it relies on
  dotnet being present inside the shell to avoid looping). Used by build/test/format/
  screenshot/init/run-task.

Supporting facts:
- Loop guard: `VISUAL_RELAY_NIX_REENTRY` (set on re-entry, checked at `:88,137`).
- Test seam: `_VISUAL_RELAY_FAKE_NO_NIX` makes `_find_nix` return nothing (`:31-33`);
  `_find_nix` (`:30-41`) probes PATH then Determinate/default profiles.
- No-nix path is graceful: `_offer_nix_install` (`:44-79`) offers Determinate Nix (consent
  gated), else clear per-tool errors. Don't regress this — nix stays *preferred*, not *required*.
- The existing re-exec already passes args intact and resolves the devshell correctly (see
  `llm-tasks/DONE-launcher-nix-reentry-drops-arguments.md`,
  `llm-tasks/DONE-launcher-dev-dispatch-cwd-independent.md`) — reuse that exec line as-is.
- Tests: `tests/VisualRelay.Tests/Installer5Bootstrap2LauncherTests.cs` runs the real
  launcher in a hermetic sandbox with stub `nix`/`nono`/`uv` on a crafted PATH; the `nix`
  stub logs argv to `/tmp/.vr-b2-nix-argv`; assertions check `develop --command bash <cmd>
  <args>` and use a `VISUAL_RELAY_NIX_REENTRY` marker for the loop guard.

## What to build

TDD — write the failing test first.

1. **Unconditional gate.** Add `_ensure_devshell()` (or repurpose the existing gate) that —
   when `VISUAL_RELAY_NIX_REENTRY` is unset and `_find_nix` succeeds — **always** re-execs
   into the devshell with the existing exec line (setting `VISUAL_RELAY_NIX_REENTRY=1`),
   regardless of whether any tool is missing. Invoke it once **before** the dispatch `case`
   so every subcommand (build/test/format/screenshot/launch/run/run-task/init/…) enters the
   devshell uniformly. Update the message to drop "missing required tools" — e.g.
   `visual-relay: entering nix develop for this command`.
2. **Preserve the escape hatches:**
   - **Loop guard:** the gate must set `VISUAL_RELAY_NIX_REENTRY=1` on exec so it runs at
     most once (also fixes the current `_require_dotnet` exec that omits the marker and could
     loop if the devshell lacked dotnet).
   - **No nix:** when `_find_nix` is empty (including under `_VISUAL_RELAY_FAKE_NO_NIX`), do
     **not** hard-fail — fall through to the existing missing-tool checks / `_offer_nix_install`
     so machines without nix still get the install offer and actionable per-tool errors.
   - **Published self-contained app:** recommended — skip the unconditional re-entry when
     `HAS_PUBLISHED` (the brew/self-contained install ships its own runtime and those users
     may not have nix), keeping the existing missing-tools fallback for nono/uv there. The
     always-enter behavior targets the source-checkout developer launcher. (Flag this choice
     in the commit body so it can be revisited if truly-always is wanted.)
3. The per-tool re-entry now inside `_missing_required_tools`/`_require_dotnet` becomes an
   in-devshell safety net: after re-entry the tools should exist; if the devshell itself
   lacks one, surface the existing per-tool error instead of re-entering again.

## Done when
- With nix available and not yet re-entered, **every** subcommand re-execs into `nix develop`
  exactly once — even when dotnet, nono, and uv are all present. Verified by a new
  `Installer5Bootstrap2`-style test that stubs all tools present and asserts the `nix` argv
  still contains `develop --command bash launch …` (this **fails** against today's
  missing-tools-only gate, then passes).
- Re-entry happens at most once (`VISUAL_RELAY_NIX_REENTRY` set) — the existing loop-guard
  test stays green; no infinite re-exec.
- Under `_VISUAL_RELAY_FAKE_NO_NIX` (no nix), the launcher does not attempt a devshell and
  still reaches the install offer / per-tool errors.
- The "missing required tools" wording is gone from the normal always-enter path; the message
  states it is entering the devshell.
- Published self-contained launches behave as before (if the `HAS_PUBLISHED` exemption is
  kept).
- `./visual-relay check` green; Conventional Commit subject (e.g. `feat(launch): always enter
  the nix devshell when nix is available`).
