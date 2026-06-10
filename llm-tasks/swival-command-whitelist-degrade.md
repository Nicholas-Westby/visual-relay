# Stop swival from hard-crashing on a missing command (don't ship optional tools in whitelists)

> **Amended 2026-06-09 (deep-dive complete; see `~/Dev/temp-swival/BUG.md`):** the
> VERIFY/RECONCILE below is resolved — the whitelist is **VR's own**:
> `RelayStages.cs:7` `ReadOnlyCommands` feeds stages 2–4 (Research/Diagnose/Plan) and its
> current revision (`git,ls,cat,grep,find,rg,head,tail,wc,sort,uniq,cut,tr,awk,sed`)
> contains Homebrew-only **`rg`** — the same crash re-armed for any stock Mac (the June-8
> revision carried `tree`; 21 of that list's 22 entries were stock macOS, `tree` was the
> lone exception). Swival's own default is `--commands all`; the fatal preflight persists
> upstream through 1.0.30. The upstream dossier is WRITTEN
> (`/Users/admin/Dev/temp-swival/BUG.md`, repro + source walk + warn-and-drop
> recommendation); W files the issue — this task's scope is the VR side only.
> **Acceptance:** with `tree` uninstalled (`brew uninstall tree`) and `rg` masked from
> PATH, stages 2–4 still run end-to-end; each dropped command is visible in run output
> (e.g. a `command_dropped` event), and an all-dropped whitelist fails with a clear
> pre-run message (never silently widening to `all`).

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

## Goal
No VR stage should ever die because an *optional* external tool is absent — on any machine,
for any target repo. Don't depend on users `brew install`-ing whatever a whitelist happens to
name; degrade visibly instead.

## Approach (suggested)
- VR-side (primary): **intersect every explicit whitelist with PATH** (drop names `which`
  can't resolve) in the swival argument-building path (`ProcessRunners`/`BuildLaunchTarget`)
  before passing `--commands` — mirroring the warn-and-drop semantics recommended upstream.
  Emit an event per dropped name. If the intersection is empty, fail the stage pre-spawn with
  a clear message (do NOT pass an empty list — swival treats empty as unrestricted).
- Audit `ReadOnlyCommands` itself: prefer stock-macOS/Linux-portable names (drop `rg`; `grep`
  is already present), but keep the intersect defense regardless — the list will drift again.
- Add an installer/launcher **preflight** that checks swival's genuinely-required binaries —
  `nono` (`<swival>/sandbox_nono.py:118`) and `git` — and surfaces a clear message instead of a
  mid-run crash. (`tree` is NOT required by swival.) Ties into [[nono-grant-swival-workspace-writes]]
  and the installer arc.
- Tests: a stage whose whitelist names a missing binary still runs (binary dropped + evented,
  not fatal); empty-after-intersection fails pre-spawn with the clear message; present-binary
  whitelists pass through byte-identical.
- Upstream (done): dossier at `/Users/admin/Dev/temp-swival/BUG.md` — warn-and-drop at startup,
  keep the in-workspace-binary rejection (`agent.py:6712-6717`) fatal, fatal-on-empty without
  falling through to the unrestricted branch (`agent.py:7324-7327`).
