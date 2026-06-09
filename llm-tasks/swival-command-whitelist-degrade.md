# Stop swival from hard-crashing on a missing command (don't ship optional tools in whitelists)

A stage flagged with `swival exit 1: Error: command 'tree' not found on PATH` (sandbox-2/3,
Research) purely because the `tree` binary wasn't installed. Investigated empirically
(subagent, 2026-06-09):

- ROOT CAUSE is swival's startup **command-whitelist preflight**: `resolve_commands`
  (`<swival>/agent.py:6693-6710`) runs `shutil.which(name)` for every command in a `--commands`
  whitelist and `raise ConfigError(f"command {name!r} not found on PATH")` on the first miss;
  that propagates through `AgentError` → `agent.py:6298-6314` → `sys.exit(1)`. It fires
  **before any LLM call**, so the model never gets to adapt. `tree` is NOT special — ANY missing
  whitelisted binary aborts the whole run (confirmed with a bogus name).
- CONTRAST: with `--commands all` there is **no** preflight; a missing command degrades to a
  recoverable tool-error returned to the model (`<swival>/tools.py:3002-3004`). So the crash only
  happens when swival is handed an explicit whitelist containing an absent tool.
- Classification: intentional-but-unfortunate swival design (fail-closed), not a narrow bug.

VR angle: current `RelayStages.cs:10` uses `--commands all` for Research (passed verbatim at
`ProcessRunners.cs:176`), and only small baseline whitelists for Ideate (`git,ls,cat`) / Commit
(`git`). **VERIFY/RECONCILE:** the failing report
(`.relay/sandbox-3-.../stage2-attempt1.report.json`) shows a 22-command whitelist incl. `tree`
with `sandbox.mode=builtin`, and drive-v6 freshly crashed on `tree` at Research in ~1s — so some
current path is still handing swival a `tree`-containing whitelist. Find it (grep stage defs +
`SwivalProfileSession` `DefaultToml` + profiles) before assuming `--commands all` everywhere.

## Goal
No VR stage should ever die because an *optional* external tool is absent. Don't depend on users
`brew install`-ing whatever swival reaches for.

## Approach (suggested)
- VR-side (primary): never put optional tools (`tree`, etc.) in a `--commands` whitelist. Either
  use `--commands all` for exploratory stages, or **intersect any whitelist with `PATH`** (drop
  names `which` can't find) in `BuildArguments` before passing `--commands` to swival.
- Add an installer/launcher **preflight** that checks swival's genuinely-required binaries —
  `nono` (`<swival>/sandbox_nono.py:118`) and `git` — and surfaces a clear message instead of a
  mid-run crash. (`tree` is NOT required by swival.) Ties into [[nono-grant-swival-workspace-writes]]
  and the installer arc.
- Test: a stage whose whitelist names a missing binary must still run (the binary is dropped, not
  fatal).
- Upstream (secondary): file a swival issue — "a single missing binary in a `--commands` whitelist
  aborts the whole run at startup; degrade missing entries (or add `--commands-strict` opt-in)
  instead of fatal." Minimal repro:
  `swival -q --provider generic --model dummy --base-url http://127.0.0.1:59999 --api-key x
   --base-dir /tmp/x --no-lifecycle --no-history --files some --commands "ls,git,not-a-real-bin"
   --max-turns 1 "hi"` → `Error: command 'not-a-real-bin' not found on PATH`, exit 1, before any
  LLM call. Keep the in-workspace-binary rejection (`agent.py:6712-6717`) fatal.
