## Stage 1 - Ideate

{
  "summary": "Add a pre-build sanity guard in the `./visual-relay` CLI (and optionally `Directory.Build.targets` for bare `dotnet build` coverage) that compares `git ls-files '*.cs'` (tracked count) against `find ... -name '*.cs'` excluding `obj/`/`bin/` (visible-on-disk count). Fail fast when visible < ~50% of tracked. The error message must name the root cause — stale virtio-fs/readdir cache — and the fix (remount via `fix-cache.sh` or `diskutil unmount` + `mount -t virtiofs`, or restart the VM), and explicitly state that `rm -rf obj bin` will not help. Optionally also flag suspiciously tiny assemblies (< ~10 KB). The guard must add negligible overhead (~tens of ms) on intact repos. Test via a fixture that simulates a mismatch (e.g. a temp directory with a subset of symlinked files).",
  "options": [
    "**Option A — Standalone guard script, CLI-only**: Write a small script (e.g. `scripts/guard-source-enumeration.sh` or `.py`) that does the `git ls-files` vs `find` comparison and prints the diagnostic + exit code. Call it at the top of the `build` and `check` cases in `./visual-relay` (the shell/script CLI). Pros: Simple, fast to implement and test in isolation, no MSBuild knowledge needed. Cons: Does not protect against bare `dotnet build` invoked outside the relay CLI, so a developer running `dotnet build` directly would bypass the guard.",
    "**Option B — MSBuild target in Directory.Build.targets**: Add a custom MSBuild target (e.g. `GuardSourceEnumeration`) that runs before `Compile` via `BeforeTargets=\"CoreCompile\"`. It uses `<Exec>` to run `git ls-files` and `find`, compares counts with a custom MSBuild task or inline conditional, and fails the build with an error message. Optionally also checks the size of the previous build's output assembly. Pros: Covers both `./visual-relay build` and bare `dotnet build` equally, natural integration with the build system. Cons: MSBuild XML is verbose for this logic; inline tasks in MSBuild are error-prone; could slow down every project evaluation if not carefully conditioned (e.g. only when `GitDetected` property is set).",
    "**Option C — Hybrid: small guard script + both CLI and MSBuild entry points**: Create a lightweight script (bash or Python) that contains the core comparison logic. Wire it into `./visual-relay` directly (Option A's approach) AND also invoke it from `Directory.Build.targets` via `<Exec>` in a pre-compile target. The MSBuild target gates on a property like `GuardSourceEnumeration=true` (opt-in) or always runs, but is cheap. Pros: Best coverage — protects all build entry points. The core logic lives in one place (the script) and is testable. Cons: Slightly more moving parts; the MSBuild `<Exec>` call has some overhead (process spawn per project), but that's acceptable since the script itself is fast (a single `git ls-files` + `find` per repo, memoized)."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is a .NET 10 Avalonia desktop app (3 src projects, 4 tool projects, 1 test project). The single entry point is `./visual-relay` (bash, ~143 lines), which delegates to `dotnet build` for both `build` and `check` cases. An existing guard pattern exists at `tools/guards/check-file-size.sh` — a standalone bash script called from `./visual-relay check`. `Directory.Build.targets` is a stub (`<Target Name=\"AvaloniaStats\"/>`) readily extensible for MSBuild integration. All project `.csproj` files use the SDK default `**/*.cs` glob (no explicit `<Compile Include=...>`), which relies on `readdir` — the exact mechanism that goes stale on virtio-fs. Current healthy source count: `git ls-files '*.cs'` returns 139, `find ... -name '*.cs'` (excluding obj/bin) returns 139. Tests use `TestRepository` (temp git dir), `TestGit` (static git runner), and `RepoSetup` (repo root resolver) in `TestDoubles.cs` — ideal for a tracked-vs-visible mismatch fixture. The Stage 1 ideation recommended Option C (hybrid: guard script + both CLI and MSBuild entry points). No `claude-vm/fix-cache.sh` script exists in this repo (it's external, on the host VM). The guard must be placed before the `dotnet build` call in both `build` and `check` cases, mirroring how `check-file-size.sh` is called.",
  "constraints": [
    "Guard must be wired into `build` and `check` cases of `./visual-relay` (bash script, line 104-105 and 131-133)",
    "Must optionally also cover bare `dotnet build` via `Directory.Build.targets` (currently a stub, line 1-3)",
    "Fail fast before `dotnet build` — not after producing empty assemblies",
    "Error message must name the root cause: stale virtio-fs / `readdir` cache on the VM",
    "Error message must specify remedies: remount via `claude-vm/fix-cache.sh`, or `sudo diskutil unmount <path>` + `sudo mount -t virtiofs <tag> <path>`, or restart the VM",
    "Error message must explicitly state that `rm -rf obj bin` will not help",
    "Threshold: visible count < ~50% of git-tracked count (or 0)",
    "Must exclude `*/bin/*` and `*/obj/*` from disk scan (matching existing guard pattern in `check-file-size.sh`)",
    "Must be fast (~tens of ms) on intact repos — negligible overhead for normal builds",
    "May optionally also flag suspiciously tiny assemblies (< ~10 KB)",
    "C# and XAML files must stay under 300 lines (enforced by `tools/guards/check-file-size.sh`)",
    "Conventional Commit subjects required",
    "Must have a test with a tracked-vs-visible mismatch fixture (e.g., temp repo with subset of files symlinked/cloned)",
    "Must cover `.cs` files; `.axaml` files are secondary but should be considered since MSBuild also globs them",
    "The guard must not be flaky — must pass cleanly when source view is intact",
    "Guard script should live alongside existing guards in `tools/guards/` (following `check-file-size.sh` convention)",
    "Solution should match Option C from Stage 1 (hybrid: guard script + both CLI and MSBuild entry points)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Built a hybrid (Option C) guard: a standalone bash script wired into both the `./visual-relay` CLI and `Directory.Build.targets` for bare `dotnet build` coverage. The guard compares `git ls-files '*.cs' '*.axaml'` (tracked count) against `find src tests tools -name '*.cs'/'*.axaml' -not -path '*/bin/*' -not -path '*/obj/*'` (visible count). When visible is 0 or below 50% of tracked, it exits 2 with a box-drawn ASCII error naming the root cause (stale virtio-fs/readdir cache on the VM), the remedies (`claude-vm/fix-cache.sh`, `diskutil unmount`+`mount -t virtiofs`, VM restart), and explicitly states `rm -rf obj bin` will not help. Six xUnit tests cover: intact repo passes, zero-visible fails with remedy text, partial-below-threshold fails with percentage, partial-above-threshold passes, obj/bin exclusion, and .axaml coverage. `./visual-relay check` is green — 246 tests passed, format clean, screenshots build. The guard adds ~30ms on intact repos (single `git ls-files` + `find` per invocation).",
  "excerpts": [
    "tools/guards/guard-source-enumeration.sh:1-132 — Bash guard script: counts git-tracked vs find-visible .cs/.axaml files, exits 0 when intact or 2 with full cause+remedy message when visible < 50% of tracked",
    "visual-relay:104-107 — `build` case: calls guard-source-enumeration.sh before dotnet build",
    "visual-relay:131-133 — `check` case: calls guard-source-enumeration.sh first, then check-file-size.sh",
    "Directory.Build.targets:1-22 — MSBuild GuardSourceEnumeration target with BeforeTargets=\"CoreCompile\", calling the same guard script via <Exec> for bare dotnet build coverage",
    "tests/VisualRelay.Tests/SourceEnumerationGuardTests.cs:1-243 — Six xUnit tests: IntactRepo_GuardPasses, ZeroVisible_GuardFailsWithRemedy, PartialVisible_BelowThreshold_GuardFails, PartialVisible_AboveThreshold_GuardPasses, ExcludesObjAndBinFromVisibleCount, CoversAxamlFiles"
  ],
  "repro": "1. `./visual-relay check` — guard passes silently, build+test+format all green (246 tests). 2. To simulate a degraded view: in a temp git repo with tracked .cs files, delete the visible files from disk (but not from git) → `bash tools/guards/guard-source-enumeration.sh` exits 2 with the box-drawn error naming virtio-fs, the fix-cache.sh remedy, and the `rm -rf obj bin` warning. 3. A bare `dotnet build` also triggers the guard via the MSBuild target in Directory.Build.targets."
}

## Stage 4 - Plan

{
  "plan": "## Plan: guard-degraded-source-enumeration (Option C — Hybrid)\n\n### Summary\nAdd a pre-build guard that compares `git ls-files '*.cs' '*.axaml'` (tracked count) against `find src tests tools -name '*.cs'/'*.axaml' -not -path '*/bin/*' -not -path '*/obj/*'` (visible-on-disk count). When visible is 0 or below 50% of tracked, fail fast with a box-drawn ASCII error naming the root cause (stale virtio-fs/readdir cache on the VM), the remedies (`claude-vm/fix-cache.sh`, `diskutil unmount`+`mount -t virtiofs`, VM restart), and explicitly state that `rm -rf obj bin` will not help. Wired into both the `./visual-relay` CLI (`build` and `check` cases) and `Directory.Build.targets` (for bare `dotnet build` coverage). Six xUnit tests cover: intact repo passes, zero-visible fails with remedy text, partial-below-threshold fails with percentage, partial-above-threshold passes, obj/bin exclusion, and .axaml coverage.\n\n### Implementation details\n\n**1. Guard script** (`tools/guards/guard-source-enumeration.sh`, 132 lines):\n- Resolves repo root from its own path (`tools/guards/` → `../..`)\n- Runs `git ls-files '*.cs' '*.axaml'` for tracked count (ground truth)\n- Runs `find src tests tools -name '*.cs'/'*.axaml' -not -path '*/bin/*' -not -path '*/obj/*'` for visible count\n- If `tracked_total == 0`: exit 0 (nothing to guard, e.g. git unavailable)\n- If `visible_total == 0`: exit 2 with full box-drawn error (zero-visible case)\n- If `visible_total / tracked_total < 0.50`: exit 2 with box-drawn error showing percentage\n- Otherwise: exit 0 silently\n- Adds ~30ms on intact repos (single git ls-files + find per invocation)\n\n**2. CLI integration** (`visual-relay`, lines 105, 132):\n- `build` case: calls `tools/guards/guard-source-enumeration.sh` before `dotnet build`\n- `check` case: calls `tools/guards/guard-source-enumeration.sh` first, then `check-file-size.sh`, then format/build/test/screenshots\n\n**3. MSBuild integration** (`Directory.Build.targets`, lines 1-19):\n- New `<Target Name=\"GuardSourceEnumeration\" BeforeTargets=\"CoreCompile\">`\n- Gated on `GuardSourceEnumeration=true` property (default true, opt-out via `-p:GuardSourceEnumeration=false`)\n- Calls the same guard script via `<Exec>` for bare `dotnet build` coverage\n\n**4. Tests** (`tests/VisualRelay.Tests/SourceEnumerationGuardTests.cs`, 243 lines):\n- `IntactRepo_GuardPasses`: temp git repo with 3 tracked+visible .cs files → exit 0, no stderr\n- `ZeroVisible_GuardFailsWithRemedy`: delete all visible files → exit 2, stderr contains \"STALE VIRTIO-FS\", \"READDIR CACHE\", \"claude-vm/fix-cache.sh\", \"diskutil unmount\", \"rm -rf obj bin\"\n- `PartialVisible_BelowThreshold_GuardFails`: 6 tracked, delete 5 → 1 visible (16%) → exit 2 with percentage\n- `PartialVisible_AboveThreshold_GuardPasses`: 4 tracked, delete 1 → 3 visible (75%) → exit 0\n- `ExcludesObjAndBinFromVisibleCount`: create obj/ and bin/ with .cs files → they are ignored, guard passes\n- `CoversAxamlFiles`: tracked .cs + .axaml, both deleted → exit 2 (not just .cs counted)\n- Uses existing `TestRepository` and `TestGit` from `TestDoubles.cs`; copies guard script into temp repo's `tools/guards/` so the script's `dirname $0` resolution walks the fixture root",
  "manifest": [
    "tools/guards/guard-source-enumeration.sh",
    "Directory.Build.targets",
    "visual-relay",
    "tests/VisualRelay.Tests/SourceEnumerationGuardTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/SourceEnumerationGuardTests.cs"],
  "rationale": "Six xUnit tests already exist and pass: (1) IntactRepo_GuardPasses — exit 0 when git-tracked == find-visible; (2) ZeroVisible_GuardFailsWithRemedy — exit 2 with STALE VIRTIO-FS, READDIR CACHE, claude-vm/fix-cache.sh, diskutil unmount, and 'rm -rf obj bin will NOT help' in stderr; (3) PartialVisible_BelowThreshold_GuardFails — 6 tracked/1 visible (16%) → exit 2 with percentage; (4) PartialVisible_AboveThreshold_GuardPasses — 4 tracked/3 visible (75%) → exit 0; (5) ExcludesObjAndBinFromVisibleCount — .cs files in obj/ and bin/ are ignored; (6) CoversAxamlFiles — .axaml files counted alongside .cs, and both deleted → exit 2. Tests use the existing TestRepository/TestGit fixtures from TestDoubles.cs, copy the guard script into temp repo's tools/guards/ so dirname resolution walks the fixture root, and run via Process.Start('bash'). All files under 300 lines (test: 243, guard: 132, Directory.Build.targets: 22, visual-relay: 145)."
}

## Stage 6 - Implement

{ "summary": "All four files already exist and are correctly implemented. The guard script (132 lines) runs silently on the intact repo (exit 0), is wired into both `./visual-relay build`/`check` and `Directory.Build.targets` for bare `dotnet build`, and 6 xUnit tests all pass (246 total tests green). No changes needed." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "Stage 7 review passed with zero issues. All four files are correctly implemented and under 300 lines: guard-source-enumeration.sh (132 lines), Directory.Build.targets (22 lines), visual-relay (145 lines), SourceEnumerationGuardTests.cs (243 lines). The guard exits 0 silently on the intact repo, is wired into both `./visual-relay build`/`check` and `Directory.Build.targets` for bare `dotnet build`, and 246 tests pass (including all 6 guard-specific tests). `./visual-relay check` is green — format clean, build 0 warnings/0 errors, test 246/246 passed, screenshots built. No blockers or warnings to resolve."}

## Stage 9 - Verify

{
  "summary": "Stage 9 is complete. A hybrid source-enumeration guard has been implemented and fully verified: a standalone bash script (`tools/guards/guard-source-enumeration.sh`, 132 lines) compares `git ls-files '*.cs' '*.axaml'` (tracked count) against `find ... -name '*.cs' '*.axaml' -not -path '*/bin/*' -not -path '*/obj/*'` (visible-on-disk count), exiting 2 with a box-drawn ASCII error naming the root cause (stale virtio-fs/readdir cache) and remedies (`claude-vm/fix-cache.sh`, `diskutil unmount`+`mount -t virtiofs`, VM restart) when visible is 0 or <50% of tracked, including the explicit warning that `rm -rf obj bin` will NOT help. The guard is wired into both `./visual-relay build`/`check` (lines 105, 132) and `Directory.Build.targets` (via `BeforeTargets=\"CoreCompile\"` MSBuild target for bare `dotnet build` coverage). Six xUnit tests (243 lines) cover intact passes, zero-visible failures with remedy text, partial-below-threshold failures with percentage, partial-above-threshold passes, obj/bin exclusion, and .axaml coverage. `./visual-relay check` is green: build 0 warnings/0 errors, 246/246 tests passed. All files are under 300 lines (132/22/145/243). The guard adds ~30ms overhead on intact repos.",
  "commitMessages": [
    "feat: add source-enumeration guard against stale virtio-fs readdir cache",
    "feat(guard): fail fast when readdir returns empty by comparing git-ls-files vs find",
    "feat: wire source-enumeration guard into CLI and Directory.Build.targets for bare dotnet build coverage",
    "test: cover source-enumeration guard with zero-visible, partial, and axaml mismatch scenarios",
    "feat: guard against silent empty assemblies from stale VM directory cache"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Stage 10 Fix-verify confirms the pinned suite is green: 246/246 tests pass (0 failures), build with 0 errors/0 warnings, ./visual-relay check green, all 4 manifest files under 300 lines (max 243). Every 'Done when' criterion satisfied — the guard compares git ls-files vs find for .cs/.axaml files, exits 2 with box-drawn ASCII error naming the root cause (stale virtio-fs/readdir cache) and remedies (claude-vm/fix-cache.sh, diskutil unmount+remount, VM restart) with explicit 'rm -rf obj bin will NOT help' warning when visible is 0 or <50% of tracked; wired into both ./visual-relay build/check and Directory.Build.targets for bare dotnet build coverage; 6 xUnit tests cover intact, zero-visible, partial-below-threshold, partial-above-threshold, obj/bin exclusion, and .axaml coverage. No failures to fix."
}

## Stage 11 - Commit

Committed by Visual Relay.

