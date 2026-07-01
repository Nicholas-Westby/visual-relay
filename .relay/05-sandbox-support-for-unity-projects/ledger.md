## Stage 1 - Ideate

{
  "summary": "Add sandbox path grants for Unity's global cache and license directories on macOS (nono profile vr-guard.json: 3 paths in filesystem.allow + 'Unity' in rollback.exclude_patterns) and Windows (MxcPolicyGenerator: C:\\ProgramData\\Unity). Add corresponding structure and rollback tests. Follow existing per-toolchain conventions ($HOME-relative, 'when: macos' predicates, existence-filtered on Windows). Leave Preferences read-only as acceptable for CI/batch.",
  "options": [
    "Option 1 — Strict compliance: exactly the 3 macOS paths, 1 Windows path, 1 rollback exclusion, and matching tests as specified. Follows existing convention, minimal diff, safe.",
    "Option 2 — Wildcard macOS Application Support: consolidate Unity and UnityHub into a single 'Unity*' glob entry. Fewer entries but breaks pattern of explicit per-path grants used by other toolchains.",
    "Option 3 — Also grant Unity-specific Preferences write: add write for ~/Library/Preferences/com.unity3d.*.plist. More complete Unity UX but deviates from spec's explicit non-goal; acceptable for a future follow-up."
  ]
}

## Stage 2 - Research

{
  "findings": "## Codebase Investigation Findings\n\n### 1. nono profile (`packaging/nono/vr-guard.json`)\n- Current `filesystem.allow` has 29 entries, some with `\"when\": \"macos\"` or `\"when\": \"linux\"` predicates, others unqualified (all-OS).\n- Uses `$HOME` variable expansion — never `/Users/...` (enforced by `VrGuardProfile_HasFilesystemAllowEntries` test).\n- `filesystem.read` is `[\"/\", \"$HOME/.gitconfig\"]` — reads are global.\n- `rollback.exclude_patterns` has 21 entries (toolchain dirs like `.nuget`, `.cache`, `Caches`, `.cargo`, etc.).\n- No Unity entries exist today in either `filesystem.allow` or `rollback.exclude_patterns`.\n- File is 103 lines (well under 300-line guard).\n\n### 2. MXC Policy Generator (`src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs`)\n- `DefaultWindowsCacheDirs()` returns 7 paths today: `%LOCALAPPDATA%`, `%APPDATA%`, `~/.nuget/packages`, `~/.dotnet`, `~/.cargo`, `~/.config/swival`, `%TEMP%`.\n- **Critically filtered** by `.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))` — missing dirs are silently dropped.\n- 71 lines total.\n\n### 3. Test files\n- **`NonoProfileStructureTests.cs`** (174 lines): Has methods `VrGuardProfile_HasDotNetEntries`, `VrGuardProfile_HasSwiftEntries`, `VrGuardProfile_HasNixEntries`, `VrGuardProfile_GrantsCargoHome_NotJustRegistryAndGit`, plus structural checks. Uses `CollectPaths(JsonElement)` helper to extract path strings from the `filesystem.allow` array.\n- **`VrGuardProfileRollbackTests.cs`** (44 lines): `VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback` asserts `.nuget`, `.cache`, `Caches`, `.bun`, `.npm`, `.dotnet`, `.cargo`, `node_modules` exist in `rollback.exclude_patterns`. Uses `RepoSetup.Root` for profile path resolution.\n- **`WindowsSandboxTests.cs`** (121 lines): `DefaultWindowsCacheDirs_ReturnsOnlyExistingDirs` is gated to Windows `Assert.SkipUnless(OperatingSystem.IsWindows())`.\n- **`MxcRealSandboxTests.cs`** (77 lines): End-to-end Windows MXC test, gated to Windows.\n\n### 4. Dynamic rollback skip (`NonoRollbackSkipDirs.cs`)\n- Already handles workspace-local Unity dirs (`Library/`, `Temp/`) via git-ignored + ≥256 MB size gate — no changes needed.\n- 216 lines (well under limit).\n\n### 5. Embedded resource\n- `VisualRelay.Core.csproj` line 13: `<EmbeddedResource Include=\"..\\..\\packaging\\nono\\vr-guard.json\" />` — confirms single-file authoring; rebuilding `VisualRelay.Core` picks up changes.\n\n### 6. Key conventions observed\n- Test method naming: `VrGuardProfile_Has<Feature>` for structure tests, `VrGuardProfile_<Condition>` for rollback tests.\n- OS-specific paths use `\"when\": \"macos\"` / `\"when\": \"linux\"` object entries.\n- Non-OS-specific paths are plain strings.\n- Tests use `CollectPaths()` helper to flatten allow entries into path strings for assertions.\n- `$HOME` references — never hardcoded `/Users/` paths.\n- Tests use `RepoSetup.Root` for profile path resolution.",
  "constraints": [
    "No hardcoded `/Users/` paths in vr-guard.json — test `VrGuardProfile_HasFilesystemAllowEntries` enforces `$HOME`-relative paths.",
    "OS-specific paths must use `\"when\": \"macos\"` predicate — `VrGuardProfile_HasWhenPredicatesForOsSpecificPaths` requires at least one such entry.",
    "Linux Unity support is explicitly out of scope — do not add `\"when\": \"linux\"` Unity paths.",
    "`rollback.exclude_patterns` must NOT include `\"Library\"` — that would match `~/Library` broadly and disable rollback for the whole macOS Library tree, plus match Unity's workspace `Library/` dir.",
    "`rollback.exclude_patterns` must include `\"Unity\"` as a new entry — matches `~/Library/Unity`, `~/Library/Application Support/Unity`, `~/Library/Application Support/UnityHub` without matching `~/Library` or `com.unity3d.*` caches (already excluded by `\"Caches\"`).",
    "MXC `DefaultWindowsCacheDirs()` additions must go *before* the `Directory.Exists(d)` filter so missing dirs (Unity not installed) are silently dropped — mirrors existing `~/.cargo` handling.",
    "Do NOT add bare `%PROGRAMDATA%` to MXC — only `Path.Combine(CommonApplicationData, \"Unity\")`.",
    "Do NOT add `~/Library/Preferences` write grant — the read-only limitation for CI/batch runs is acceptable and explicitly out of scope (leave a comment only).",
    "Do NOT add `~/Library/Caches/*` or `~/Library/Logs/*` — already granted by `user_caches_macos` group pulled by swival.",
    "Every new/edited `*.cs` file must be ≤ 300 lines (VR's file-size guard).",
    "All existing tests must continue to pass — do not weaken, skip, or delete any test.",
    "Full `dotnet test`/Verify gate must be green (`Failed: 0`, exit 0).",
    "New test `VrGuardProfile_HasUnityEntries` must follow the same shape as `HasDotNetEntries`/`HasSwiftEntries` using `CollectPaths` helper.",
    "Existing test `VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback` must be extended to require `\"Unity\"` in exclude_patterns.",
    "Windows `DefaultWindowsCacheDirs_ReturnsOnlyExistingDirs` (or similar existing test) must still pass with the new `ProgramData\\Unity` path — existence-filter keeps Unity-less hosts clean.",
    "Changes must be safe and inert for non-Unity projects — extra allow entries only matter when Unity actually runs."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "## Changes Made (4 files, 0 test failures expected)\n\n### 1. `packaging/nono/vr-guard.json` — macOS Unity write grants + rollback exclusion\n\n**`filesystem.allow` (lines 47-49):** Added three macOS-only entries following existing per-toolchain convention:\n- `{ \"path\": \"$HOME/Library/Unity\", \"when\": \"macos\" }` — UPM global package cache + Asset Store-5.x\n- `{ \"path\": \"$HOME/Library/Application Support/Unity\", \"when\": \"macos\" }` — licensing client state / .ulf\n- `{ \"path\": \"$HOME/Library/Application Support/UnityHub\", \"when\": \"macos\" }` — Unity Hub editor resolution\n\nAll three use `$HOME`-relative paths with `\"when\": \"macos\"` predicates (no hardcoded `/Users/` — verified via grep: zero matches). No Linux Unity paths added. No `~/Library/Preferences` write grant (read-only limitation acceptable for CI/batch; left as-is from `user_caches_macos` group).\n\n**`rollback.exclude_patterns` (line 70):** Added `\"Unity\"` between `.templateengine` and `.swiftpm`. This matches `~/Library/Unity`, `~/Library/Application Support/Unity`, `~/Library/Application Support/UnityHub` without matching `~/Library` or the lowercase `com.unity3d.*` caches (already excluded by existing `\"Caches\"` pattern). DID NOT add `\"Library\"` (the landmine) — grep confirms no bare `\"Library\"` in exclude_patterns.\n\n### 2. `src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs` — Windows `C:\\ProgramData\\Unity`\n\nLine 66: Added `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), \"Unity\")` to the `dirs` list **before** the `.Where(d => … && Directory.Exists(d))` filter. On a host without Unity, `C:\\ProgramData\\Unity` won't exist and is silently dropped — same pattern as `~/.cargo` handling. Did NOT add bare `%PROGRAMDATA%`.\n\n### 3. `tests/VisualRelay.Tests/NonoProfileStructureTests.cs` — new `VrGuardProfile_HasUnityEntries` test\n\nLines 85-97: Added test following exact same shape as `HasDotNetEntries`/`HasSwiftEntries`/`HasNixEntries`. Uses `CollectPaths` helper to extract path strings, then asserts exact match for all three Unity paths. File is 188 lines (under 300-line guard).\n\n### 4. `tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs` — extended rollback assertion\n\nLine 35: Added `\"Unity\"` to the required patterns array in `VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback`.\n\n### 5. `src/VisualRelay.Core/Execution/NonoRollbackSkipDirs.cs` — verified, no change needed\n\nAlready handles workspace-local Unity dirs (`Library/`, `Temp/`) via the ecosystem-agnostic git-ignored + ≥256 MB size gate. No Unity-specific code needed.\n\n## Constraint verification\n- No `/Users/` paths: grep returns zero matches ✅\n- All OS-specific paths use `\"when\"` predicates ✅\n- `$HOME`-relative paths only ✅\n- `\"Unity\"` in exclude_patterns, no `\"Library\"` ✅\n- MXC addition before `Directory.Exists` filter ✅\n- No bare `%PROGRAMDATA%` ✅\n- No Linux Unity paths ✅\n- All .cs files under 300 lines (NonoProfileStructureTests.cs: 188, VrGuardProfileRollbackTests.cs: 44, MxcPolicyGenerator.cs: 72) ✅\n- Existing tests preserved — no deletions, no skips, no weakening ✅",
  "excerpts": [
    "packaging/nono/vr-guard.json:47-49: { \"path\": \"$HOME/Library/Unity\", \"when\": \"macos\" }, { \"path\": \"$HOME/Library/Application Support/Unity\", \"when\": \"macos\" }, { \"path\": \"$HOME/Library/Application Support/UnityHub\", \"when\": \"macos\" }",
    "packaging/nono/vr-guard.json:70: \"Unity\"",
    "src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs:66: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), \"Unity\")",
    "tests/VisualRelay.Tests/NonoProfileStructureTests.cs:85-97: VrGuardProfile_HasUnityEntries test with exact-match assertions for all three paths",
    "tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs:35: \"Unity\" added to required rollback exclusions array",
    "grep /Users/ packaging/nono/vr-guard.json: zero matches (no hardcoded paths)",
    "grep '\"Library\"' packaging/nono/vr-guard.json: zero matches (landmine avoided)"
  ],
  "repro": "dotnet test VisualRelay.slnx --filter \"FullyQualifiedName~NonoProfileStructureTests|FullyQualifiedName~VrGuardProfileRollbackTests|FullyQualifiedName~WindowsSandboxTests\""
}

## Stage 4 - Plan

{
  "plan": "## Concrete Implementation Plan\n\n### 1. `packaging/nono/vr-guard.json` — macOS Unity grants + rollback exclusion\n\n**`filesystem.allow` (insert 3 entries before the closing `]` on line 49):**\nAdd three macOS-only write grants, grouped near the existing per-toolchain blocks (after the nix entries at line 45):\n\n```json\n{ \"path\": \"$HOME/Library/Unity\", \"when\": \"macos\" },\n{ \"path\": \"$HOME/Library/Application Support/Unity\", \"when\": \"macos\" },\n{ \"path\": \"$HOME/Library/Application Support/UnityHub\", \"when\": \"macos\" }\n```\n\nAll use `$HOME`-relative paths and `\"when\": \"macos\"` predicates. No `/Users/` hardcoding. No `\"when\": \"linux\"` entries (Linux Unity out of scope). No `~/Library/Preferences` write grant (read-only limitation acceptable for CI/batch; existing `user_caches_macos` group already provides read-only). A comment noting the Preferences limitation is optional.\n\n**`rollback.exclude_patterns` (add `\"Unity\"` between `.templateengine` and `.swiftpm`):**\nAdd the string `\"Unity\"` to the array. This matches `~/Library/Unity`, `~/Library/Application Support/Unity`, and `~/Library/Application Support/UnityHub` without matching `~/Library` (which would disable rollback for the entire macOS Library tree) or the workspace `Library/` dir. The existing `\"Caches\"` pattern already excludes lowercase `com.unity3d.*` caches. Do NOT add `\"Library\"`.\n\n---\n\n### 2. `src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs` — Windows ProgramData\\Unity\n\n**`DefaultWindowsCacheDirs()` method (add one line before the `Path.GetTempPath()` entry):**\nInsert:\n```csharp\nPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), \"Unity\"),\n```\nThis resolves to `C:\\ProgramData\\Unity` on standard Windows installations. Placed *before* the `.Where(d => … && Directory.Exists(d))` filter so a host without Unity simply drops the missing directory — same pattern as `~/.cargo`. Do NOT add bare `%PROGRAMDATA%`.\n\n---\n\n### 3. `tests/VisualRelay.Tests/NonoProfileStructureTests.cs` — new `VrGuardProfile_HasUnityEntries`\n\nAdd a new `[Fact]` method following the exact shape of `VrGuardProfile_HasDotNetEntries` / `VrGuardProfile_HasSwiftEntries` / `VrGuardProfile_HasNixEntries`:\n- Parse the profile, get `filesystem.allow`, call `CollectPaths(allow)`.\n- Assert `paths` contains exact-match `\"$HOME/Library/Unity\"`, `\"$HOME/Library/Application Support/Unity\"`, and `\"$HOME/Library/Application Support/UnityHub\"`.\n\nThis test ensures the profile is not regressed. The existing `VrGuardProfile_HasFilesystemAllowEntries` (no `/Users/` hardcoding) and `VrGuardProfile_HasWhenPredicatesForOsSpecificPaths` (requires at least one `\"when\"` entry) continue to pass.\n\n---\n\n### 4. `tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs` — extend exclusion assertion\n\n**`VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback`:**\nAdd `\"Unity\"` to the `required` array on line 35, alongside the existing `.nuget`, `.cache`, `Caches`, `.bun`, `.npm`, `.dotnet`, `.cargo` entries.\n\n---\n\n### 5. `src/VisualRelay.Core/Execution/NonoRollbackSkipDirs.cs` — verify only, no change\n\nThe existing ecosystem-agnostic mechanism (git-ignored top-level dirs ≥256 MB) already skips Unity workspace `Library/` and `Temp/` directories. No Unity-specific code needed. Confirm and move on.\n\n---\n\n### 6. `tests/VisualRelay.Tests/WindowsSandboxTests.cs` — verify only, no change\n\nThe existing `DefaultWindowsCacheDirs_ReturnsOnlyExistingDirs` test (gated to `OperatingSystem.IsWindows()`) already validates that every returned directory exists. The new `ProgramData\\Unity` path is automatically covered: on a Unity-less host it won't exist and is filtered; on a Unity host it exists and is returned. No test changes needed.\n\n---\n\n## Constraint checklist\n- ✅ No `/Users/` hardcoded paths (enforced by existing test)\n- ✅ All OS-specific paths use `\"when\": \"macos\"` predicates\n- ✅ `$HOME`-relative paths only\n- ✅ `\"Unity\"` in `rollback.exclude_patterns`, no `\"Library\"`\n- ✅ MXC addition before `Directory.Exists` filter\n- ✅ No bare `%PROGRAMDATA%`\n- ✅ No Linux Unity paths\n- ✅ All `.cs` files under 300 lines\n- ✅ No existing tests weakened, skipped, or deleted\n- ✅ Changes inert for non-Unity projects",
  "manifest": [
    "packaging/nono/vr-guard.json",
    "src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs",
    "tests/VisualRelay.Tests/NonoProfileStructureTests.cs",
    "tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 2.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/NonoProfileStructureTests.cs",
    "tests/VisualRelay.Tests/VrGuardProfileRollbackTests.cs"
  ],
  "rationale": "Two test files were modified: (1) NonoProfileStructureTests.cs gained VrGuardProfile_HasUnityEntries — a [Fact] mirroring HasDotNetEntries/HasSwiftEntries/HasNixEntries that uses CollectPaths to assert exact-match presence of the three Unity macOS paths ($HOME/Library/Unity, $HOME/Library/Application Support/Unity, $HOME/Library/Application Support/UnityHub) in filesystem.allow. (2) VrGuardProfileRollbackTests.cs was extended with 'Unity' added to the required exclude_patterns array in VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback, enforcing nono rollback skips the UPM cache and license/hub dirs. WindowsSandboxTests.cs needs no changes — DefaultWindowsCacheDirs_ReturnsOnlyExistingDirs already validates all returned dirs, covering the new ProgramData\\Unity path on Windows hosts with Unity installed (and silently dropping it on hosts without)."
}

## Stage 6 - Implement

{ "summary": "Added Unity sandbox path grants: 3 macOS write entries in vr-guard.json filesystem.allow ($HOME/Library/Unity, ~/Application Support/Unity, ~/Application Support/UnityHub), 'Unity' rollback exclusion, and C:\\ProgramData\\Unity in MxcPolicyGenerator. All 10 targeted tests pass, exit 0." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All four files already contained the required changes from prior stages. Verified with targeted test run: 10 passed, 0 failed. vr-guard.json has the 3 macOS Unity write grants (lines 46-48) with $HOME-relative paths and when:macos predicates, plus 'Unity' in rollback.exclude_patterns (line 69). MxcPolicyGenerator.cs has ProgramData\\Unity before the Directory.Exists filter (line 66). NonoProfileStructureTests.cs has VrGuardProfile_HasUnityEntries (lines 85-97). VrGuardProfileRollbackTests.cs includes 'Unity' in required exclusions (line 35). No issues found." }

## Stage 9 - Verify

{
  "summary": "Add sandbox path grants for Unity project global cache and license directories: macOS (nono) — 3 $HOME-relative write entries in vr-guard.json filesystem.allow ($HOME/Library/Unity, $HOME/Library/Application Support/Unity, $HOME/Library/Application Support/UnityHub) with \"when\": \"macos\" predicates plus \"Unity\" in rollback.exclude_patterns; Windows (MXC) — C:\\ProgramData\\Unity in DefaultWindowsCacheDirs() before the Directory.Exists filter; plus matching structure and rollback tests. Full test suite green (Failed: 0, Passed: 2464).",
  "commitMessages": [
    "feat(sandbox): grant Unity cache and license paths on macOS and Windows",
    "feat(nono): add macOS Unity write grants and rollback exclusion",
    "feat(mxc): grant C:\\ProgramData\\Unity directory for license files"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

