## Stage 1 - Ideate

{
  "summary": "The macOS Foundation atomic-write path goes to the volume root's `.TemporaryItems` directory, which lies outside nono's `--allow-cwd` grant for external-volume workspaces. This causes EPERM failures in `swift build` (silent file-not-written → misleading I/O error) and `swiftformat` (logged but silently ignored by VR). The fix: add a pure path-string helper `WorkspaceVolumeTempDir` that detects `/Volumes/<vol>/` roots and returns `/Volumes/<vol>/.TemporaryItems`, then thread that as an auto-grant (`-a <dir>`) into `BuildNonoPrefix` for both the swival agent runner (`ProcessRunners.Helpers.cs`) and the verification runner (`SandboxedTestRunner.ResolveLaunch`). This is an internal-only grant — `SandboxExtraAllowPaths` validation stays unchanged.",
  "options": [
    "**Option A (prescribed — narrowest grant):** Pure path-string helper `WorkspaceVolumeTempDir(string rootPath)` that returns `/Volumes/<vol>/.TemporaryItems` when `rootPath` starts with `/Volumes/<vol>/`, else `null`. The helper goes in a new file `WorkspaceVolumeTempDir.cs` in `VisualRelay.Core/Execution/` (or alongside `ProcessRunners`). Extend `BuildNonoPrefix` in `ProcessRunners.cs` to accept an optional `string? workspaceRoot` parameter. Both callers — the swival prefix builder in `ProcessRunners.Helpers.cs` and `SandboxedTestRunner.ResolveLaunch` — pass the `targetRoot` (already available). The `-a` flag is emitted alongside existing `SandboxExtraAllowPaths` entries, before `--`. No filesystem probing, no config changes, no user-facing API changes.",
    "**Option B (broader grant — whole volume root):** Instead of granting just `.TemporaryItems`, grant the entire volume root (`/Volumes/<vol>/`). This is simpler to implement (no subpath knowledge needed) and would also cover any other volume-root paths Foundation might use in the future. However, it's a wider grant than necessary — nono's `deny_macos_private` and other deny rules still block sensitive paths under `/Volumes/<vol>/` (like `.TemporaryItems` itself only needs the subpath), so the practical risk is low. The downside is a slightly larger surface area and a less precise justification in code comments.",
    "**Option C (filesystem-probing fallback):** Same path-string helper as Option A, but add an empirical `Directory.Exists` check on the computed `.TemporaryItems` path before emitting `-a`. If the directory doesn't exist (uncommon — macOS creates it on demand), skip the grant. This trades testability (the helper is no longer pure) for a minor safety net. The task specifies nono accepts `-a` for nonexistent paths, so the probe is unnecessary; keep it pure unless empirical testing proves otherwise.",
    "**Option D (opt-in config flag):** Instead of auto-detecting, add a `RelayConfig` boolean like `AutoGrantVolumeTempDir` (default true) that controls the behavior. This lets users opt out if the grant causes issues. However, the task explicitly says 'not user config' and the prescribed approach is automatic — this option complicates the surface area for no proven benefit."
  ]
}

## Stage 2 - Research

{
  "findings": "Investigation complete. Key code paths identified:\n\n1. WorkspaceVolumeTempDir helper: new file needed at src/VisualRelay.Core/Execution/WorkspaceVolumeTempDir.cs. Pure path-string logic: if rootPath starts with `/Volumes/<vol>/` (non-empty volume name), return `/Volumes/<vol>/.TemporaryItems`; else null.\n\n2. BuildNonoPrefix (ProcessRunners.cs:152-193): currently has no workspaceRoot parameter. Needs a new optional `string? workspaceRoot = null`. The computed grant emitted as `-a <path>` alongside SandboxExtraAllowPaths and templatesDir grants, before `--`.\n\n3. Two callers that need rootPath threaded:\n   a) BuildLaunchTarget (ProcessRunners.Helpers.cs:27): called from RunAsync (ProcessRunners.RunAsync.cs:125) with invocation carrying TargetRoot. Pass invocation?.TargetRoot.\n   b) SandboxedTestRunner.ResolveLaunch (SandboxedTestRunner.cs:56): already has rootPath parameter (line 49), currently unused on Unix. Pass to BuildNonoPrefix.\n\n4. SandboxedTestRunner.RunAsync already passes rootPath to ResolveLaunch (line 24).\n\n5. Test files: SandboxedTestRunnerArgumentTests.cs (extend with external-volume/home-root tests), SwivalSubagentRunnerSandboxTests.cs (BuildNonoPrefix tests may need new cases), new test file for WorkspaceVolumeTempDir.\n\n6. RelayConfigLoader SandboxExtraAllowPaths validation (lines 134-201) rejects paths outside $HOME/workspace root. The task explicitly says not to relax this - the auto-grant bypasses it since it's internal, not user config.\n\n7. Existing hardcoded Assert.Equal tests (e.g., prefix contents) pass unchanged because test rootPath (Path.GetTempPath() or defaults) doesn't start with `/Volumes/`.",
  "constraints": [
    "macOS-only path: /Volumes/ paradigm doesn't exist on Linux/Windows — helper must return null for non-macOS platforms and paths not under /Volumes/<vol>/",
    "Do NOT modify RelayConfig or RelayConfigLoader — the volume temp grant is purely internal, not user-configurable",
    "Do NOT relax SandboxExtraAllowPaths validation — it deliberately rejects paths outside $HOME/workspace root and must stay that way",
    "The -a <path> grant is emitted only on Unix (macOS/Linux) — Windows uses MXC wrapper, not nono, and is untouched per task spec",
    "nono is expected to accept -a for nonexistent paths; if empirical testing proves otherwise, do best-effort Directory.CreateDirectory and skip on failure — never fail the run",
    "Existing hardcoded prefix-assertion tests pass unchanged because test rootPaths (Path.GetTempPath()) don't trigger the volume grant; new test cases must explicitly use /Volumes/<vol>/ rootPaths",
    "BuildNonoPrefix signature change must remain backward-compatible: new parameter with null default",
    "The -a flags must appear before --rollback/--no-rollback-prompt and before -- (child separator), maintaining existing order: SandboxExtraAllowPaths → templatesDir → VolumeTempDir → skipDirs → rollback flags → silent → --"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "BuildNonoPrefix (ProcessRunners.cs:152-193) computes all nono sandbox grants but has no workspaceRoot parameter, so it cannot detect external volumes. macOS Foundation atomic writes (NSWriteAuxiliaryFile) stage temp files in the destination volume's /.TemporaryItems directory, which lies outside --allow-cwd on external volumes — empirically confirmed 2026-07-18: `nono run --diagnostics-json` shows PolicyBlocked on file-write-create under /Volumes/Tera/.TemporaryItems/… during swift build (EPERM on output-file-map.json) and swiftformat (Failed to write file). Adding `-a /Volumes/Tera/.TemporaryItems` fixes both. Users cannot work around this via SandboxExtraAllowPaths because RelayConfigLoader:134-201 rejects paths outside $HOME/workspace root, and /Volumes/<vol>/.TemporaryItems sits at the volume root outside both. Both callers already hold the root path but don't pass it: BuildLaunchTarget (Helpers.cs:27) receives StageInvocation with .TargetRoot but passes nothing to BuildNonoPrefix; SandboxedTestRunner.ResolveLaunch (SandboxedTestRunner.cs:56) receives rootPath but ignores it on Unix. No WorkspaceVolumeTempDir helper exists anywhere in the codebase. Existing tests use Path.GetTempPath() (not under /Volumes/) so all exact-array-content assertions pass unchanged; the drift guard (NonoLaunchDriftGuardTests:36-37) also holds because the volume-temp grant would appear identically in both agent and verify prefixes for the same rootPath.",
  "excerpts": [
    "ProcessRunners.cs:152-155 — BuildNonoPrefix signature has no workspaceRoot parameter: `internal static IReadOnlyList<string> BuildNonoPrefix(RelayConfig config, bool rollback, IReadOnlyList<string>? skipDirs = null, bool verboseDiagnostics = false, string? userTemplatesDirOverride = null)`",
    "ProcessRunners.Helpers.cs:27 — swival caller discards root: `var prefix = BuildNonoPrefix(_config, rollback: true, skipDirs: skipDirs, verboseDiagnostics: _verboseDiagnostics);` — invocation?.TargetRoot is available on line 16 but not passed",
    "SandboxedTestRunner.cs:56 — verify caller discards root: `var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false, verboseDiagnostics: verboseDiagnostics);` — rootPath parameter is on line 49 but ignored on Unix",
    "SandboxedTestRunner.cs:24 — RunAsync passes rootPath to ResolveLaunch: `var (fileName, args) = ResolveLaunch(command, rootPath);` — the data is there, just not threaded into BuildNonoPrefix",
    "ProcessRunners.RunAsync.cs:125 — RunAsync passes attemptInvocation to BuildLaunchTarget: `var (fileName, launchArguments) = BuildLaunchTarget(arguments, skipDirs, attemptInvocation);` — TargetRoot available",
    "StageInvocation.cs:3-7 — record carries TargetRoot: `public sealed record StageInvocation(string TargetRoot, ...)`",
    "RelayConfigLoader.cs:166-177 — SandboxExtraAllowPaths validation rejects paths outside $HOME/workspace root, confirming users cannot add /Volumes/*/.TemporaryItems themselves",
    "llm-tasks/00-grant-workspace-volume-temp-dir-in-sandbox/00-grant-workspace-volume-temp-dir-in-sandbox.md:6-17 — empirical evidence of EPERM failure and fix with -a grant"
  ],
  "repro": "From an external-volume Swift workspace (e.g., /Volumes/Tera/dev/patternsmith):\n1. `nono run --profile <vr-guard-profile> --allow-cwd -- /bin/sh -c \"swift build --disable-sandbox\"` → fails with \"encountered an I/O error (code: 1) while reading …/output-file-map.json\" (EPERM; PolicyBlocked on /Volumes/Tera/.TemporaryItems/…)\n2. Same command with `-a /Volumes/Tera/.TemporaryItems` added before `--` → exits 0, build green\n3. `swiftformat .` without the grant → \"Failed to write file …\" on every file\n4. System-volume workspaces unaffected: `$TMPDIR` (e.g., /private/tmp) is already in the vr-guard profile's write allowlist"
}

## Stage 4 - Plan

{
  "plan": "## Summary\n\nAuto-grant `/Volumes/<vol>/.TemporaryItems` in the nono sandbox for external-volume workspaces so Foundation atomic writes (used by `swift build`, `swiftformat`, etc.) don't hit EPERM from nono's PolicyBlocked.\n\n## Changes\n\n### 1. `+src/VisualRelay.Core/Execution/WorkspaceVolumeTempDir.cs`\n\nNew static helper `WorkspaceVolumeTempDir.Resolve(string rootPath)`: pure path-string logic. On macOS, if rootPath starts with `/Volumes/<non-empty-vol>/`, returns `/Volumes/<vol>/.TemporaryItems`; otherwise returns null. On non-macOS, always returns null. No filesystem probing, no `Directory.Exists`.\n\n### 2. `src/VisualRelay.Core/Execution/ProcessRunners.cs` (line 152, BuildNonoPrefix)\n\nAdd `string? workspaceRoot = null` as a trailing optional parameter. After the templatesDir `-a` grant and before skipDirs, compute the volume temp dir via the helper and emit `-a <dir>` if non-null (macOS guard + non-null workspaceRoot).\n\n### 3. `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs` (line 27, BuildLaunchTarget)\n\nPass `invocation?.TargetRoot` as the `workspaceRoot` argument to `BuildNonoPrefix`.\n\n### 4. `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs` (line 56, ResolveLaunch)\n\nPass the existing `rootPath` parameter as the `workspaceRoot` argument to `BuildNonoPrefix`.\n\n### 5. `+tests/VisualRelay.Tests/WorkspaceVolumeTempDirTests.cs`\n\nEight red-first tests: `/Volumes/Tera/dev/x` → `/Volumes/Tera/.TemporaryItems`; `/Volumes/Tera` → same; `/Users/nick/dev/x` → null; `/private/tmp/wt` → null; trailing-slash handled; bare `/Volumes` and `/Volumes/` → null; non-macOS always returns null.\n\n### 6. `tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs`\n\nThree new tests: external-volume rootPath emits `-a /Volumes/<vol>/.TemporaryItems` in the prefix; home-rooted rootPath omits it; the grant appears before `--`.\n\n### 7. `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs`\n\nOne new test: swival prefix with external-volume rootPath includes the grant before `--rollback`.\n\n### 8. `tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs`\n\nOne new test: agent and verify prefixes built with the same workspaceRoot both carry the identical volume temp grant; non-rollback portions stay equal.\n\n## Existing test safety\n\nAll existing exact-index/content tests use the default `null` workspaceRoot and `Path.GetTempPath()` which never starts with `/Volumes/`. The new parameter is optional with null default, so every existing call site compiles and runs unchanged.",
  "manifest": [
    "+src/VisualRelay.Core/Execution/WorkspaceVolumeTempDir.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs",
    "src/VisualRelay.Core/Execution/SandboxedTestRunner.cs",
    "+tests/VisualRelay.Tests/WorkspaceVolumeTempDirTests.cs",
    "tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs",
    "tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 3, untracked deleted: 1.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/WorkspaceVolumeTempDirTests.cs",
    "tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs",
    "tests/VisualRelay.Tests/NonoLaunchDriftGuardTests.cs"
  ],
  "rationale": "All tests written red-first per the task spec. WorkspaceVolumeTempDirTests covers 8 cases (external volume, home, system temp, trailing slash, bare /Volumes, bare /Volumes/, non-macOS). SandboxedTestRunnerArgumentTests adds 4 tests for volume grant emission/omission and ordering. SwivalSubagentRunnerSandboxTests adds 1 test for grant before --rollback. NonoLaunchDriftGuardTests adds 1 test for agent/verify parity. All 44 targeted tests pass (2 expected skips). Existing tests pass unchanged because the new workspaceRoot parameter defaults to null."
}

## Stage 6 - Implement

{
  "summary": "Implemented auto-grant of workspace volume .TemporaryItems dir in nono sandbox for external macOS volumes. Added WorkspaceVolumeTempDir.cs (pure path-string helper resolving /Volumes/<vol>/.TemporaryItems from rootPath), threaded workspaceRoot param into BuildNonoPrefix (ProcessRunners.cs), passed invocation?.TargetRoot from BuildLaunchTarget (Helpers.cs) and rootPath from SandboxedTestRunner.ResolveLaunch. Added tests: WorkspaceVolumeTempDirTests.cs (8 cases), BuildNonoPrefixVolumeTempGrantTests.cs (5 cases, extracted from two existing files to stay under 300-line limit), NonoLaunchDriftGuardTests.cs (1 drift-guard test). All 3382 tests pass (0 failed, 3267 passed, 115 skipped)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure C# backend changes — path-string helper, command-line argument builder, and unit tests. Nothing visual to render: no UI, markup, styles, images, charts, documents, or terminal UI involved._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Auto-grant workspace volume .TemporaryItems dir in nono sandbox. Added `WorkspaceVolumeTempDir.Resolve()` (pure path-string helper returning `/Volumes/<vol>/.TemporaryItems` for external-volume roots, null otherwise). Threaded `workspaceRoot` parameter into `BuildNonoPrefix`, passing `invocation?.TargetRoot` from `BuildLaunchTarget` (swival agent) and `rootPath` from `SandboxedTestRunner.ResolveLaunch` (verification). All 3382 tests pass (3267 passed, 115 skipped, 0 failed).",
  "commitMessages": [
    "feat(nono): auto-grant volume .TemporaryItems for external macOS workspaces",
    "fix: grant /Volumes/<vol>/.TemporaryItems in nono to unbreak Foundation atomic writes",
    "feat: add WorkspaceVolumeTempDir helper and thread into BuildNonoPrefix for external volumes",
    "fix(sandbox): emit -a .TemporaryItems grant when workspace is on an external macOS volume",
    "feat: compute and inject volume-root temp grant for swival and verify sandbox prefixes"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

