# Make the launcher offer to install swival when it's missing, and check weekly for a swival upgrade — both consent-gated

The `visual-relay` launcher already verifies and (consent-gated) installs its prerequisites: it offers
to install **Determinate Nix** when tools are missing, and it **requires `nono`** (with install
instructions) before a sandboxed run. But it never checks the **swival binary** — so on a host where
swival isn't installed (it lives on the VM, not here), the launcher lets the app start and the run
fails mid-flight with the confusing sandbox error addressed by
`legible-run-failures-and-tool-preflight`.

Extend the launcher's existing consent-gated provisioning to swival, in two phases:
1. **Install-on-missing:** at launch preflight, detect a missing swival binary and offer a
   consent-gated install (mirroring the existing `nono` / Nix offers).
2. **Weekly upgrade:** once swival is present, at most once every 7 days look up the latest swival and
   offer a consent-gated upgrade.

Both honor the project's consent rule — **explicit `y/N`, yes-only**; in non-interactive contexts print
instructions instead of acting (see [[consent-for-global-installs]]).

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; if a snippet has drifted, re-read the file and adapt.

All of this is in the repo-root `visual-relay` bash launcher.

**The consent-gated install template** — `_offer_nix_install` (this is the pattern to mirror for
swival): it no-ops if nix is found, refuses to re-ask within one invocation, prints manual instructions
and returns 1 when **not** attached to a TTY, and otherwise prompts and runs an **overridable**
installer:

```bash
if [[ ! -t 0 || ! -t 1 ]]; then
  printf 'visual-relay: install Determinate Nix to provide the missing tools:\n  curl -fsSL https://install.determinate.systems/nix | sh -s -- install\n' >&2
  return 1
fi
printf 'visual-relay: required tools missing. Install Determinate Nix to provide them? [y/N] ' >&2
IFS= read -r answer
case "$answer" in
  [yY]|[yY][eE][sS])
    eval "${VISUAL_RELAY_NIX_INSTALLER:-curl -fsSL https://install.determinate.systems/nix | sh -s -- install}"
    …
```

Note the `VISUAL_RELAY_NIX_INSTALLER` override — that's how tests drive the install path without a real
install. Add an analogous `VISUAL_RELAY_SWIVAL_INSTALLER`.

**The per-tool hard gate template** — `_require_nono`: present ⇒ return; else offer nix, print
`brew install nono` instructions, `exit 127`. Mirror this as `_require_swival`.

**The preflight that lists required tools** — `_missing_required_tools [check_uv]`:

```bash
# dotnet (hard when not published)
if (( ! HAS_PUBLISHED )) && ! command -v dotnet >/dev/null 2>&1; then missing_any=1; fi
# nono (hard when sandbox enabled)
if ! _read_bypass_sandbox && ! command -v nono >/dev/null 2>&1; then missing_any=1; fi
# uv (soft — missing alone never blocks; backend degrades gracefully)
if (( check_uv )) && ! command -v uv >/dev/null 2>&1; then missing_any=1; fi
```

**`swival` is absent from this list.** When something is missing and nix is available, this function
re-execs once into `nix develop` (so devshell tools become present) — see the caveat below about swival
not being in the flake.

**Sandbox gating** — `_read_bypass_sandbox` reads `bypassSandbox` from `.relay/config.json`. nono is
gated on it; **swival is not** — swival is the agent that runs every stage (with sandbox bypassed,
`BuildLaunchTarget` launches `swival` directly), so swival is required **unconditionally**.

**Don't conflate two "swival" things.** `_provision_nono` runs `nono pull jedisct1/swival` — that pulls
the **nono profile pack** named swival (the sandbox profile `vr-guard` extends), **not** the swival
binary. This task installs the **binary**; leave the pack handling alone.

**swival ships via its own Homebrew tap, not nix.** Per **swival.dev**, the macOS install is
`brew trust swival/tap && brew install swival/tap/swival`. swival is **not** in `flake.nix` devshell
`packages` (`dotnet-sdk_10 … nono uv python313`) and has no nixpkgs attr — so re-entering `nix develop`
does *not* supply swival (unlike dotnet/nono). The supported channel is the Homebrew tap (the project is
otherwise nix-first, but this specific tool only ships via brew).

**Dispatch** — `launch|run` runs `_missing_required_tools 1`, then (when not bypassed) `_require_nono`
+ `_provision_nono`, then starts the backend, then runs the app. `run-task` runs `_missing_required_tools`
+ `_require_nono`. swival should be gated in these same arms.

**Test harness** — `tests/VisualRelay.Tests/Installer5Bootstrap2LauncherTests.cs` and
`…Installer5Bootstrap3LauncherTests.cs` copy the launcher into a temp dir, build a **fake PATH** of stub
binaries (`PATH="$S:/usr/bin:/bin"`), run `bash "$T/visual-relay" launch …` with env overrides
(`VISUAL_RELAY_NIX_REENTRY`, `_VISUAL_RELAY_FAKE_NO_NIX`, etc.), and assert on output/exit. Extend this
family for swival.

**Per-machine state** — the launcher already uses `${XDG_CONFIG_HOME:-$HOME/.config}` (in
`_provision_nono`). The weekly-check timestamp must be **per-machine and NOT in the repo tree** (the repo
is shared host↔VM — see [[repo-shared-with-vm]]). Use an XDG state path, e.g.
`${XDG_STATE_HOME:-$HOME/.local/state}/visual-relay/swival-upgrade-check` (matching `~/.config/visual-relay`
like `KeyEnvFile` is also acceptable — implementer's call; the test is the arbiter).

## What to build

The two phases can land as **two commits** if the single-commit size gate (changed files < 300 lines)
demands; **Phase 1 must land first** (Phase 2 reuses its install/upgrade command + overridable var).

### Phase 1 — Offer to install swival when missing (consent-gated)
- Gate swival in the launch/run preflight so a missing swival is caught **before the app starts**
  (mirror how `nono` is gated). swival is a **hard, always-required** tool (not sandbox-gated).
- Add `_require_swival` (mirror `_require_nono`): `command -v swival` present ⇒ return; else call a
  consent-gated `_offer_swival_install`, and on decline / non-TTY print clear manual instructions and
  `exit 127`.
- Add `_offer_swival_install` (mirror `_offer_nix_install`): TTY ⇒ `[y/N]`, yes-only; on yes run the
  install command (default overridable via `VISUAL_RELAY_SWIVAL_INSTALLER`), then re-check `command -v
  swival`; non-TTY ⇒ print instructions and return non-zero. Don't re-ask within one invocation.

**Install command (confirmed channel).** swival is distributed via a **Homebrew tap**; per swival.dev
the macOS install is:

```
brew trust swival/tap && brew install swival/tap/swival
```

- Default `_offer_swival_install` to that command, **overridable** via `VISUAL_RELAY_SWIVAL_INSTALLER`
  (mirrors `VISUAL_RELAY_NIX_INSTALLER`).
- **`brew` is a prerequisite of this path.** If `command -v brew` fails, don't guess — print guidance
  ("Homebrew is required to install swival — https://brew.sh, then `brew install swival/tap/swival`")
  and take the decline path. Do **not** auto-install Homebrew.
- **Platform:** the brew line is macOS-specific (the user's host). On non-macOS, print "see
  https://swival.dev for install instructions" rather than running it.
- **Freshness:** confirm the exact command against the live swival.dev page at implement time (the tap
  path or `brew trust` step may change).

### Phase 2 — Weekly upgrade check (consent-gated)
After swival is confirmed present, on `launch`/`run`:
- Read a last-checked timestamp from the per-machine XDG path. If `now - last_check < 7 days`, do
  nothing. Otherwise run the check (below) and **always rewrite the timestamp afterward** (so a declined
  upgrade doesn't re-nag until next week).
- **Check via brew** (channel-consistent, since we installed via brew). A weekly `brew update --quiet`
  then `brew outdated swival/tap/swival` (empty ⇒ current) is acceptable at this cadence; if a full
  `brew update` is too heavy, `brew livecheck swival/tap/swival` or the GitHub releases API for
  `Swival/swival` are lighter "latest" probes. Make the probe **overridable** for tests (e.g.
  `VISUAL_RELAY_SWIVAL_LATEST_CMD`). Read the installed version via `swival --version` (or
  `brew list --versions swival/tap/swival`) for the prompt text.
- If an upgrade is available: `printf 'visual-relay: a newer swival is available (you have <installed>). Upgrade now? [y/N] '`;
  on yes run `brew upgrade swival/tap/swival` (**overridable** via `VISUAL_RELAY_SWIVAL_UPGRADER`);
  non-TTY ⇒ print "upgrade available; run: brew upgrade swival/tap/swival".
- **Never block launch on an available upgrade** (suggestion only — contrast Phase 1, where *missing*
  swival blocks). Make the whole check **best-effort and non-fatal**: a network/parse failure must not
  break launch (mirror `_provision_nono`'s `|| echo …` style), and the timestamp still updates.

## Tests / verification
Extend the launcher test family (`Installer5Bootstrap2LauncherTests.cs` / `…Bootstrap3…`): cp launcher
to a temp dir, stub PATH, run `bash visual-relay launch`, assert. Add:
- **Present:** a `swival` stub on PATH ⇒ no install offer; launch proceeds (no new exit/error).
- **Missing + non-TTY** (the C# harness pipes, so stdin isn't a TTY): prints swival **install
  instructions**, does **not** run the installer, exits non-zero — mirror the existing nono/nix non-TTY
  assertions.
- **Installer override:** with `VISUAL_RELAY_SWIVAL_INSTALLER` set to a sentinel and the "yes" path
  exercised the way the suite drives `VISUAL_RELAY_NIX_INSTALLER`, assert the override command ran.
- **Weekly check — fresh vs stale:** with a temp XDG dir and an overridable "latest" source, a
  **fresh** timestamp ⇒ no check; a **>7-day-old** timestamp ⇒ check runs and the timestamp is
  rewritten.
- **Upgrade available + non-TTY:** newer "latest" ⇒ prints "upgrade available", does **not** upgrade,
  launch still proceeds.
- **Non-fatal:** a failing/empty "latest" lookup ⇒ launch still proceeds and the timestamp still updates.
- `./visual-relay check` green (the launcher tests run under it).

## Done when
- On a host without swival, `./visual-relay launch` in a terminal detects it and offers a consent-gated
  install; declining prints clear instructions; non-interactive prints instructions without acting.
- After install, at most once a week the launcher offers a consent-gated swival upgrade; declining (or
  non-interactive) never blocks and doesn't re-nag until the next week.
- Install/upgrade use swival's official Homebrew tap (`swival/tap`), overridable via env; a missing
  `brew` yields guidance, not a crash.
- New launcher tests cover present / missing / override / fresh-vs-stale-timestamp / non-fatal paths;
  `./visual-relay check` green.
- Conventional Commit subject(s), e.g. `feat(launcher): offer to install swival when missing` and
  `feat(launcher): weekly consent-gated swival upgrade check`.

## Decisions (settled)
1. **Lives in the `visual-relay` launcher**, extending its existing consent-gated provisioning
   (`_offer_nix_install` / `_require_nono` / `_missing_required_tools`) — not a new subsystem. *Why:*
   that's where Nix/nono provisioning and the `[y/N]` consent idiom already live, and the user asked for
   "the launcher."
2. **swival is a hard, always-required tool** (unlike nono, which is sandbox-gated). Missing swival
   **blocks** launch with an install offer; an outdated swival only **suggests** an upgrade.
3. **Consent: explicit y/N, yes-only; non-interactive ⇒ print instructions, don't act; commands
   overridable via env** (for tests and alternate channels). *Why:* [[consent-for-global-installs]].
4. **Install/upgrade via swival's official Homebrew tap** (`brew install swival/tap/swival` /
   `brew upgrade swival/tap/swival`), commands overridable via env. *Why:* swival.dev publishes only a
   brew tap (no nixpkgs attr), so brew is the supported channel even though the project is otherwise
   nix-first ([[nix-first-determinate]]). `brew` is thus a prerequisite of the install path; if it's
   absent, print guidance rather than auto-installing it.
5. **Weekly cadence via a per-machine XDG timestamp** (never the repo tree — shared host/VM). Always
   refresh the timestamp after a check; an upgrade never blocks. *Why:* [[repo-shared-with-vm]].
6. **The `jedisct1/swival` nono pack ≠ the swival binary.** `_provision_nono` already handles the pack;
   this task installs the binary and must not disturb the pack logic.

## Notes
- **Coordination with `legible-run-failures-and-tool-preflight`:** that task makes the *GUI* fail
  legibly if swival is still missing at run time (e.g. a desktop/published launch with no TTY, where the
  launcher can't prompt); this task prevents the situation up front for terminal launches. Complementary
  — land either order; this one assumes the GUI legible fallback still exists.
- **GUI install button** (offering install via the in-app `ShowConfirmationAsync` dialog — wired in
  `App.axaml.cs`, used today by attachment-remove — for non-TTY/desktop launches) is a reasonable
  **follow-up but out of scope here.** The user asked for the launcher; keep scope bounded.
- **Install channel (confirmed):** swival.dev publishes a Homebrew tap — macOS install
  `brew trust swival/tap && brew install swival/tap/swival`, upgrade `brew upgrade swival/tap/swival`.
  Verify against the live page at implement time; for non-macOS, defer to swival.dev.
