# Own and self-heal the nono `vr-guard` profile at an XDG path

Visual Relay installs the nono `vr-guard` sandbox profile to `~/.config/nono/profiles/vr-guard.json`
**only if it is absent**, then invokes nono **by name** (`-p vr-guard`). Two consequences, both real
bugs: (1) once the file exists it is never refreshed, so a machine provisioned before the profile
grew its toolchain-cache grants keeps running sandboxed builds under a stale, over-restrictive
profile — SwiftPM/cargo/etc. writes get denied, and under headless nono a denied write stalls the
process until the test cap fires (observed: a `swift test` suite that completes in ~0.8s on the host
timed out at 600000ms under the sandbox); (2) the source it copies from is repo-relative
(`$SCRIPT_DIR/packaging/...`), which a brew / self-contained install does not have.

Fix the class of bug, not the instance: make Visual Relay **own** the profile at a VR-private path,
ship its content **in the assembly**, and **rewrite it on every run** so it cannot go stale. Load it
**by path** (`--profile <abs>`), not by name.

This design is decided — implement exactly this, no alternatives:
- Canonical content lives **embedded in `VisualRelay.Core`** (single source of truth; works whether
  launched from a checkout or an installed app).
- It is written to **`$XDG_CONFIG_HOME/visual-relay/vr-guard.json`** (default
  `$HOME/.config/visual-relay/vr-guard.json`) — beside VR's existing `.env`.
- Write policy is **overwrite-always**. VR owns this file; per-repo/extra access is the existing
  `sandboxExtraAllowPaths` seam, so there is nothing of the user's to preserve here.
- nono loads it via **`--profile <that-path>`**, replacing `-p vr-guard`.

## Current state (researched)

- **Invoked by name.** `ProcessRunners.cs:20` (`private const string NonoProfile = "vr-guard";`)
  assembled at `ProcessRunners.cs:86` (`new List<string> { "run", "-p", NonoProfile, "--allow-cwd" }`).
  The Verify test runner reuses this same prefix (`SwivalSubagentRunner.BuildNonoPrefix(config,
  rollback:false)` in `SandboxedTestRunner.cs`), so changing `BuildNonoPrefix` covers both agent
  stages and verification.
- **Installed write-if-absent by the launcher.** `visual-relay` `_provision_nono()` (`:375-402`)
  pulls the base pack `nono pull jedisct1/swival` (`:385`), then `cp`s
  `$SCRIPT_DIR/packaging/nono/vr-guard.json` → `${XDG_CONFIG_HOME:-$HOME/.config}/nono/profiles/vr-guard.json`
  **only if missing** (`:394-401`, comment "never clobber user edits"). This is the stale-forever
  bug; the source path is also repo-relative.
- **Pure nono policy file.** `extends: "swival"`, `filesystem.read/allow/write/deny`, `groups`,
  `rollback`, `unsafe_macos_seatbelt_rules`, `meta`. Only nono reads it — swival never does. It drifted
  ahead of installed copies on Jun 16–19 (commit `6d69e24` routed verification through nono; later
  commits added `$HOME/.swiftpm`, `$HOME/.cargo`, nix grants).
- **Embedding precedent to mirror.** `SwivalProfileSession.DefaultToml` ships a profile as an
  in-assembly constant, and the driver writes/pins it at run start —
  `ResolvePinnedSwivalProfileContentAsync` (`RelayDriver.VerifyFix.cs:187`). Put the new ensure call
  at that same run-start juncture so it runs once per run for **every** entry point (GUI Run All,
  headless `RunTask`, resume).
- **XDG resolution to reuse.** `KeyEnvFile` already resolves `$XDG_CONFIG_HOME/visual-relay/` with a
  HOME fallback and an injectable accessor for tests (`KeyEnvFile.cs:36-53`). Resolve the profile dir
  the same way so it lands beside `.env`.
- **Do not touch** the unrelated swival *model-tier* profile (`ProcessRunners.cs:54 "--profile", <tier>`;
  `SwivalProfileSession` / `swival.toml`). Different concept that happens to share the word "profile".
- **Existing tests in scope.** `NonoProfileStructureTests` and `VrGuardProfileRollbackTests` read
  `packaging/nono/vr-guard.json`; `Installer5Bootstrap2LauncherTests` drives the real launcher with
  stub `nix`/`nono`; an existing test asserts the `-p vr-guard` nono args.
- **Verified capability** (nono 0.61.1, via `nix develop --command nono`): `nono run --profile
  <abs-path> --allow-cwd -- …` loads the file *at that path* — confirmed with a unique marker grant; it
  did **not** fall back to the installed-by-name copy. `extends: "swival"` still resolves from the
  installed pack, there is no trust/promote prompt for a profile outside the profiles dir, and
  `--allow-cwd` composes (CLI grants tag `[user]`, profile grants `[profile]`).

## What to build

TDD — write the failing test first.

1. **Embed the profile (single source of truth).** Mark `packaging/nono/vr-guard.json` as an
   `<EmbeddedResource>` in `VisualRelay.Core.csproj` and read it from the assembly at runtime. Add a
   guard test asserting the embedded bytes equal the on-disk `packaging/nono/vr-guard.json`, so the
   file `NonoProfileStructureTests`/`VrGuardProfileRollbackTests` validate can never drift from what
   actually ships.
2. **`NonoProfileEnsurer` (new, in Core).**
   - `ResolveProfilePath(...)` → `$XDG_CONFIG_HOME/visual-relay/vr-guard.json` (default
     `$HOME/.config/visual-relay/vr-guard.json`), reusing `KeyEnvFile`'s XDG/HOME resolution and its
     injectable accessor.
   - `EnsureAsync(...)` → create the directory and **write the embedded profile, overwrite-always**
     (no if-absent, no ownership/marker check — VR owns this private path). Always make the file match
     the embedded content even if it was hand-edited; skip the actual write only when bytes already
     match (avoid mtime churn). If the dir can't be resolved or the write fails, throw an actionable
     error — the run must not proceed to a sandboxed stage with a missing/stale profile.
3. **Ensure at run start.** Call `NonoProfileEnsurer.EnsureAsync(...)` once per run at the shared
   driver run-start (next to `ResolvePinnedSwivalProfileContentAsync`), gated on `!config.BypassSandbox`.
   This single site is what makes the profile self-heal on the normal open → Run All flow, and it
   covers headless `RunTask` and resume too.
4. **Load by path.** Change `BuildNonoPrefix` to emit `"--profile", NonoProfileEnsurer.ResolveProfilePath(...)`
   instead of `"-p", "vr-guard"`. `--allow-cwd`, the `-a` `sandboxExtraAllowPaths` flags, and the
   rollback flags are unchanged.
5. **Retire the launcher copy.** In `_provision_nono` remove the write-if-absent `cp` block
   (`visual-relay:392-401`); **keep** `nono pull jedisct1/swival` (`:385`) — the profile still
   `extends: "swival"`. Do **not** delete any pre-existing `~/.config/nono/profiles/vr-guard.json`; it
   simply stops being referenced.

## Done when

- New `NonoProfileEnsurer` tests pass and **fail against today's code**: a pre-seeded *stale* file at
  the XDG path is overwritten to match the embedded profile; an absent file is created (dir made); an
  already-identical file is left untouched (no rewrite). XDG/HOME resolution is exercised through the
  injected accessor.
- Guard test: embedded `vr-guard.json` equals `packaging/nono/vr-guard.json` byte-for-byte.
- `BuildNonoPrefix` (and the Verify path via `SandboxedTestRunner`) now emits
  `--profile <…/visual-relay/vr-guard.json>` and no longer `-p vr-guard`; the existing arg-assertion
  test is updated accordingly.
- Launcher test shows `_provision_nono` no longer writes under `~/.config/nono/profiles/`, and still
  runs `nono pull jedisct1/swival` (update the `Installer5Bootstrap2`-style expectations).
- Integration/manual sanity (note it explicitly if not automated): with a deliberately stale
  `$XDG_CONFIG_HOME/visual-relay/vr-guard.json`, a run refreshes it and a sandboxed write to
  `~/.swiftpm` / `~/Library/org.swift.swiftpm` is permitted — the 600000ms verify stall no longer
  reproduces.
- `./visual-relay check` green. Conventional Commit subject e.g.
  `fix(sandbox): own and self-heal the vr-guard nono profile at an XDG path`. Flag in the commit body:
  the overwrite-always policy (VR owns the file; customize via `sandboxExtraAllowPaths`), and that the
  old global `~/.config/nono/profiles/vr-guard.json` is now unused but intentionally not deleted.
