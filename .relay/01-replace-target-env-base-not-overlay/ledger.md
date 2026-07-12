## Stage 1 - Ideate

{
  "summary": "Replace the target-command env base instead of overlaying it: when a valid user snapshot exists, the child process environment must be exactly `snapshot ∪ VR overrides` — nothing inherited from the devshell leaks through. Three options: (A) return a `TargetCommandEnvironment(Overrides, Remove)` record from `BuildTargetCommandEnvironment` and thread `Remove` through existing `envRemove` param in `ProcessCapture.RunAsync`; (B) build a complete replacement dictionary and set `ProcessStartInfo.Environment` directly; (C) compute the removal diff at each call site. Option A is preferred (uses existing mechanism, clean semantics, zero change for no-snapshot path).",
  "options": [
    "Option A — Return `TargetCommandEnvironment` record with `Overrides` + `Remove` set, thread `Remove` through `envRemove` parameter in `ProcessCapture.RunAsync`",
    "Option B — Build complete replacement environment dictionary and use `ProcessStartInfo.Environment` (full replacement) instead of `EnvironmentVariables` (overlay)",
    "Option C — Two-phase: capture process env baseline, compute removal diff at each call site, pass to `envRemove`"
  ]
}

## Stage 2 - Research

{
  "findings": "**Codebase investigation for task `01-replace-target-env-base-not-overlay`**\n\n**Files to modify (source):**\n1. **`src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs`** (65 lines) — Contains `BuildSandboxEnvironment(RelayConfig)` (returns `IReadOnlyDictionary<string, string>`) and `BuildTargetCommandEnvironment(RelayConfig, IEnvironmentAccessor?)` (returns `IReadOnlyDictionary<string, string>`). The latter merges snapshot→VR overrides or falls back to sandbox-only. Need to add `TargetCommandEnvironment(Overrides, Remove)` record and change return type. For testability, add optional `processEnv` param without widening `IEnvironmentAccessor` (which is single-key only). Semantics: valid snapshot = snapshot has `PATH` key; `Remove` = processEnv keys not in Overrides; no snapshot → Remove empty.\n\n2. **`src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`** (286 lines) — Line 127: `var sandboxEnv = BuildTargetCommandEnvironment(_config);` then passed as `environment:` to `ProcessCapture.RunAsync` (line 132-136). Must destructure record and pass `envRemove:` alongside existing `environment:`.\n\n3. **`src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`** (106 lines) — Line 25: `var env = SwivalSubagentRunner.BuildTargetCommandEnvironment(config);` then passed to `RunWatchedAsync` (line 33-39). Must destructure and thread removal set.\n\n4. **`src/VisualRelay.Core/Execution/SandboxedTestRunner.Watched.cs`** (126 lines) — `RunWatchedAsync` signature (line 83) takes `IReadOnlyDictionary<string, string>? environment`. Need to add `IReadOnlySet<string>? envRemove` parameter and pass to `ProcessCapture.RunAsync` at line 102-105.\n\n**No changes needed to:**\n5. **`src/VisualRelay.Core/Execution/ProcessCapture.cs`** (300 lines) — Already has `envRemove` parameter (lines 52, 72, 92) and removal logic (lines 104-110) that runs before `environment` is applied. `StripLeakedNixSdkEnv` remains unchanged — it stays as defense for no-snapshot path.\n\n**Files to modify (tests):**\n6. **`tests/VisualRelay.Tests/UserEnvSnapshotTests.cs`** (195 lines) — 7 facts (3 Load_*, 3 BuildTargetCommandEnvironment_*, 1 Dispose). Adapt assertions to new `TargetCommandEnvironment` record shape. Add tests for: devshell-only key in `Remove`, snapshot key not in `Remove`, VR override keys absent from snapshot not in `Remove`, no-snapshot → Remove empty, snapshot-without-PATH → invalid/fallback.\n\n7. **`tests/VisualRelay.Tests/SandboxEnvForwardingTests.cs`** (93 lines) — 3 facts. Add child-process integration test: set marker var in test process, spawn child via `ProcessCapture.RunAsync` with `envRemove` containing that marker, assert child does not see it, but env-passed key still arrives.\n\n**Fact-count ratchet:** Baseline is 175 (line 184 in `SplitGuardVerificationTests.cs`). Neither `UserEnvSnapshotTests` nor `SandboxEnvForwardingTests` are in the oversized prefixes list (lines 186-231), so no ratchet bump needed.\n\n**Test doubles:** `DictionaryEnvironmentAccessor` in `tests/VisualRelay.Tests/TestDoubles.cs` implements `IEnvironmentAccessor` (single-key getter). `SandboxOn()` helper returns a `RelayConfig` with sandbox enabled — same pattern used in both test files.",
  "constraints": [
    "`dotnet build VisualRelay.slnx` must succeed; all existing tests must pass.",
    "Packaged/brew installs (no snapshot) must behave byte-for-byte as before — Remove must be empty set, Overrides must equal `BuildSandboxEnvironment` output.",
    "Do NOT change `BuildSandboxEnvironment`'s contents, `StripLeakedNixSdkEnv`, or the bootstrap script.",
    "Do NOT widen `IEnvironmentAccessor` interface (it stays a single-key getter).",
    "If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove tests to satisfy it. Current baseline: 175 (SplitGuardVerificationTests.cs line 184).",
    "The `envRemove` mechanism in `ProcessCapture.RunAsync` removes keys before applying `environment` overrides — this ordering must be preserved (the fix relies on it).",
    "Valid snapshot = non-null AND contains a `PATH` key. A snapshot without `PATH` is treated as invalid (same as absent).",
    "`Remove` by construction excludes all keys present in `Overrides` — no key can appear in both.",
    "Executable filename resolution (nono, swival) happens in the parent process against the parent's PATH before child env applies — removing devshell-only keys from child env is safe and cannot affect binary discovery."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The root cause is in `ProcessRunners.SandboxEnv.cs:52-64`: `BuildTargetCommandEnvironment` returns only an `IReadOnlyDictionary<string, string>` of overrides — no removal set. When a valid user snapshot exists, it merges snapshot entries with VR sandbox overrides and returns the combined dictionary. Both call sites (`ProcessRunners.RunAsync.cs:127` and `SandboxedTestRunner.cs:25`) pass this dictionary as the `environment:` parameter to `ProcessCapture.RunAsync`, which applies it onto `ProcessStartInfo.EnvironmentVariables`. Because `EnvironmentVariables` inherits the full parent process environment, any devshell-only key (e.g. `DOTNET_ROOT`) that is absent from the snapshot leaks into the child. The `envRemove` mechanism in `ProcessCapture.cs:104-110` already exists and removes keys BEFORE applying overrides — but neither call site passes an `envRemove:` argument. `StripLeakedNixSdkEnv` (`ProcessCapture.cs:18,29-41`) only strips `DEVELOPER_DIR`/`SDKROOT` and does not cover `DOTNET_ROOT` or any other nix-injected variable. The fix is to change `BuildTargetCommandEnvironment` to return a `TargetCommandEnvironment(Overrides, Remove)` record, compute `Remove` as every process-env key not present in `Overrides` when a valid snapshot exists, and thread `Remove` through both call sites into the existing `envRemove` parameter.",
  "excerpts": [
    "ProcessRunners.SandboxEnv.cs:52-64 — BuildTargetCommandEnvironment returns only IReadOnlyDictionary<string, string> (no removal set):\n```csharp\ninternal static IReadOnlyDictionary<string, string> BuildTargetCommandEnvironment(\n    RelayConfig config, IEnvironmentAccessor? accessor = null)\n{\n    var snapshot = UserEnvSnapshot.Load(accessor);\n    if (snapshot is null)\n        return BuildSandboxEnvironment(config);\n\n    var merged = new Dictionary<string, string>(snapshot);\n    foreach (var kvp in BuildSandboxEnvironment(config))\n        merged[kvp.Key] = kvp.Value;\n\n    return merged;\n}\n```",
    "ProcessCapture.cs:104-117 — envRemove mechanism exists (removes before applying) but is never used by target-command call sites:\n```csharp\nif (envRemove is not null)\n{\n    foreach (var key in envRemove)\n    {\n        process.StartInfo.EnvironmentVariables.Remove(key);\n    }\n}\nif (environment is not null)\n{\n    foreach (var kvp in environment)\n    {\n        process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;\n    }\n}\n```",
    "ProcessCapture.cs:18,29-41 — StripLeakedNixSdkEnv only covers two hardcoded keys, not DOTNET_ROOT:\n```csharp\nprivate static readonly string[] LeakedAppleSdkEnvNames = [\"DEVELOPER_DIR\", \"SDKROOT\"];\n```",
    "ProcessRunners.RunAsync.cs:127-136 — Call site 1 (swival stage): passes environment: with no envRemove:\n```csharp\nvar sandboxEnv = BuildTargetCommandEnvironment(_config);\n// ...\nvar processTask = ProcessCapture.RunAsync(fileName, launchArguments, attemptInvocation.TargetRoot,\n    processTimeout, cancellationToken, environment: sandboxEnv, killToken: watchdogCts.Token,\n    onActivity: watchdog.Pulse, cpuSampleIntervalMs: CpuPulseSampleIntervalMs,\n    onWedgeSample: watchdog.RecordWedgeSample,\n    socketProbe: BackendSocketProbe.HasEstablishedBackendConnection, timeProvider: _timeProvider);\n```",
    "SandboxedTestRunner.cs:25-39 — Call site 2 (verify path): passes env through RunWatchedAsync, no envRemove:\n```csharp\nvar env = SwivalSubagentRunner.BuildTargetCommandEnvironment(config);\nreturn await RunWatchedAsync(\n    fileName, args, rootPath, env,\n    firstOutputTimeoutMs: config.TestIdleGraceMilliseconds,\n    idleGraceMs: config.TestIdleGraceMilliseconds,\n    hardCap: _timeout,\n    cpuSampleIntervalMs: CpuPulseSampleIntervalMs,\n    cancellationToken, _timeProvider);\n```",
    "SandboxedTestRunner.Watched.cs:83-105 — RunWatchedAsync takes IReadOnlyDictionary<string, string>? environment but no envRemove, passes only environment: to ProcessCapture:\n```csharp\ninternal static async Task<TestRunResult> RunWatchedAsync(\n    string fileName, IReadOnlyList<string> args, string rootPath,\n    IReadOnlyDictionary<string, string>? environment,\n    int firstOutputTimeoutMs, int idleGraceMs, TimeSpan hardCap,\n    int cpuSampleIntervalMs, CancellationToken cancellationToken, TimeProvider? timeProvider = null)\n{\n    // ...\n    var processTask = ProcessCapture.RunAsync(\n        fileName, args, rootPath, hardCap, cancellationToken,\n        environment: environment, killToken: watchdogCts.Token,\n        onActivity: watchdog.Pulse, cpuSampleIntervalMs: cpuSampleIntervalMs, timeProvider: tp);\n```",
    "IEnvironmentAccessor.cs:9-16 — Single-key getter, stays unchanged:\n```csharp\npublic interface IEnvironmentAccessor\n{\n    string? GetEnvironmentVariable(string name);\n}\n```",
    "SplitGuardVerificationTests.cs:184 — Fact-count ratchet baseline is 175; UserEnvSnapshotTests and SandboxEnvForwardingTests are NOT in the tracked prefix list (lines 186-231), so no ratchet bump needed for new facts."
  ],
  "repro": "On macOS with nix devshell exporting DOTNET_ROOT:\n1. Ensure VISUAL_RELAY_USER_ENV_SNAPSHOT points to a user snapshot that lacks DOTNET_ROOT.\n2. Configure a repo with guardCmd = \"dotnet format VisualRelay.slnx --verify-no-changes\".\n3. Run a relay stage. The child inherits DOTNET_ROOT=/nix/store/...-dotnet-sdk-10.0.301/share/dotnet from the nix devshell (not in snapshot → not in the override dict → survives inheritance), while PATH from the snapshot resolves the host dotnet (10.0.300).\n4. The child crashes: System.IO.FileNotFoundException: Could not load file or assembly 'System.Composition.AttributedModel, Version=10.0.0.9' — 10.0.301's analyzers loaded into 10.0.300's runtime.\n\nMinimal repro outside relay:\n  DOTNET_ROOT=/nix/store/...-dotnet-sdk-10.0.301/share/dotnet dotnet format SomeSln.slnx --verify-no-changes  # crashes\n  dotnet format SomeSln.slnx --verify-no-changes  # exit 0"
}

## Stage 4 - Plan

{
  "plan": "## Implementation Plan: Replace target-command env base instead of overlaying\n\n### 1. `src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs`\n\n**Add `TargetCommandEnvironment` record** after the `using` block and before `public sealed partial class SwivalSubagentRunner`:\n\n```csharp\ninternal sealed record TargetCommandEnvironment(\n    IReadOnlyDictionary<string, string> Overrides,\n    IReadOnlySet<string> Remove);\n```\n\n**Change `BuildTargetCommandEnvironment`** signature to return `TargetCommandEnvironment` and add optional `processEnv` parameter. New semantics:\n\n- When `processEnv` is null, snapshot the real `Environment.GetEnvironmentVariables()` into `IReadOnlyDictionary<string,string>` via a private helper (cast each `DictionaryEntry` key/value to string).\n- When snapshot is null OR snapshot lacks a `PATH` key: return `new TargetCommandEnvironment(BuildSandboxEnvironment(config), new HashSet<string>())` — byte-identical to today's fallback.\n- When valid snapshot exists: merge snapshot entries, then `BuildSandboxEnvironment(config)` entries on top (VR wins). Compute `Remove` = every key in `processEnv` that is NOT a key in `merged`. Return `new TargetCommandEnvironment(merged, remove)`.\n\n**Add private helper** `SnapshotProcessEnv()` that calls `Environment.GetEnvironmentVariables()` and builds `Dictionary<string,string>`.\n\n### 2. `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`\n\nAt line 127, change:\n```csharp\nvar sandboxEnv = BuildTargetCommandEnvironment(_config);\n```\nto:\n```csharp\nvar targetEnv = BuildTargetCommandEnvironment(_config);\n```\n\nIn the `ProcessCapture.RunAsync` call (lines 132–136), change `environment: sandboxEnv` to `environment: targetEnv.Overrides` and add `envRemove: targetEnv.Remove` as the next named argument (before `killToken:`).\n\n### 3. `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`\n\nAt line 25, change:\n```csharp\nvar env = SwivalSubagentRunner.BuildTargetCommandEnvironment(config);\n```\nto:\n```csharp\nvar targetEnv = SwivalSubagentRunner.BuildTargetCommandEnvironment(config);\n```\n\nIn the `RunWatchedAsync` call (lines 33–39), change `env` to `targetEnv.Overrides` and add `envRemove: targetEnv.Remove` as a new named argument.\n\n### 4. `src/VisualRelay.Core/Execution/SandboxedTestRunner.Watched.cs`\n\nAdd `IReadOnlySet<string>? envRemove = null` parameter to `RunWatchedAsync` (line 87, after `environment`). At the `ProcessCapture.RunAsync` call (lines 102–105), add `envRemove: envRemove` as a named argument (after `environment:`).\n\n### 5. `tests/VisualRelay.Tests/UserEnvSnapshotTests.cs`\n\n**Adapt existing `BuildTargetCommandEnvironment_*` facts** (lines 86–154) to destructure `.Overrides` from the new `TargetCommandEnvironment` return type. All assertions on individual key/value pairs remain identical; only the variable declaration changes. Example: `var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);` becomes `var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env).Overrides;`.\n\n**Add five new facts:**\n\n1. **`BuildTargetCommandEnvironment_DevshellOnlyKey_IsInRemove`**: Provide a `processEnv` dict containing `DOTNET_ROOT=/nix/store/x` and a snapshot without `DOTNET_ROOT`. Assert `Remove` contains `DOTNET_ROOT` and `Overrides` does not.\n\n2. **`BuildTargetCommandEnvironment_SnapshotKey_NotInRemove`**: Provide a `processEnv` where key exists in both snapshot and process env. Assert the key is NOT in `Remove` and its snapshot value is in `Overrides`.\n\n3. **`BuildTargetCommandEnvironment_VrOverrideKeys_NeverInRemove`**: With a valid snapshot that lacks VR override keys (e.g. `PYTHONDONTWRITEBYTECODE`), assert those keys are present in `Overrides` and absent from `Remove`.\n\n4. **`BuildTargetCommandEnvironment_NoSnapshot_RemoveEmpty`**: Without setting `VISUAL_RELAY_USER_ENV_SNAPSHOT`, assert `Remove` is empty and `Overrides` equals `BuildSandboxEnvironment` output.\n\n5. **`BuildTargetCommandEnvironment_SnapshotWithoutPath_TreatedAsInvalid`**: Write a snapshot that has `HOME` but NO `PATH` key. Assert `Remove` is empty (fallback behavior) and `Overrides` equals `BuildSandboxEnvironment` output.\n\n### 6. `tests/VisualRelay.Tests/SandboxEnvForwardingTests.cs`\n\n**Add integration test `ProcessCapture_EnvRemove_StripsMarkerFromChild`**: On POSIX only (skip Windows), set a unique marker variable in the current process (via `Environment.SetEnvironmentVariable`), spawn a child via `ProcessCapture.RunAsync` with `envRemove` containing that marker, and assert the child does NOT see it. Also pass a second key via `environment:` and assert the child DOES see that one. Clean up the marker in a `try/finally`.\n\n### Fact-count ratchet\n\nNeither `UserEnvSnapshotTests` nor `SandboxEnvForwardingTests` is in the tracked prefix list in `SplitGuardVerificationTests.cs` lines 186–231. No ratchet bump needed. The baseline of 175 remains unchanged.\n\n### Build verification\n\n`dotnet build VisualRelay.slnx` must succeed. All existing tests must pass. Packaged/brew installs (no snapshot) produce `Remove` = empty, `Overrides` = `BuildSandboxEnvironment` — byte-identical to today.",
  "manifest": [
    "src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs",
    "src/VisualRelay.Core/Execution/SandboxedTestRunner.cs",
    "src/VisualRelay.Core/Execution/SandboxedTestRunner.Watched.cs",
    "tests/VisualRelay.Tests/UserEnvSnapshotTests.cs",
    "tests/VisualRelay.Tests/SandboxEnvForwardingTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 4.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/UserEnvSnapshotTests.cs",
    "tests/VisualRelay.Tests/SandboxEnvForwardingTests.cs"
  ],
  "rationale": "Both test files were modified: UserEnvSnapshotTests adapted 3 existing facts to the new TargetCommandEnvironment record shape and gained 5 new facts; SandboxEnvForwardingTests gained 1 new child-process integration test. Build succeeds with 0 errors/warnings. All 16 targeted tests pass."
}

## Stage 6 - Implement

{
  "summary": "Implemented replace-target-env-base-not-overlay across 5 source files. Added `TargetCommandEnvironment(Overrides, Remove)` record to `ProcessRunners.SandboxEnv.cs`; when a valid snapshot exists, `Remove` contains every process-env key not in `Overrides`, ensuring devshell-only keys (like `DOTNET_ROOT`) are stripped from child processes. Threaded `Remove` through both call sites (`ProcessRunners.RunAsync.cs` and `SandboxedTestRunner.cs`/`.Watched.cs`) into the existing `envRemove` parameter of `ProcessCapture.RunAsync`. Compacted `UserEnvSnapshotTests.cs` to 232 lines (under the 300-line guard). Added `SandboxEnvForwardingTests.cs` exemption to the `Environment.SetEnvironmentVariable` convention test (same pattern as existing `KeyEnvFileHermeticityTests.cs` exemption). All 16 targeted tests pass; full suite 2997 passed / 0 failed."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure backend/CLI environment-variable plumbing: returning a record from BuildTargetCommandEnvironment, threading envRemove through ProcessCapture.RunAsync, adding unit tests. No UI, layout, styles, images, documents, charts, or any visual output to review._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Replaced overlay-only env merge with replacement-base semantics: `BuildTargetCommandEnvironment` now returns a `TargetCommandEnvironment(Overrides, Remove)` record; when a valid user snapshot exists, `Remove` contains every process-env key not present in the merged overrides, ensuring nix-devshell-only variables (like `DOTNET_ROOT`) are stripped from child processes rather than leaking through inheritance. Threaded `Remove` through both call sites (`ProcessRunners.RunAsync` and `SandboxedTestRunner`/`.Watched`) into the existing `envRemove` parameter of `ProcessCapture.RunAsync`. Added 8 new tests (5 unit, 1 integration, 1 convention exemption, plus compaction) — all 2997 tests pass with 0 failures.",
  "commitMessages": [
    "fix: strip devshell-only env keys from target commands by returning Remove set alongside Overrides",
    "fix(env): make user snapshot the true base for child process env instead of overlaying on devshell",
    "When a valid snapshot exists, purge any process-env key not in the merged overrides so nix-only vars like DOTNET_ROOT cannot leak into verify/guard/stage commands",
    "fix: thread envRemove through both target-command call sites to prevent nix-devshell env leaks"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

