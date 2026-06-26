# Make Visual Relay run on Windows: native toolchain, a cross-platform `visual-relay` entry point, and a Windows-aware execution layer

Visual Relay's UI (Avalonia 12) is already cross-platform and `VisualRelay.App.csproj`
builds `WinExe` with an `app.manifest` and `.ico` — so the *window* will render on
Windows almost for free. The blockers are entirely in the **mechanics around** the
UI: the launcher is bash + Nix, several startup paths call POSIX-only APIs
unconditionally, config/data directories resolve through XDG/`HOME` (unset on
Windows), and the two hard external dependencies (`nono` sandbox, `swival` agent)
have no Windows story. This task ports the mechanics so the GUI runs and is fully
usable for inspection on Windows, and lays a clean path to running tasks there.

This is a multi-phase epic in the spirit of the `bootstrap-1/2/3` series. Each
phase below is a self-contained, independently shippable task with its own
tests and "Done when". Ship them in order; Phase 0 + Phase 1 alone deliver "the
GUI opens and is usable on Windows", which is the bulk of the user-visible win.

## Current state (researched — exact references)

The launch path is bash → Nix → C# CLI → Avalonia app, and it hard-stops on
Windows in several places **before** the window ever opens:

- **Launcher is bash + Nix.** `visual-relay` (`visual-relay:1` `#!/usr/bin/env bash`,
  logic in `main()` at `:18-43`) resolves its dir (`:20`), enters the Nix devshell
  unconditionally (`_ensure_devshell`, `:34-37`) which `exec`s
  `nix develop --command bash "$0" …`, then `exec dotnet run --project
  tools/VisualRelay.Cli …` (`:41`). `cmd.exe`/PowerShell honor neither the shebang
  nor `nix develop`. Nix has **no native Windows support** (WSL2 only).
- **The Nix flake has no Windows system.** `flake.nix` `systems = [ "aarch64-darwin"
  "x86_64-darwin" "x86_64-linux" "aarch64-linux" ]` and provisions
  `dotnet-sdk_10 git bash icu imagemagick openssl zlib nono uv python313`. `nono`
  (Seatbelt/Landlock) cannot exist on Windows.
- **`launch` hard-fails on the nono gate at exit 127.** `LaunchCommand.Run`
  (`tools/VisualRelay.Cli/Commands/LaunchCommand.cs:15-18`) calls
  `NonoGate.Require` first; `NonoGate.cs:18-37` returns **127** when `nono` is not
  on PATH (it never will be on Windows), printing brew/Nix install hints. Launch
  dies here before the app starts.
- **`launch` then hard-fails on the swival gate at exit 127.**
  `LaunchCommand.cs:20-22` → `SwivalGate.Require` (`SwivalGate.cs:16-35`) returns
  **127** when `swival` is not on PATH; its install path is a macOS Homebrew tap
  (`brew trust swival/tap && brew install swival/tap/swival`).
- **PATH probing misses Windows executables.** `ProcessLauncher.OnPath`
  (`tools/VisualRelay.Cli/ProcessLauncher.cs:69-81`) does
  `Path.Combine(dir, name)` with **no PATHEXT** handling, so even a present
  `nono.exe`/`swival.exe`/`git.exe` would not be found (`IsExecutable` returns
  `true` on Windows at `:83-96`, but the bare-name `File.Exists` check fails).
- **`BackendProcess` P/Invokes `libc` unconditionally.** `BackendProcess.cs:18-19`
  `[DllImport("libc")] kill(int,int)`, used by `IsAlive`/`SendTerm`/`SendKill`
  (`:30-43`) and `ReadLivePid` (`:52-69`). On Windows this throws
  `DllNotFoundException`. The launch path reaches it via the best-effort backend
  start (`LaunchCommand.cs:31-34`).
- **Config/data dirs resolve through XDG/`HOME`, which is unset on Windows.**
  `XdgConfig.ResolveConfigDir` (`XdgConfig.cs:15-23`) throws "Cannot resolve config
  directory" when neither `XDG_CONFIG_HOME` nor `HOME` is set; Windows sets
  `USERPROFILE`/`APPDATA`/`LOCALAPPDATA`, not those. This breaks `KeyEnvFile`
  (`KeyEnvFile.cs:66-77`), `BackendPaths.Resolve` (`BackendPaths.cs:48-67`),
  `UiStateStore`, and `RelayConfigLoader` — i.e. the GUI cannot load or persist UI
  state or settings.
- **POSIX-only shell-outs in the execution layer** (reached when running tasks /
  the backend, not at GUI open): `/bin/sh` proxy spawn in
  `BackendLifecycle.Start.cs` (`exec litellm …`); `/bin/sh -lc "command -v git"`
  and `/usr/bin/git`/`xcrun` in `GitInvoker.cs`; `/bin/ps -axo …` in
  `ProcessTreeCpuSampler.cs`; `/bin/sh` test wrapping in `SandboxedTestRunner.cs`
  and `ShellTestRunner.cs`; POSIX venv layout `bin/python`/`bin/litellm` in
  `BackendPaths.cs:36-37` (Windows uses `Scripts\python.exe`); `setpgid`/`kill`
  process-group teardown in `ProcessCapture.cs` (guarded for Linux/macOS with a
  `Process.Kill(entireProcessTree: true)` fallback — verify the fallback path).
- **CI/release is macOS-only.** `.github/workflows/release.yml` `runs-on:
  macos-latest`, `matrix.rid: [osx-arm64, osx-x64]`, `codesign`, and a
  `build-app-bundle` step. No `win-x64`.

Good news — patterns to copy: `FileReveal.cs` already dispatches
`explorer.exe`/`open`/`xdg-open` by OS; many file-mode calls are already guarded
(`KeyEnvFile.cs:154-178`, `HookInstaller.cs`, `NonoProfileEnsurer.cs`,
`MainWindowViewModel.Settings.cs`); `MacDockIcon.cs` already no-ops off macOS. The
architecture has the right seams (`ISubagentRunner`, `ITestRunner`, `IGitInvoker`)
to slot Windows behavior behind without touching Domain or the UI.

## Key decisions (the "best approach" — bake these in)

These answer the three mechanics questions up front so the executor doesn't
re-litigate them.

### 1. Nix does **not** run on Windows — use a native, consent-gated bootstrap instead

Nix is unavailable on native Windows (WSL2 only, which we treat as a non-goal —
see Notes). Do **not** try to make the flake target a Windows system. Instead the
Windows launcher provisions the same toolchain natively, mirroring the Nix path's
philosophy (zero surprise global installs; one consent prompt; reuse on later
runs):

- **.NET 10 SDK** — detect an existing `dotnet` with an SDK ≥ 10; if absent, offer
  to run Microsoft's official `dotnet-install.ps1`
  (`irm https://dot.net/v1/dotnet-install.ps1`) with `-Channel 10.0
  -InstallDir %LOCALAPPDATA%\visual-relay\dotnet`, then prepend that dir to PATH
  for the session only. (winget `Microsoft.DotNet.SDK.10` is an acceptable
  alternative when winget is present.)
- **uv + Python 3.13** — the backend already uses uv. Offer
  `irm https://astral.sh/uv/install.ps1 | iex` (or `winget install astral-sh.uv`)
  when `uv` is absent; uv then supplies Python (`uv python install 3.13`), exactly
  as the venv layer expects. uv is fully native on Windows.
- **git** — required; if missing, print `winget install Git.Git` and stop.
- **swival** — runs on Windows (pure Python, PyPI classifier "OS Independent",
  Python ≥ 3.13). Install cross-platform with `uv tool install swival` — **not** the
  macOS `brew` tap. Since uv is already provisioned above, swival provisioning is
  free on Windows. (Provisioned in Phase 3, which is where it's needed.)
- **nono** — Linux/macOS only; not used on Windows. The Windows sandbox story uses
  swival's *own* `--sandbox` backends instead — see decision 3.

The Nix flake and the bash launcher stay exactly as they are for macOS/Linux.

### 2. One `visual-relay` entry point across platforms = sibling launchers resolved by the OS

A single file named `visual-relay` with a bash shebang cannot be invoked on
Windows (`cmd`/PowerShell ignore shebangs; you cannot `./visual-relay` in `cmd`).
A bash/batch "polyglot" single file is fragile and can't simultaneously have the
no-extension form Unix wants and the `.cmd` extension Windows needs. The standard,
robust solution (this is exactly the `gradlew`/`gradlew.bat`, `mvnw`/`mvnw.cmd`
pattern) is **sibling launchers with the same stem**, and the user types the same
token on every platform:

- `visual-relay` — the existing bash launcher (Unix), unchanged in spirit.
- `visual-relay.cmd` — a thin Windows shim that just invokes the PowerShell logic:
  `@echo off` … `powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0visual-relay.ps1" %*`.
- `visual-relay.ps1` — the real Windows bootstrap (decision 1), ending in
  `dotnet run --project tools\VisualRelay.Cli\VisualRelay.Cli.csproj -- <cmd> <args>`.

On Windows the user runs `.\visual-relay <cmd>` (PowerShell resolves `.cmd` via
PATHEXT) or `visual-relay <cmd>` (cmd.exe / when on PATH). On Unix, `./visual-relay
<cmd>` as today. **All per-command logic stays in the cross-platform C# CLI**
(`tools/VisualRelay.Cli`) — the launchers only do the pre-.NET bootstrap, so the
Windows-specific shell surface is tiny and the two launchers converge on the same
`dotnet run` line. Keep the `.ps1` as small as the bash one; do not grow command
logic into it.

### 3. Sandboxing swival on Windows: wrap it externally (the nono-analogue) or use swival's own backend — several options, one seam

This was the big unknown; it's now researched (sources in Notes). There are two
families — **(A)** wrap swival in an external sandbox launcher *exactly as VR wraps
it in `nono run … -- swival` on Unix* (this is what was asked), or **(B)** let swival
self-sandbox via its own `--sandbox` flag. Both reduce to the same code seam (below).
Key facts first:

- **swival has its own sandbox abstraction with three backends** —
  `--sandbox builtin|agentfs|nono`. VR currently does **not** use it: it wraps
  swival in `nono run … -- swival …` itself, deliberately, "to pin the exact profile
  and invocation and avoid swival<->nono version skew"
  (`ProcessRunners.cs:76`). That wrapper is the only Windows blocker for the
  sandbox — swival itself is happy to drive its own backend.
- **`nono`** ([always-further/nono](https://github.com/always-further/nono)) is
  native on Linux (Landlock) / macOS (Seatbelt) only. Its **official Windows story
  is WSL2** (issue #463: "support running under WSL2 … rather than attempting a
  native Windows sandbox") — and C#/Python/TS bindings are on its roadmap. So nono
  *can* wrap swival on Windows, but only inside WSL2.
- **`builtin`** (swival's default) is **cross-platform** but only applies
  app-layer path guards to swival's *own file tools*; **a shell command is an
  unguarded write/delete escape** (this is the exact gap that made VR adopt nono —
  see `DONE-sandbox-1-…md:6-13`). So `builtin` alone does **not** meet VR's
  accident-containment bar.
- **`agentfs`** ([tursodatabase/agentfs](https://github.com/tursodatabase/agentfs))
  is a **copy-on-write overlay** (SQLite-backed delta) that re-execs swival with
  the workspace mounted writable and everything else read-only — purpose-built to
  be **cross-platform** where Linux/macOS lack a common CoW primitive. Turso ships
  a Windows PowerShell installer + Windows release binaries, **but** the authoritative
  `MANUAL.md` documents mounts only as FUSE (Linux) / NFS (macOS) and marks
  `agentfs exec` — the very call swival's backend needs — **"Unix only."** So
  agentfs on Windows is **not production-ready today** (BETA; no documented
  WinFsp/ProjFS mount). It is the right *strategic* bet, not a today answer.

**The unifying insight — it's the same seam.** VR already builds each launch as a
*wrapper prefix* + `swival` + swival args (`BuildLaunchTarget`/`BuildNonoPrefix`,
`ProcessRunners.cs`); on Unix the prefix is `nono run --profile vr-guard --allow-cwd
--rollback --no-rollback-prompt --`. Sandboxing swival on Windows is the **same seam
with a different prefix** (or none). So every option below is a small, additive
prefix-builder selected by OS/config — not a rearchitecture. The GUI + inspection
features (Phases 0–1) never invoke this seam and need no sandbox at all.

**Family A — wrap swival externally (the direct nono-analogue that was asked for).**
Best-fit first:

1. **nono inside WSL2** — the most faithful: the *same* nono, the *same* `vr-guard`
   profile, kernel-enforced (Landlock). WSL2 is nono's own official Windows story, so
   this is "wrap with nono on Windows" almost literally. Prefix becomes
   `wsl -d <distro> nono run --profile … --`; the GUI stays native Windows and
   execution shells into WSL2. Cost: a WSL2 dependency, and the workspace must live
   Linux-side (or `/mnt/c`, with perf/permission caveats).
2. **Microsoft Execution Containers (MXC)** ([microsoft/mxc](https://github.com/microsoft/mxc),
   **MIT**) — **the chosen Windows sandbox (usable today).** A cross-platform
   "sandboxed code execution system for running untrusted code (model output,
   plugins, tools)" whose JSON policy is `readonlyPaths` / `readwritePaths` +
   `network.allowOutbound` — *exactly* nono's broad-read / confined-write model, and
   the *same* "one schema, OS-appropriate backend" design VR already uses (Windows
   `processcontainer`, Linux `bubblewrap`, macOS `seatbelt`). Ships now (signed
   release binaries + `@microsoft/mxc-sdk`); CLI is `wxc-exec.exe config.json`;
   `processcontainer` is the **default and only *stable* Windows backend** and
   enforces `readwritePaths`/`readonlyPaths` today. Why it fits VR's
   *accident-containment* model despite being early-preview:
   - **The Windows gaps don't bite VR.** Only `deniedPaths` and **outbound-network
     filtering** are unenforced on Windows today — and VR's nono profile *already
     leaves network open on purpose* (swival must reach the LiteLLM proxy +
     providers), so the network gap is a non-issue; write-confinement (the dimension
     VR cares about) is the enforced one.
   - **VR hand-authors its policy**, sidestepping the "SDK-generated policies can be
     overly permissive" disclaimer — same as it owns the `vr-guard` nono profile.
   - **Caveats to respect:** "no MXC profiles should be treated as security boundaries
     currently" (fine for accident-containment, not for an adversarial agent — state
     it); schema is `0.x-alpha` (pin a version); no C# SDK (shell out to `wxc-exec.exe`
     like VR does for nono); and **no source observed proves write-blocking on Windows
     — Phase 3 must empirically verify it** (Phase-3 step zero, below).
3. **Sandboxie-Plus** ([sandboxie-plus](https://github.com/sandboxie-plus/Sandboxie),
   **GPLv3**) — turnkey and mature (active 2026 releases): `Start.exe /box:<name>
   <cmd>` runs swival with kernel write-interception that redirects writes into a
   per-box copy-on-write store; host reads are allowed. Caveats: GPLv3 (distribution
   implications if bundled), a kernel driver install, and a *copy-back* model (writes
   land in the box, so VR must extract changes before its commit gate — the same
   materialize step agentfs needs).
4. **Custom native wrapper** — build VR's own, using OpenAI Codex's documented Windows
   sandbox as the blueprint: a restricted-token child whose sandbox user has no write
   access by default, with write-allow ACEs stamped on the workspace (**in-place
   writes, like nono — no copy-back**), explicit deny-write ACEs on `.git`, reads
   unrestricted, and per-user Windows Firewall rules for network. Closest to nono's
   in-place semantics with full control, but real cost: needs elevation/UAC for usable
   latency and breaks under some enterprise GPO ("Log on locally" stripped → error
   1385), AV interception of `CreateProcessAsUserW`, and UNC/junction/symlink path
   edge cases. (AppContainer + `SetAppContainerACL` is the lower-level variant.)

**Family B — let swival self-sandbox (no external wrapper).** `--sandbox builtin`
(cross-platform now, but shell is an unguarded escape → degraded) or `--sandbox
agentfs` (the cross-platform overlay; its Windows mount is not shipped yet — see the
facts above). Simpler (swival drives it), but weaker today.

**Decision (made): MXC is the Windows sandbox** — it's the closest native analogue to
nono (same confined-write/broad-read policy model, same "one schema, OS-appropriate
backend" shape, no WSL/VM dependency) and is usable today for VR's accident-containment
model per the analysis above. Implement it as the Windows arm of the prefix-seam
(Phase 3). **Keep `nono-in-WSL2` documented as the stronger, kernel-enforced opt-in**
for users who want it (it reuses the exact `vr-guard` profile), and a **Codex-style
custom native wrapper** as the fallback if MXC's preview status regresses or in-place
writes become a hard requirement; `agentfs`/`builtin` remain swival-native
alternatives. Never run fully unsandboxed silently: if MXC is unavailable and no opt-in
is set, **block** execution with an accurate message and disable the run controls.

> **Remaining owner sign-off (small):** confirm acceptance of MXC's early-preview
> caveat — it is *accident containment, not a hardened security boundary* (the same
> bar VR already accepts for nono's `vr-guard`), gated on the Phase-3 empirical
> write-block test passing on the `vr-windows` host. If the owner instead wants a
> kernel-enforced boundary on day one, switch the default to `nono-in-WSL2`. Phases
> 0–2, 4 (GUI, launcher, backend lifecycle, CI) do **not** depend on this.

---

## Phase 0 — The GUI opens and is usable on Windows (no sandbox, no Nix)

**Goal:** On a Windows machine with a .NET 10 SDK already installed, `dotnet run
--project tools\VisualRelay.Cli\VisualRelay.Cli.csproj -- launch` builds, the
Avalonia window opens, and inspection features (root pick, queue, logs/traces
view, settings read/write) work without throwing. No `nono`, no `swival`, no Nix.

**Approach:**

- **Make the launch gates platform-aware** without weakening macOS/Linux:
  - `NonoGate.Require` (`NonoGate.cs:18-37`): on Windows, do not return 127. The
    sandbox is unavailable; return 0 so the GUI can open. Task *execution* is
    governed separately in Phase 3 — Phase 0 only needs the window. Add a one-line
    stderr note on Windows ("OS sandbox unavailable on Windows; inspection only").
  - `SwivalGate.Require` (`SwivalGate.cs:16-35`): on Windows, do not hard-fail
    GUI launch on a missing `swival`; downgrade to a soft warning (swival is only
    needed to *run* stages, which Phase 3 gates). Drop the brew-tap assumption in
    the Windows message.
- **Fix Windows PATH probing.** `ProcessLauncher.OnPath`
  (`ProcessLauncher.cs:69-81`): on Windows, try each PATHEXT extension
  (`.COM;.EXE;.BAT;.CMD;…` from `%PATHEXT%`) when probing, so `git.exe`/`nono.exe`/
  `swival.exe` resolve. Keep the Unix exec-bit path as-is.
- **Guard the libc P/Invoke.** `BackendProcess.cs`: add an `OperatingSystem.IsWindows()`
  branch so `IsAlive`/`SendTerm`/`SendKill` never call `libc kill` on Windows. Use
  `Process.GetProcessById(pid)` + `HasExited` for liveness (catch
  `ArgumentException` → not alive); `Process.Kill()` / `Process.Kill(entireProcessTree:
  true)` for term/kill. `ReadLivePid`'s injectable `isAlive` seam (`:52`) already
  makes this unit-testable.
- **Resolve config/data dirs on Windows.** Add a Windows branch to the resolver
  so the GUI can read/write state:
  - `XdgConfig.ResolveConfigDir(xdgConfigHome, home)` (`XdgConfig.cs:15-23`): when
    on Windows and neither XDG nor `HOME` is set, fall back to
    `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)`
    (`%APPDATA%`). Keep XDG/`HOME` taking precedence when explicitly set (preserves
    test injection and power-user overrides).
  - `BackendPaths.Combine(xdgDataHome, home)` (`BackendPaths.cs:55-67`): same idea
    with `SpecialFolder.LocalApplicationData` (`%LOCALAPPDATA%`) as the Windows
    fallback. Keep the byte-for-byte XDG layout when those vars are set.
- **Skip backend autostart on Windows in this phase.** In `LaunchCommand.cs:31-34`,
  guard the best-effort backend start so it is a no-op on Windows for now (Phase 2
  makes it real). It is already best-effort, but the underlying `/bin/sh` spawn
  would throw `Win32Exception`; skipping keeps Phase 0 about the window only.

**Files:**

- `tools/VisualRelay.Cli/Gates/NonoGate.cs`, `tools/VisualRelay.Cli/Gates/SwivalGate.cs`
- `tools/VisualRelay.Cli/ProcessLauncher.cs`
- `tools/VisualRelay.Cli/Commands/LaunchCommand.cs`
- `src/VisualRelay.Core/Execution/BackendProcess.cs`
- `src/VisualRelay.Core/Configuration/XdgConfig.cs`
- `src/VisualRelay.Core/Execution/BackendPaths.cs`
- `tests/VisualRelay.Tests/` (new cross-platform unit tests; these run on the
  macOS/Linux CI too, exercising the Windows branches via the env/`isAlive` seams).

**Tests (write the failing tests first):**

- `XdgConfig.ResolveConfigDir(null, null)` — assert it throws today on non-Windows
  (unchanged) but, when `OperatingSystem.IsWindows()`, returns `%APPDATA%`. Drive
  the Windows branch with the explicit-overrides overload so it is testable on any
  OS: e.g. a new internal overload taking an injected "is-windows + appdata"
  resolver, asserted to return the AppData path when XDG/HOME are empty.
- `BackendPaths` — same: empty XDG_DATA_HOME + empty HOME resolves to the
  LocalAppData-based path on the Windows branch (injected), still
  `…/visual-relay/{backend-venv,scratch}`.
- `BackendProcess.ReadLivePid` — with the injected `isAlive` already covered; add a
  test that the production `IsAlive` on Windows path uses `Process`-based liveness
  (factor the OS dispatch behind a tiny seam so it is assertable without spawning).
- `ProcessLauncher.OnPath` — on the Windows branch (factor PATHEXT resolution into
  a pure helper `ResolveOnPath(pathEnv, pathext, name, fileExists)` and unit-test
  it): `name="git"`, dir contains `git.exe`, PATHEXT contains `.EXE` → found;
  without PATHEXT handling → not found (the failing case today).
- `NonoGate`/`SwivalGate` — extract the decision into a pure function
  (`Decide(onPath, isWindows) -> (exitCode, message)`) and assert: non-Windows +
  missing → 127 (unchanged); Windows + missing → 0 with the inspection-only note.

**Verify on a real Windows host** (the `vr-windows` remote target): `dotnet run
--project tools\VisualRelay.Cli\VisualRelay.Cli.csproj -- launch` opens the window;
pick a repo with `llm-tasks/`; queue/logs/settings render; closing and reopening
preserves UI state (proves config-dir resolution writes successfully).

**Done when:**

- On Windows with .NET 10 SDK, the app window opens via `dotnet run … -- launch`
  with no `nono`/`swival`/Nix present and no unhandled exception.
- UI state and settings persist across restarts under `%APPDATA%`/`%LOCALAPPDATA%`.
- macOS/Linux behavior is byte-for-byte unchanged (gates still 127 there; XDG
  layout untouched when the vars are set).
- `./visual-relay check` green on macOS/Linux; new unit tests pass; C#/AXAML files
  under the 300-line guard; Conventional Commit subjects.

---

## Phase 1 — A `visual-relay.cmd` + `visual-relay.ps1` entry point that bootstraps the native toolchain

**Goal:** On a clean Windows machine, a developer clones the repo and runs
`.\visual-relay launch`; the launcher provisions .NET 10 (and uv/Python for later
phases) with one consent prompt, then runs the C# CLI — mirroring how
`./visual-relay` self-provisions via Nix on Unix.

**Approach (decision 2 + decision 1):**

- Add `visual-relay.cmd` (thin shim → PowerShell) and `visual-relay.ps1` (the
  bootstrap). Keep `.ps1` minimal and delegate everything post-bootstrap to the
  CLI via `dotnet run --project tools\VisualRelay.Cli\VisualRelay.Cli.csproj --
  <cmd> <args>`. Export the same env the bash launcher does
  (`AVALONIA_TELEMETRY_OPTOUT=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`,
  `MSBUILDDISABLENODEREUSE=1`, `PYTHONDONTWRITEBYTECODE=1`,
  `VISUAL_RELAY_SCRIPT_DIR`, `ORIGINAL_CWD`) — see `visual-relay:25`.
- `.ps1` bootstrap steps: resolve script dir; detect `dotnet` (SDK ≥ 10) → if
  missing, consent-prompt and run `dotnet-install.ps1` into
  `%LOCALAPPDATA%\visual-relay\dotnet`, prepend to `PATH`; detect `uv` → offer the
  official installer (used by Phase 2; a soft want for `launch`); detect `git` →
  hint `winget install Git.Git` if missing. Non-interactive (no TTY / piped):
  print the manual one-liners and exit non-zero without installing anything —
  matching the bash launcher's `_offer_nix_install` contract (`visual-relay:32-33`).
- **Do not trip the shell-size guard.** The launcher-size guard lives in
  `tools/VisualRelay.Guards/ShellSizeGuard.cs` + `ShellScriptClassifier.cs` +
  `ShellScriptLineCounter.cs` (run by `./visual-relay guards`/`check`). Decide and
  implement one of: (a) classify `.ps1`/`.cmd` and apply an analogous logic-line
  cap, or (b) explicitly exclude the Windows launchers from the bash-oriented
  guard. Add a test in the guards' test suite for whichever you choose.
- Update `.gitattributes` so `visual-relay.cmd`/`.ps1` keep CRLF (or are marked
  appropriately) and the bash `visual-relay` keeps LF.

**Files:**

- `visual-relay.cmd` (new), `visual-relay.ps1` (new)
- `tools/VisualRelay.Guards/ShellScriptClassifier.cs` (or guard config) + its tests
- `.gitattributes`
- `tests/VisualRelay.Tests/` — hermetic launcher test mirroring the existing bash
  launcher tests (e.g. `Installer5Bootstrap2LauncherTests.cs`): run
  `visual-relay.ps1` with a crafted PATH and stub `dotnet`/`uv` recording argv;
  assert it execs `dotnet run --project …VisualRelay.Cli… -- launch <args>` with
  args (including a space-containing one) intact, and that a missing-tool +
  non-interactive run prints the manual one-liner and does not install.

**Tests (write the failing tests first):** the launcher argv-integrity and
no-TTY-no-install cases above. Gate these tests to run only where PowerShell is
available (Windows CI from Phase 4; `pwsh` on the Unix runners if present) so they
don't silently skip everywhere.

**Done when:**

- On a clean Windows host, `.\visual-relay launch` provisions .NET 10 with a single
  consent prompt and opens the app; a second run reuses the toolchain with no
  prompt and no global-machine changes outside `%LOCALAPPDATA%\visual-relay`.
- `.\visual-relay build` / `test` / `check` dispatch into the same C# CLI commands.
- Non-interactive runs print actionable install one-liners and exit non-zero
  without installing.
- `./visual-relay check` stays green on Unix (guard handles the new scripts);
  Conventional Commit subjects.

---

## Phase 2 — Local model backend (LiteLLM) lifecycle on Windows

**Goal:** `VisualRelay.Backend start|status|stop` works on Windows: it provisions
the uv venv with the Windows layout, spawns the LiteLLM proxy without `/bin/sh`,
and tracks liveness without POSIX signals. Re-enables the backend autostart that
Phase 0 stubbed off.

**Approach:**

- **Windows venv layout.** `BackendPaths.VenvPython`/`VenvLitellm`
  (`BackendPaths.cs:36-37`) hardcode `bin/python`/`bin/litellm`. On Windows uv
  creates `Scripts\python.exe` and `Scripts\litellm.exe`. Branch on
  `OperatingSystem.IsWindows()` → `Path.Combine(VenvDir, "Scripts", "python.exe")`
  / `"litellm.exe"`.
- **Spawn without `/bin/sh`.** Replace the `/bin/sh -c 'exec litellm … >LOG 2>&1'`
  spawn in `BackendLifecycle.Start.cs` with a direct `ProcessStartInfo` for the
  resolved `litellm` exe (`UseShellExecute = false`, `RedirectStandardOutput/Error`
  to the log file via `FileStream`), so no shell is required on any OS. This also
  simplifies the Unix path. Keep writing the pidfile that `BackendProcess` reads.
- **Liveness/term/kill** already handled in Phase 0's `BackendProcess` Windows
  branch — confirm `start`/`status`/`stop` use it end to end.
- **CPU-time activity sampling.** `ProcessTreeCpuSampler.cs` parses `/bin/ps -axo
  pid=,ppid=,time=`. On Windows, sample via `System.Diagnostics.Process`
  (`TotalProcessorTime` walked over the child tree) instead of shelling to `ps`.
  Factor the sampler behind a small interface with a POSIX (`ps`) impl and a
  `Process`-based impl selected by OS; keep the watchdog logic OS-agnostic.

**Files:**

- `src/VisualRelay.Core/Execution/BackendPaths.cs`,
  `src/VisualRelay.Core/Execution/BackendLifecycle.Start.cs`,
  `src/VisualRelay.Core/Execution/ProcessTreeCpuSampler.cs`,
  `tools/VisualRelay.Backend/` (wire-up), `tests/VisualRelay.Tests/`.

**Tests (write the failing tests first):**

- `BackendPaths` venv-path test: Windows branch yields `Scripts\python.exe` /
  `Scripts\litellm.exe`; Unix yields `bin/python` / `bin/litellm`.
- A backend start/status/stop integration test using a stub "litellm" executable
  (a tiny script/exe that sleeps and writes a line) asserting: log file gets
  output, pidfile written, `status` reports alive, `stop` terminates it and clears
  the pidfile — runnable on Windows CI (Phase 4) and Unix.
- CPU sampler: a test double process tree; assert non-zero CPU is observed via the
  OS-appropriate sampler.

**Done when:**

- On Windows, `.\visual-relay launch` autostarts a healthy backend (re-enable the
  `LaunchCommand.cs:31-34` start on Windows); the in-app backend status indicator
  is green; a model call routes through the proxy.
- Unix backend behavior unchanged (now shell-free); `./visual-relay check` green.

---

## Phase 3 — Windows-aware task execution (git, process teardown, OS-selected sandbox wrapper)

> Unknowns resolved: **swival runs on Windows** (pure Python; `uv tool install
> swival`) and the chosen sandbox is **MXC** (decision 3), wrapping swival on Windows
> the way nono does on Unix — same prefix-seam. Step zero of this phase is the
> empirical write-block test (below); do not ship execution until it passes. Do this
> phase last; Phases 0–2 + 4 deliver the usable GUI without it.

**Goal:** Running a task on Windows behaves correctly and safely: git operations,
process-tree teardown, and test execution work, and swival runs wrapped in **MXC**
(`wxc-exec` with a VR-authored confined-write policy), with the active sandbox mode
surfaced and execution blocked if no sandbox is available — never silently
uncontained.

**Approach:**

- **Git resolution.** `GitInvoker.cs`: the `/usr/bin/git` preference and `xcrun`
  lookup are macOS-specific; the `/bin/sh -lc "command -v git"` fallback is
  POSIX. On Windows, resolve `git` via PATHEXT (reuse Phase 0's PATH helper); drop
  the shell fallback. Keep macOS behavior intact behind its `IsOSPlatform(OSX)`
  guards.
- **Process-tree teardown.** Confirm `ProcessCapture.cs` uses its non-POSIX
  fallback (`Process.Kill(entireProcessTree: true)`) on Windows and that the
  `setpgid`/`kill(-pgid)` block stays Linux/macOS-only. Add a Windows test that a
  spawned child tree is fully killed on timeout/cancel.
- **Test runners.** `ShellTestRunner.cs` / `SandboxedTestRunner.cs` shell `/bin/sh
  -lc "<cmd>"`. The configured test command (from `.relay/config.json`) is
  user-authored and may be shell syntax. On Windows, run it through `cmd.exe /c`
  (or `pwsh -Command`) instead of `/bin/sh`. Keep `ITestRunner` the seam; add a
  Windows runner implementation selected by OS.
- **Generalize the sandbox wrapper into an OS/config-selected prefix (decision 3).**
  Today VR hard-wires the nono wrapper: `ProcessRunners.cs` builds the launch target
  as a *prefix* + swival (`BuildNonoPrefix`/`BuildLaunchTarget` →
  `nono run --profile … --allow-cwd --rollback --no-rollback-prompt -- swival …`),
  and `SandboxedTestRunner.cs` wraps tests the same way. Extract that prefix behind a
  tiny seam (e.g. `ISandboxLauncher.BuildPrefix(config) -> IReadOnlyList<string>`)
  and select the implementation by OS/config — the rest of the pipeline is unchanged:
  - **Unix:** the existing nono prefix + `vr-guard` profile. Byte-for-byte identical;
    all current tests stay green.
  - **Windows — MXC (the chosen wrapper).** The launcher is `wxc-exec.exe
    <policy.json>` wrapping the swival command. VR generates the policy itself (do
    **not** use the SDK's auto-generated policy — it can be overly permissive),
    mirroring the `vr-guard` allow-list: `readwritePaths` = workspace root + the same
    per-ecosystem cache dirs vr-guard grants (`%LOCALAPPDATA%`/NuGet/uv/npm/etc.),
    `readonlyPaths` = broad (drives the agent reads), `network.allowOutbound = true`
    (VR intentionally leaves network open; Windows wouldn't filter it anyway). Pin a
    specific MXC version. Provision `wxc-exec.exe` from the pinned signed MXC release
    (no Node needed) — or via `@microsoft/mxc-sdk` if Node is acceptable; detect it
    via the Phase-0 PATHEXT helper.
  - **Windows — opt-in alternatives (documented, not default):** `wsl -d <distro> nono
    run --profile vr-guard --` (nono-in-WSL2 — stronger, kernel-enforced, reuses the
    profile; needs WSL2 + Linux-side workspace), a custom restricted-token launcher
    (Codex-style, in-place writes), or swival-native `--sandbox builtin` (labeled
    degraded) / `--sandbox agentfs` (when its Windows mount ships).
  - **Windows — none enabled:** **block** with run controls disabled. Always emit the
    active mode (and any residual gap, e.g. builtin's shell escape) to the run log + UI.
  - `NonoProfileEnsurer.cs` is already a no-op on Windows; ensure nothing downstream
    assumes a `vr-guard` profile or a nono binary exists on native Windows.
  - **Copy-back caveat for overlay/box wrappers (agentfs, Sandboxie, containers):**
    these redirect writes to a delta/box rather than the real tree (e.g. agentfs's
    `~/.agentfs/run/…/delta.db`), so VR must materialize changes back into the
    workspace before its commit gate (or run git inside the overlay). nono-in-WSL2 and
    the Codex-style ACL wrapper write **in place** and avoid this. Spike copy-back only
    if an overlay/box wrapper is chosen.
- **swival invocation + detection.** swival runs on Windows (pure Python). Drop the
  brew-tap assumption: `SwivalGate` should detect swival via the Phase-0 PATHEXT
  helper and, when missing, instruct `uv tool install swival` (uv is already
  provisioned in Phase 1), not `brew`. Provision swival here.
- **MXC provisioning + gate.** Provision a pinned `wxc-exec.exe` (signed MXC release
  zip, cached under `%LOCALAPPDATA%\visual-relay\mxc`; or `@microsoft/mxc-sdk`). Add a
  Windows `MxcGate` parallel to `NonoGate`: when `wxc-exec` is absent, block execution
  with install guidance (mirrors nono being a hard dep on Unix). Make the pinned
  version a single constant so the flip to a newer MXC is one edit.

**Files:**

- `src/VisualRelay.Core/Execution/GitInvoker.cs`, `…/ProcessCapture.cs`,
  `…/ShellTestRunner.cs`, `…/SandboxedTestRunner.cs`, `…/ProcessRunners.cs` +
  `…/ProcessRunners.SandboxEnv.cs` (extract the `ISandboxLauncher` prefix seam; add the
  MXC launcher + the VR-authored MXC policy generator, sibling to `NonoProfileEnsurer`),
  `tools/VisualRelay.Cli/Gates/SwivalGate.cs` (Windows guidance) + new `MxcGate.cs`,
  relevant view-model wiring for the run-disabled/warning UI, `tests/VisualRelay.Tests/`.

**Tests (write the failing tests first):**

- Git resolution chooses the PATHEXT-resolved `git.exe` on the Windows branch and
  never shells `/bin/sh`.
- Test-runner OS dispatch: Windows → `cmd.exe /c`, Unix → `/bin/sh -lc` (assert via
  the captured `ProcessStartInfo`, no real run needed).
- **MXC policy generation (pure-logic):** given a workspace root + cache dirs, the
  generator emits JSON with `readwritePaths` = workspace + caches, broad
  `readonlyPaths`, `network.allowOutbound = true` — and never emits the SDK's
  auto-generated policy. Assert the path lists and that the workspace root is the only
  non-cache writable root.
- **Sandbox-strategy selection (pure-logic, assertable on any OS):** Unix → the nono
  wrapper command unchanged; Windows + MXC available → launch target is `wxc-exec.exe
  <policy.json> … swival …`; Windows + an opt-in alternative → its prefix; Windows +
  nothing → execution blocked, run controls disabled. Assert via the captured launch
  target (`fileName` + args), no real run needed.
- **Step zero — empirical write-confinement test on the `vr-windows` host (gating):**
  under the generated MXC policy, a swival shell command that writes/deletes a file
  **outside** the workspace is **blocked**, while writes **inside** the workspace
  succeed — the Windows analogue of `DONE-sandbox-1`'s macOS verification. Execution
  must not ship until this passes; if MXC fails to block, fall back to the
  `nono-in-WSL2` opt-in and re-evaluate.
- Process-tree kill on Windows (integration, Phase 4 CI).

**Done when:**

- The step-zero write-confinement test passes on Windows under MXC; a sample task then
  runs end to end and produces the normal `.relay` artifacts, with the active sandbox
  mode surfaced in the UI/log.
- With MXC absent and no opt-in set, execution is cleanly blocked with accurate
  install guidance (via `MxcGate`).
- macOS/Linux execution is byte-for-byte unchanged and still nono-wrapped.
- `./visual-relay check` green; Conventional Commit subjects.

---

## Phase 4 — CI/release for Windows

**Goal:** Tagged releases produce a Windows artifact, and a Windows CI job runs the
build + tests so the port doesn't regress.

**Approach:**

- **Add a Windows build job** to `.github/workflows/release.yml`: a matrix entry
  `win-x64` (and optionally `win-arm64`) `runs-on: windows-latest` with
  `actions/setup-dotnet@v4` `10.0.x`. Publish `VisualRelay.App` (and `Init`,
  `GenBackendConfig`, `Backend`) self-contained for the Windows RID, exactly like
  the macOS steps (`release.yml:22-60`) but without `codesign`/`build-app-bundle`.
- **Windows packaging.** No `.app` bundle. Assemble `publish\` with
  `VisualRelay.App.exe`, the sibling launchers (`visual-relay.cmd`/`.ps1`), and
  `tools\backend\litellm-config.yaml` (mirror the macOS "Assemble release layout"
  step `release.yml:80-87`), then zip it (`Compress-Archive` →
  `visual-relay-win-x64.zip`) and compute SHA256. Authenticode signing is optional
  and out of scope for the first release (note it as future work).
- **Smoke test** (mirrors `release.yml:101-102`): `VisualRelay.App.exe --help`.
- **CommandGuardEnsurer.** `CommandGuardEnsurer.cs` publishes guard binaries per
  `RuntimeInformation.RuntimeIdentifier`; ensure `win-x64`/`win-arm64` resolve.
- **VisualRelay.Packaging.** Its `build-app-bundle` is macOS-only; either guard it
  or add a `--platform windows` no-op/zip path so the packaging tool doesn't run
  on the Windows job.
- Optionally add a separate `ci.yml` Windows job that runs `dotnet build` + `dotnet
  test` on PRs (the repo commits to `main` without PRs per AGENTS.md, so at minimum
  add a Windows leg to whatever test workflow exists, or document running the suite
  on the `vr-windows` host).

**Files:**

- `.github/workflows/release.yml` (+ a CI test workflow if one exists),
  `src/VisualRelay.Core/Execution/CommandGuardEnsurer.cs`,
  `tools/VisualRelay.Packaging/`.

**Done when:**

- A tag build produces `visual-relay-win-x64.zip` + `.sha256`; the smoke test
  passes; the Windows job builds and runs the test suite green.
- The macOS release legs are unchanged.

---

## Phase 5 — Docs and Windows polish

**Goal:** Windows is documented as a first-class (inspection-first) platform and
the remaining macOS-only assumptions degrade cleanly.

**Approach:**

- **README install section** (`README.md`, between the `BEGIN/END install section`
  markers): add a Windows path — clone, `.\visual-relay launch`, the native
  bootstrap (no Nix), and an explicit note that task **execution** on Windows is
  unsandboxed/gated per decision 3 while inspection is fully supported. Keep the
  Unix/Nix instructions as the primary path.
- **TROUBLESHOOTING.md**: Windows entries (PowerShell execution policy, PATH after
  `dotnet-install`, where state lives under `%APPDATA%`/`%LOCALAPPDATA%`).
- **Obsidian bridge** (`ObsidianBridgeSettings.cs`): the hardcoded
  `~/Library/Mobile Documents/iCloud~…` path is macOS-only — make it conditional
  (no-op / sensible Windows default or explicit unset) so settings don't surface a
  dead macOS path on Windows.
- **Git hooks** (`HookInstaller.cs`): the embedded `#!/usr/bin/env bash` pre-commit
  hook runs under Git-for-Windows' bundled bash, so it generally works; verify
  `install-hooks` on Windows and document the Git-for-Windows requirement. The
  `SetUnixFileMode` chmod is already guarded.
- App icon: `ApplicationIcon` `.ico` is already set; confirm the taskbar/window
  icon renders on Windows.

**Files:** `README.md`, `TROUBLESHOOTING.md`,
`src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs`,
`src/VisualRelay.Core/Init/HookInstaller.cs`.

**Done when:** README/TROUBLESHOOTING describe the Windows flow accurately; no
macOS-only path is shown to Windows users; `install-hooks` verified on Windows;
`./visual-relay check` green.

---

## Sequencing

- **Phase 0** (GUI opens) and **Phase 1** (Windows launcher) are the core win and
  can land in either order, but 0 first lets you verify with a plain `dotnet run`
  before adding the launcher.
- **Phase 2** (backend) builds on Phase 0's `BackendProcess` Windows branch.
- **Phase 3** (execution) is last and blocked only on owner sign-off for the
  Windows execution default (decision 3); the swival/sandbox unknowns are resolved
  (Notes).
- **Phase 4** (CI) can land right after Phase 0/1 to lock in the build, then gain
  the test legs from each later phase.
- **Phase 5** (docs) trails whatever has shipped.

This pairs naturally with the `bootstrap-1/2/3` toolchain work: it is the Windows
analogue of "self-provision the launch toolchain", using native installers where
those tasks used Nix.

## Global "Done when"

- On Windows: `.\visual-relay launch` opens the app and all inspection features
  work, with the toolchain self-provisioned and no Nix.
- macOS/Linux behavior is unchanged throughout (every Windows branch is additive;
  no XDG/Nix/sandbox regressions).
- Task execution on Windows wraps swival in **MXC** (decision 3) with a VR-authored
  confined-write policy, verified by the Phase-3 empirical write-block test;
  nono-in-WSL2 is the documented stronger opt-in. Execution is never silently
  uncontained — if no sandbox is available it is blocked.
- CI publishes a Windows artifact and runs the suite on `windows-latest`.
- `./visual-relay check` green on macOS/Linux at every phase boundary; new tests
  pass; C#/AXAML files under the 300-line guard; Conventional Commit subjects.

## Notes / decisions / risks

- **Sandbox research findings (resolved 2026-06-24).** The two old unknowns are
  answered:
  - **swival runs on Windows.** It is pure Python (PyPI `swival`, classifier "OS
    Independent", requires Python ≥ 3.13); the macOS Homebrew tap is just
    convenience. Cross-platform install is `uv tool install swival` — and VR already
    provisions `uv`, so swival is free to provision on Windows. (Smoke-test swival's
    own shell tool on Windows, but no build gap exists.)
  - **swival has its own sandbox: `--sandbox builtin|agentfs|nono`.** VR currently
    ignores it and drives `nono run … -- swival …` itself on purpose (version
    pinning, `ProcessRunners.cs:76`). On Windows, drop that wrapper and use swival's
    backend directly. `nono` = Landlock/Seatbelt, Unix-only. `builtin` = app-layer
    file-tool guards, cross-platform, **but shell is an unguarded write/delete
    escape** (the exact reason nono was added — `DONE-sandbox-1-…md`), so it is a
    *degraded* mode. `agentfs` (tursodatabase/agentfs) = cross-platform CoW overlay,
    the parity target.
  - **AgentFS-Windows flip condition.** AgentFS is purpose-built to be
    cross-platform (Linux=FUSE, macOS=NFS), Turso ships a Windows PowerShell
    installer + Windows release binaries, but its `MANUAL.md` currently documents no
    Windows mount backend and marks `agentfs exec` **"Unix only"** (BETA). **Adopt
    `swival --sandbox agentfs` as the Windows default when** `agentfs exec` works on
    Windows with a real overlay mount (WinFsp/ProjFS/NFS-loopback). Until then,
    Windows execution uses `builtin` (labeled degraded) or WSL2 (opt-in), or is
    blocked — per the decision-3 sign-off. Re-check the AgentFS manual/releases at
    revisit time.
  - **WSL2 is a non-goal *for the GUI*** (it would give a Linux GUI via WSLg and
    defeat a native port) but is a legitimate **execution-only** option: keep the
    GUI native on Windows and delegate task execution into WSL2 where nono+Landlock
    work — the only kernel-enforced isolation available on Windows today. Offer as
    an explicit opt-in, not the default.
  - Sources: [swival](https://swival.dev/), [swival usage](https://swival.dev/pages/usage.html),
    [swival nono](https://swival.dev/pages/nono.html), [swival agentfs](https://swival.dev/pages/agentfs.html),
    [swival PyPI](https://pypi.org/project/swival/),
    [tursodatabase/agentfs](https://github.com/tursodatabase/agentfs),
    [AgentFS install](https://docs.turso.tech/agentfs/installation),
    [AgentFS MANUAL](https://github.com/tursodatabase/agentfs/blob/main/MANUAL.md).
- **External Windows sandbox wrappers — research, 2026-06-24 (Family A in decision 3).**
  Yes, swival can be wrapped on Windows the way nono wraps it on Unix; the wrapper is
  just a different launch prefix. Ranked:
  - **nono-in-WSL2** — most faithful (same nono, same `vr-guard` profile,
    kernel-enforced); WSL2 is nono's official Windows story. Cost: WSL2 dep +
    cross-boundary workspace path. In-place writes (no copy-back).
  - **Microsoft Execution Containers (MXC) — CHOSEN (usable today).** Cross-platform,
    MIT, JSON `readonlyPaths`/`readwritePaths` + `network.allowOutbound` (= nono's
    model). Ships now (signed release binaries v0.6.1/v0.7.0 + `@microsoft/mxc-sdk`);
    CLI `wxc-exec.exe config.json`; Windows `processcontainer` is the default & only
    *stable* backend and enforces `readwritePaths`/`readonlyPaths` today. **Windows
    gaps (verified):** `deniedPaths` and outbound-network filtering are unenforced —
    network is a **non-issue for VR** (VR leaves net open anyway); write-confinement is
    enforced. **Respect:** early-preview "not a security boundary" (OK for VR's
    accident-containment model, stated); SDK auto-policies can be over-permissive → VR
    hand-authors its own; pin a version; no C# SDK (shell out to `wxc-exec.exe`).
    **No source observed a confirmed Windows write-block** → Phase-3 step-zero must
    empirically verify (write outside workspace blocked) before shipping, mirroring
    `DONE-sandbox-1`'s macOS proof.
  - **Sandboxie-Plus** — turnkey, mature, `Start.exe /box:<name> <cmd>`. Caveats:
    GPLv3, kernel driver, copy-back (writes land in the box).
  - **Custom native wrapper** — restricted token + workspace write-ACEs + deny-write
    on `.git` + per-user firewall, modeled on OpenAI Codex's Windows sandbox; in-place
    writes, full control, but needs elevation and hits GPO/AV/path edge cases.
  - **Weak fits:** Windows Sandbox (`.wsb`) is interactive-only (no headless mode);
    microsandbox/containers are micro-VM/bind-mount (heavy dep + copy-back).
  - Sources: [microsoft/mxc](https://github.com/microsoft/mxc),
    [MXC schema](https://github.com/microsoft/mxc/blob/main/docs/schema.md),
    [MXC examples](https://github.com/microsoft/mxc/blob/main/docs/examples.md),
    [MXC on Windows walkthrough (byteiota)](https://byteiota.com/microsoft-mxc-sdk-sandbox-your-ai-agents-on-windows/),
    [Windows platform security for AI agents](https://blogs.windows.com/windowsdeveloper/2026/06/02/windows-platform-security-for-ai-agents/),
    [nono WSL2 issue #463](https://github.com/always-further/nono/issues/463),
    [Sandboxie-Plus](https://github.com/sandboxie-plus/Sandboxie),
    [Codex Windows sandbox internals](https://codex.danielvaughan.com/2026/05/14/codex-cli-windows-sandbox-engineering-restricted-tokens-acls-elevated-architecture/),
    [AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation),
    [zerobox](https://github.com/afshinm/zerobox).
- **Keep every change additive and OS-guarded.** Prefer `OperatingSystem.IsWindows()`
  branches and the existing accessor/seam patterns (env accessor in `KeyEnvFile`,
  injectable `isAlive` in `BackendProcess`, the `ITestRunner`/`ISubagentRunner`/
  `IGitInvoker` interfaces) so the Windows paths are unit-testable from the
  macOS/Linux CI and macOS/Linux behavior is provably untouched.
- **Verification needs a real Windows host.** Much of the payoff (window opens,
  state persists, backend runs) can only be confirmed on Windows — use the
  `vr-windows` remote host. Pure-logic changes are covered by the seam-based unit
  tests that run everywhere; Phase 4 adds `windows-latest` CI to make it durable.
- **.NET 10 SDK on Windows CI:** `actions/setup-dotnet@v4` with `10.0.x` already
  provides it on `windows-latest`; the launcher's `dotnet-install.ps1` path is only
  for developer machines without the SDK.
