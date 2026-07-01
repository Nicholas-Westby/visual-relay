# Show Sandbox Paths (Readable / Writable / Blocked) in Settings

Add a **read-only** panel to the Settings screen that shows the effective sandbox filesystem policy —
which paths are **readable**, **writable**, and **blocked** — for the current OS. It must include the
**inherited** paths (from the base `swival`/`default` profile groups), and it must be **derived from
the actual resolved values at runtime**, not a hardcoded snapshot of whatever the base paths happen to
be today. **Display only for now** — no editing; a later task can make entries editable.

## Current state

**Settings UI.** `src/VisualRelay.App/Views/SettingsWindow.axaml` hosts a single
`Controls/SettingsPanel.axaml` (`x:DataType="vm:MainWindowViewModel"`), a `ScrollViewer` around a
`StackPanel Spacing="6"` of bordered sections. There is already a **"Verbose sandbox diagnostics"**
toggle in that panel (bound to `VerboseSandboxDiagnostics`), so a sandbox area already exists to grow.
Self-contained sections are factored into their own controls with a VM partial — see
`Controls/ObsidianSettings.axaml` (+ `.axaml.cs`) paired with
`src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs`; other settings state lives in
`MainWindowViewModel.Settings.cs`. Section styling tokens in use: panel bg `#12151C`, border `#252A33`,
header `#F2F5FA`, body text `#9AA3B1`/`#E7ECF3`, accent `#5575F2`, status dots green `#5AD47D` / gray
`#4A5568`.

**Where the real policy lives.**

- **macOS/Linux — nono.** The enforced profile is `vr-guard` (`packaging/nono/vr-guard.json`, embedded
  as `VisualRelay.Core.vr-guard.json` and self-healed to the runtime copy by
  `NonoProfileEnsurer.EnsureAsync`). vr-guard's **own** directives are `filesystem.allow` (read-write),
  `filesystem.read` (includes `/` — reads are broad), and `filesystem.deny`. But **most of the effective
  set is inherited** from `groups.include` (the `swival` → `default` groups), and those groups are
  defined **inside the nono binary**, not in the repo — so their concrete paths can only be obtained by
  asking nono.
- **Windows — MXC.** `MxcPolicyGenerator.DefaultWindowsCacheDirs()` + the workspace form the read-write
  set (`Generate()` → `filesystem.readwritePaths`); reads are broad by MXC default; writes elsewhere are
  blocked. This is computed in-process — no external tool needed.

**Two facts that shape the design (both verified against nono 0.61.1):**

1. **`nono profile groups <name> --json` is the way to expand a group** to concrete paths. It returns
   `allow.read` / `allow.readwrite` as `{ "raw": "~/…", "expanded": "/Users/…" }` entries, and for deny
   groups a `deny.access` path list (plus non-path `deny.commands` / `deny.unlink` which must be
   ignored). Each group carries a `platform` field (`macos` / `linux` / `cross-platform`) for filtering
   to the current OS. `nono profile show <name> --json` does **not** expand groups (it only lists group
   *names*), and the all-groups `nono profile groups --json` returns *counts*, not paths — so per-group
   `--json` calls are required for concrete paths.
2. **Do not trust `nono profile show <name>` / the registered profile copy for vr-guard's own
   directives.** `nono … <name>` reads `~/.config/nono/profiles/vr-guard.json`, which is a **stale**
   copy (verified: it currently lacks the `.nuget`/dotnet grants that the repo + the enforced runtime
   copy both have). The viewer must read vr-guard's own `allow`/`read`/`deny` + `groups.include` from
   the **enforced** profile — the embedded resource `VisualRelay.Core.vr-guard.json` (always present,
   byte-identical to what runs) or the `NonoProfileEnsurer`-managed file — and use nono **only** to
   expand the group *definitions* (which come from the binary and are not stale).

## What to build

### 1. Derivation service (Core) — do the resolving here, not in the VM

Add a service, e.g. `SandboxPathInspector` in `src/VisualRelay.Core/Execution/`, that returns a
structured list of entries `{ raw, expanded, access, source }` where `access ∈ { ReadOnly, ReadWrite,
Blocked }` and `source` is `"vr-guard"` or the originating group name (for provenance in the UI):

- **macOS/Linux path:**
  1. Read the enforced vr-guard profile (embedded `VisualRelay.Core.vr-guard.json`): take its own
     `filesystem.allow` → ReadWrite, `filesystem.read` → ReadOnly, `filesystem.deny` → Blocked, and its
     `groups.include` name list. Honor `{ "path", "when" }` predicates for the current OS.
  2. For each included group, run `nono profile groups <name> --json` and merge: `allow.read` → ReadOnly,
     `allow.readwrite` → ReadWrite, `deny.access` → Blocked. Skip entries whose `platform` doesn't apply
     to the current OS. Ignore `deny.commands` / `deny.unlink` (not filesystem paths).
  3. Also surface the **per-run** writable additions so the picture is honest: the active workspace root
     (granted via `--allow-cwd` in `SwivalSubagentRunner.BuildNonoPrefix`) and any
     `RelayConfig.SandboxExtraAllowPaths` for the selected project — label them "current workspace" /
     "per-project extras" so they read as context-dependent, not profile-global.
  4. Resolve the nono binary the same way the run path does (`ProcessLauncher.OnPath("nono")` / the
     `NonoBinary` constant in `ProcessRunners.cs`). If nono is absent or a call fails, return an
     "unavailable" result — never throw into the UI (`NonoGate` treats missing nono as fatal for a run,
     but the Settings screen must degrade gracefully).
- **Windows path:** derive in-process from `MxcPolicyGenerator.DefaultWindowsCacheDirs()` + workspace as
  ReadWrite, plus a single synthetic "everything else" ReadOnly note and a "writes outside the list are
  blocked" note. No shell-out.
- **Derived, never hardcoded.** Every path entry must come from the profile JSON + nono group expansion
  (or the MXC generator). The only literals allowed are bucket labels and the explanatory copy. This is
  the core requirement: adding a path to `vr-guard.json` (e.g. a newly-granted toolchain cache path) or to a group
  must show up here with **no change to this code**.
- Prefer showing the `raw` form (`~/Library/Caches`) in the UI with `expanded` available as a
  tooltip/secondary — avoids baking the developer's home path into the display.

### 2. ViewModel — a new partial

`src/VisualRelay.App/ViewModels/MainWindowViewModel.Sandbox.cs` exposing the service output as three
observable collections (Readable / Writable / Blocked), an `IsSandboxInfoAvailable` (or status) flag,
and an async load/refresh command. Populate **asynchronously** — the nono group calls are subprocesses
and must not block opening Settings; show a lightweight loading state. Mirror the async/section pattern
already used by `MainWindowViewModel.ObsidianBridge.cs`.

### 3. View — a self-contained section control

Create `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml` (+ `.axaml.cs`), mirroring the
`ObsidianSettings` control, and embed it in `Controls/SettingsPanel.axaml` right **after** the "Verbose
sandbox diagnostics" border. Match the existing bordered-section styling. Show three labeled groups
(Readable / Writable / Blocked); each row shows the path, its access, and its source; use the existing
status-dot idiom (green for writable, neutral for read-only, red-ish for blocked) and keep it scrollable
when long. Include one line explaining the model so the list isn't misread:
"Reads: the whole filesystem **except** the blocked paths. Writes: **only** the paths listed here (plus
the current workspace)." Render the "unavailable" state cleanly when the service reports it.

### 4. Display-only

No editing, no persisted setting, no new writable grant introduced by this task. (A future task can add
editing / per-project `sandboxExtraAllowPaths` management.)

## Constraints & done criteria

- **Derived, not snapshotted (the headline requirement).** Add a test proving the classifier reflects
  the *input*, not constants: feed the parser a sample vr-guard JSON (own `allow`/`read`/`deny` +
  `groups.include`) and sample `nono profile groups --json` payloads (an allow group with
  `read`/`readwrite`, a deny group with `deny.access` + a `deny.commands` that must be ignored, and a
  wrong-`platform` entry that must be filtered) and assert the correct three-bucket classification,
  source attribution, and OS filtering. A path added to the sample input must show up in the output.
- **Handle the stale-copy pitfall:** the test suite should encode that own-directives come from the
  embedded/enforced profile, not the registered `~/.config/nono/profiles` copy.
- **Graceful degradation:** a test for the nono-absent / call-failure path returning the "unavailable"
  state without throwing.
- **Windows** derives from the existing `MxcPolicyGenerator` (no duplicate hardcoded list).
- Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines (factor the section into its own control and
  the parsing into the service; `SettingsPanel.axaml` is already long).
- No weakening of the sandbox; display-only. Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage will finalize the manifest)

- `src/VisualRelay.Core/Execution/SandboxPathInspector.cs` (new) — resolve + classify read/write/blocked
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Sandbox.cs` (new partial) — three buckets + async load
- `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml` (+ `.axaml.cs`) (new) — the section control
- `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` — embed the new section after the sandbox-diagnostics toggle
- `tests/VisualRelay.Tests/` — classifier/OS-filter/degraded-state tests
- (reference, no change) `packaging/nono/vr-guard.json`, `NonoProfileEnsurer`, `MxcPolicyGenerator`, `NonoGate`, `RelayConfig.SandboxExtraAllowPaths`, `ProcessRunners.cs` (`NonoBinary`)
