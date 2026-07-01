# Grant Unity Project Cache & License Paths in the Sandbox

Make the OS sandboxes stop **blocking Unity's writes** so a task run on a Unity (C#/.NET) project
does not fail on denied filesystem access. Unity's editor tooling writes to a handful of global
cache and license directories that live **outside** the workspace and outside the toolchain-cache
paths `vr-guard` currently grants; under the sandbox those writes are denied today.

This task covers **only the sandbox path grants**, and only on the two OSes that matter for Unity
here: **macOS** (nono) and **Windows** (MXC). **Linux is explicitly out of scope** for Unity (project
decision). It does **not** make VR detect or drive Unity — see **Scope / non-goals** below. Keep the
changes as small and general as the existing per-ecosystem grants (.NET, Swift, Node, …).

## Current state

VR confines Swival's writes with two OS sandboxes, both authored in-repo:

- **macOS/Linux — nono.** Profile `packaging/nono/vr-guard.json` (`"extends": "swival"`). The
  `filesystem.allow` array is the write/delete allowlist; `filesystem.read` is `["/",
  "$HOME/.gitconfig"]` so **reads are already global**. The file is an embedded resource
  (`VisualRelay.Core.csproj`, `EmbeddedResource Include="..\..\packaging\nono\vr-guard.json"`) and is
  self-healed to the runtime copy at run start by `NonoProfileEnsurer.EnsureAsync` — the repo file
  and the embedded copy are the *same* file, so editing `packaging/nono/vr-guard.json` and rebuilding
  `VisualRelay.Core` is the only edit needed (no second copy to sync). `vr-guard` extends the pulled
  `swival` pack, which pulls in nono's built-in group `user_caches_macos` — it grants **read-write**
  `~/Library/Caches` + `~/Library/Logs` and **read-only** `~/Library/Preferences`.
- **Windows — MXC.** `MxcPolicyGenerator.DefaultWindowsCacheDirs()` returns the read-write cache
  roots (mirroring vr-guard); `Generate()` writes them into `filesystem.readwritePaths`. Crucially it
  returns **only directories that already exist** (a missing path makes MXC container setup fail).
  `%LOCALAPPDATA%` and `%APPDATA%` are granted **wholesale** today.

What that means for Unity right now (each macOS verdict below was confirmed with
`nono why --profile vr-guard --path <p> --op readwrite`):

| Unity path | OS | Status today | Source |
|---|---|---|---|
| `~/Library/Caches/com.unity3d.*` (GI/shader/editor cache) | macOS | ✅ writable | group `user_caches_macos` |
| `~/Library/Logs/Unity` | macOS | ✅ writable | group `user_caches_macos` |
| editor binary under `/Applications/Unity/Hub/Editor/<ver>/…` | macOS | ✅ readable | group `system_read_macos` |
| `~/Library/Unity` (UPM package cache + `Asset Store-5.x`) | macOS | ❌ **write denied** | only `/` read |
| `~/Library/Application Support/Unity` (licensing client / `.ulf`) | macOS | ❌ **write denied** | only `/` read |
| `~/Library/Application Support/UnityHub` (editor resolution) | macOS | ❌ **write denied** | only `/` read |
| `~/Library/Preferences/com.unity3d.*.plist` (editor prefs) | macOS | ⚠️ read-only (write denied) | `user_caches_macos` grants Preferences read-only |
| `%LOCALAPPDATA%\Unity\*`, `%APPDATA%\Unity`, `%APPDATA%\UnityHub` | Windows | ✅ writable | wholesale `%LOCALAPPDATA%`/`%APPDATA%` |
| `%PROGRAMDATA%\Unity` (`C:\ProgramData\Unity`, license files) | Windows | ❌ **write denied** | not in `DefaultWindowsCacheDirs()` |

So most of Unity's churn (caches, logs, editor binary reads, and on Windows almost everything) is
already permitted. The genuine gaps are the **UPM package cache** and the **license/hub state dirs**
that sit outside the cache roots.

Workspace-local Unity dirs (`Library/`, `Temp/`, `obj/`, `Logs/`, `Build/`) are inside the granted
workspace (writable via `--allow-cwd`) and — because a standard Unity `.gitignore` ignores them and
they are large — are already excluded from nono's ~2 GiB rollback preflight by the **existing general
mechanism** `NonoRollbackSkipDirs.ComputeAsync` (skips top-level git-ignored dirs ≥256 MB via
`--skip-dir`). No new code is expected there; see the verification note under **What to build**.

## What to build

### 1. nono profile — `packaging/nono/vr-guard.json`

Add the missing macOS write grants to `filesystem.allow`, following the file's existing conventions:
`$HOME`-relative, and `{ "path": …, "when": "macos" }` predicates for OS-specific paths (hardcoded
`/Users/…` is rejected by a test). Group them with a short comment near the other toolchain blocks.
Add:

- `{ "path": "$HOME/Library/Unity", "when": "macos" }` — UPM global package cache + `Asset Store-5.x`
- `{ "path": "$HOME/Library/Application Support/Unity", "when": "macos" }` — licensing client state / `.ulf`
- `{ "path": "$HOME/Library/Application Support/UnityHub", "when": "macos" }` — Unity Hub (editor resolution)

Do **not** add: any Linux (`when: "linux"`) Unity paths (out of scope); `~/Library/Caches/*` or
`~/Library/Logs/*` (already granted by `user_caches_macos`); any `read:` entries (reads are already
global); or a broad `~/Library/Preferences` **write** — that would open every app's prefs. The
read-only Preferences limitation (editor prefs won't persist across sandboxed runs) is acceptable for
batch/CI runs and is explicitly out of scope; leave a one-line comment noting it rather than widening
the grant.

### 2. nono rollback exclusions — same file, `rollback.exclude_patterns`

The newly-granted `~/Library/Unity` (UPM cache) can be multiple GB; nono's rollback preflight
snapshots every writable path, so it must be excluded or it blows the fixed budget. Add the pattern:

- `"Unity"`

**Landmine — do NOT add `"Library"`.** These patterns match a path component/substring, so `"Library"`
would match `~/Library` (disabling rollback for the whole macOS Library tree) **and** the Unity
project's own workspace `Library/` folder. `"Unity"` is specific: it covers `~/Library/Unity`,
`~/Library/Application Support/Unity`, and `~/Library/Application Support/UnityHub`, without matching
`~/Library` or the lowercase `com.unity3d.*` caches (already excluded via the existing `"Caches"`
pattern).

### 3. Windows MXC — `MxcPolicyGenerator.DefaultWindowsCacheDirs()`

The wholesale `%LOCALAPPDATA%`/`%APPDATA%` grants already cover Unity's Windows cache, logs, Asset
Store, and Hub dirs. The one gap is the shared license dir. Add:

- `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Unity")`
  — i.e. `C:\ProgramData\Unity`

Add it to the `dirs` list *before* the existing `.Where(d => … && Directory.Exists(d))` filter so a
host without Unity installed simply drops it (mirrors how `~/.cargo` is handled). Do **not** add bare
`%PROGRAMDATA%` (over-broad).

### 4. Verify the workspace-local rollback skip (expected: no change)

Confirm `NonoRollbackSkipDirs` already keeps a Unity project's git-ignored `Library/`/`Temp/` out of
rollback preflight (they must be git-ignored — the standard Unity `.gitignore` does this — and ≥256 MB
to trip the size gate). If a realistic Unity project would still blow the budget, note it, but do not
special-case Unity by name here — the mechanism is intentionally ecosystem-agnostic.

### 5. Recommended empirical validation

The macOS path list above is the known default, not gospel — Unity's exact cache locations vary by
version. Validate against a real Unity project before finalizing:

- Query the live policy: `nono why --profile vr-guard --path <p> --op readwrite --json`.
- Or, without editing the profile, drop the candidate paths into a Unity repo's `.relay/config.json`
  `sandboxExtraAllowPaths` (they map straight to nono `-a <path>` via
  `SwivalSubagentRunner.BuildNonoPrefix`; validated in `RelayConfigLoader` — all are under `$HOME` and
  none are in its sensitive-subtree denylist) and run a real editor task to see what's actually hit.

## Scope / non-goals

This task is the **sandbox slice only**. It makes the sandbox stop blocking Unity; it does **not**
make VR recognize or run Unity projects. Out of scope (separate future tasks — do not start them here,
and do not let their absence block this one): a Unity-priority detector branch in
`TestCommandDetector.DetectCandidates` (today a Unity project matches the generic `*.csproj`/`*.sln`
branch and resolves `dotnet test`, which cannot drive Unity); a Unity batch-mode test/build command
with editor-path resolution off `ProjectSettings/ProjectVersion.txt`; and Unity licensing activation
(granting write to the license dirs here is necessary but not sufficient). **Linux Unity support is
also out of scope.** Because those pieces don't exist yet, this task's changes are **safe and inert**
for non-Unity projects (existence-filtered on Windows; extra allow entries only matter when Unity
actually runs).

## Constraints & done criteria

- **Tests.** Mirror the existing per-toolchain structure tests:
  - Add `VrGuardProfile_HasUnityEntries` to `tests/VisualRelay.Tests/NonoProfileStructureTests.cs`
    (same shape as `VrGuardProfile_HasDotNetEntries`/`HasSwiftEntries` using the `CollectPaths` helper).
  - Extend `VrGuardProfileRollbackTests.VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback`
    (`tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs`) to require `"Unity"`.
  - The existing `VrGuardProfile_HasWhenPredicatesForOsSpecificPaths` and the no-hardcoded-`/Users/`
    check (`VrGuardProfile_HasFilesystemAllowEntries`) must still pass — use `when` predicates and
    `$HOME`, never literal paths.
  - If a Windows-side structure/existence test exists for `DefaultWindowsCacheDirs()`, extend it; the
    existence-filter must keep a Unity-less host clean.
- **Stay general.** No Unity-version-specific hardcoding beyond the stable dir names; harmless when
  Unity is absent.
- Keep every new/edited `*.cs` file ≤ 300 lines (VR's file-size guard); do not weaken, skip, or delete
  any test.
- Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage will finalize the manifest)

- `packaging/nono/vr-guard.json` — `filesystem.allow` (macOS Unity grants) + `rollback.exclude_patterns` (`"Unity"`)
- `src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs` — `DefaultWindowsCacheDirs()` adds `C:\ProgramData\Unity`
- `tests/VisualRelay.Tests/NonoProfileStructureTests.cs` — new `VrGuardProfile_HasUnityEntries`
- `tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs` — require `"Unity"` exclusion
- possibly a Windows `MxcPolicyGenerator` test, if one exists
- (verify only, likely no change) `src/VisualRelay.Core/Execution/NonoRollbackSkipDirs.cs`
