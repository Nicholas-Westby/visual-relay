## Stage 1 - Ideate

{ "summary": "Frame the task of detecting environment-bootstrap file changes and validating fresh bootstrap before commit, with three architectural options ranging from minimal prepend-injection (Option 3) through shared-channel gating (Option 1) to a dedicated stage (Option 2).", "options": ["Option 1: Stage-9 gate with manifest-driven bootstrap check, reusing the test runner's result channel and fix-verify loop", "Option 2: Dedicated bootstrap validation stage (stage 8.5) with its own event type, time-box, and degradation policy", "Option 3: Build-time injection — prepend the bootstrap smoke command to the test-runner command sequence when bootstrap files are touched"] }

## Stage 2 - Research

{
  "findings": "Codebase investigation complete. Pipeline: 11 stages, stage 9 (Verify) runs test command via ITestRunner; red tests enter RunVerifyFixLoopAsync in RelayDriver.VerifyFix.cs (loop up to MaxVerifyLoops). Manifest (from stage 4 Plan) is available as IReadOnlyList<string> at stage 9. Config is RelayConfig record loaded from .relay/config.json by RelayConfigLoader — new fields must be added to both. ITestRunner interface: RunAsync(rootPath, command, Cancellation) → TestRunResult(ExitCode,Output,TimedOut). ShellTestRunner wraps /bin/sh with configurable timeout (default 300s). Test doubles exist: ScriptedTestRunner (FIFO queue), ScriptedSubagentRunner (canned per-stage JSON), InMemoryRelayEventSink, TestRepository (WriteConfig/WriteTask), CapturingSubagentRunner (records invocations). No existing bootstrap code anywhere — search returns zero matches. Key file locations: RelayDriver.cs:180-230 (stage 9 gate), RelayDriver.VerifyFix.cs:115-213 (fix-verify loop), RelayConfig.cs (record to extend), RelayConfigLoader.cs (parser to extend), ProcessRunners.cs:8-20 (ShellTestRunner impl), TestDoubles.cs (test mocks), RelayDriverTestDoubles.cs, SubagentRunnerTestDoubles.cs.",
  "constraints": [
    "No new stages — bootstrap check must integrate into existing stage 9 gate (Option 1 from ideation)",
    "Zero cost when bootstrap files unchanged — manifest scan must be cheap, smoke command only fires on match",
    "Config-driven — BootstrapFiles (string[] globs) and BootstrapCheckCommand (string) must be optional in .relay/config.json with sensible defaults; default globs: flake.nix, flake.lock, *.nix, Brewfile, Dockerfile*, .tool-versions, rust-toolchain*",
    "Time-boxed — bootstrap smoke command must use the same timeout mechanism as ShellTestRunner; timeout produces TimedOut:true TestRunResult feeding existing flag-with-hint path",
    "Fix-verify integration — non-zero exit from bootstrap check enters RunVerifyFixLoopAsync with check output as failingTestOutput, same as failing test",
    "Seal/status recording — bootstrap check result must be recorded in status entries and seals (green/red) like test results via RecordStageAsync",
    "Reuse existing test doubles — ScriptedTestRunner, ScriptedSubagentRunner, InMemoryRelayEventSink, TestRepository, CapturingSubagentRunner; NoGitCommit option",
    "Backward-compatible config — new fields must be optional in RelayConfigLoader (existing OptionalString/Int/Bool pattern); existing configs without bootstrap fields load unchanged",
    "No LLM prompt contract changes — stage 9's JSON output contract stays unchanged; bootstrap check is purely driver-side code",
    "Default BootstrapFiles list must be empty / null so repos without bootstrap config get no check; built-in detection can activate based on known file patterns in manifest"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The Verify stage (stage 9) inside RelayDriver.cs (lines 180-230) only runs `config.TestCommand` via the ITestRunner interface. It has no awareness of environment-bootstrap files (flake.nix, Brewfile, Dockerfile, .tool-versions, rust-toolchain.toml, etc.). The manifest captured from stage 4 Plan (line 103-125) lists all modified files but is only used for git commit scoping and seal hashing — never scanned for bootstrap-relevant paths. RelayConfig (RelayConfig.cs) has no `BootstrapFiles` or `BootstrapCheckCommand` fields. RelayConfigLoader (RelayConfigLoader.cs) has no bootstrap-related parsing. A grep of the entire src/ tree for 'bootstrap', 'flake.nix', 'Brewfile', 'Dockerfile', 'tool-versions', and 'rust-toolchain' returns zero matches. The consequence: when a run adds `nono` to flake.nix (as in the 2026-06-09 incident), the test suite executes inside the already-provisioned nix shell where nono was already installed, all stages pass, the change is committed (eaef5be), and the next fresh `nix develop` entry fails because nixpkgs builds nono 0.53.0 from source and its check phase fails inside the nix build sandbox. The fix-verify loop (RelayDriver.VerifyFix.cs:115-213) only re-runs the test command — it cannot remediate a broken bootstrap because the bootstrap isn't checked at all. The entire pipeline is bricked for every subsequent invocation until a human reverts the line (734a551).",
  "excerpts": [
    "RelayDriver.cs:180-230 — Stage 9 gate runs only `config.TestCommand`; no bootstrap check exists",
    "RelayDriver.cs:103-125 — Manifest captured from stage 4 Plan but never scanned for bootstrap-file patterns",
    "RelayConfig.cs:1-36 — RelayConfig record has no BootstrapFiles or BootstrapCheckCommand fields",
    "RelayConfigLoader.cs:1-176 — No bootstrap-related JSON fields parsed from .relay/config.json",
    "RelayDriver.VerifyFix.cs:115-213 — Fix-verify loop only re-runs test command, cannot remediate bootstrap failures",
    "ProcessRunners.cs:8-19 — ShellTestRunner wraps /bin/sh, configurable timeout; same pattern needed for bootstrap smoke command",
    "Interfaces.cs:15-18 — ITestRunner has single RunAsync(rootPath, command, ct) method; no separate bootstrap path",
    "grep src/ for 'bootstrap', 'flake.nix', 'Brewfile', 'Dockerfile', 'tool-versions', 'rust-toolchain' — zero matches in entire source tree"
  ],
  "repro": "1. Create a repo with flake.nix defining a dev shell. 2. Add a task that modifies flake.nix to add a package whose build fails in the nix sandbox (e.g. nono). 3. Run the pipeline inside an already-provisioned nix develop shell. 4. Observe: stage 5 author-test gate passes (tests run in existing shell), stage 9 verify passes (tests run in existing shell), commit is created. 5. Exit the shell and run `nix develop` fresh. 6. Observe: nix build fails, pipeline is bricked for all subsequent tasks until manual revert."
}

## Stage 4 - Plan

{
  "plan": "## Implementation Plan: verify-env-bootstrap-changes\n\n### Summary\nAdd a bootstrap verification gate at stage 9 that detects environment-bootstrap file changes (flake.nix, Brewfile, Dockerfile, etc.) and validates fresh provisioning before commit. Failures enter the existing fix-verify loop identically to test failures.\n\n---\n\n### 1. `src/VisualRelay.Domain/RelayConfig.cs` — Add bootstrap fields\n\nAppend two fields to the `RelayConfig` record (before closing `);`):\n- `IReadOnlyList<string> BootstrapFiles = []` — glob patterns for bootstrap files. Empty means \"use built-in defaults\".\n- `string? BootstrapCheckCommand = null` — smoke command. Null means auto-detect (nix repos get `nix develop --command true`, others skip).\n\n---\n\n### 2. `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` — Parse new fields\n\nAdd two private helpers:\n- `OptionalStringArray(JsonElement root, string name)` → returns `[]` when absent/not-array.\n- `OptionalStringOrNull(JsonElement root, string name)` → returns `null` when absent/non-string.\n\nIn `TryLoadAsync`, inside the `config = defaults with { ... }` block, add:\n```csharp\nBootstrapFiles = OptionalStringArray(root, \"bootstrapFiles\"),\nBootstrapCheckCommand = OptionalStringOrNull(root, \"bootstrapCheckCmd\"),\n```\nThe `Defaults()` method needs no changes — both defaults are empty/null by record initialization.\n\n---\n\n### 3. `src/VisualRelay.Core/Execution/RelayDriver.cs` — Stage 9 bootstrap gate\n\nAdd three private static members to `RelayDriver`:\n- `BuiltInBootstrapGlobs` = `[\"flake.nix\", \"flake.lock\", \"*.nix\", \"Brewfile\", \"Dockerfile*\", \".tool-versions\", \"rust-toolchain*\"]`\n- `BuiltInBootstrapCommand` = `\"nix develop --command true\"`\n- `MatchesBootstrapGlob(string path, string glob)` — filename-based glob matching: prefix `*`, suffix `*`, or exact.\n\nAdd `ResolveBootstrapCheck(RelayConfig config, IReadOnlyList<string> manifest)`:\n- Globs = `config.BootstrapFiles` if non-empty, else `BuiltInBootstrapGlobs`.\n- Command = `config.BootstrapCheckCommand` if set, else auto-detect: only nix repos (any .nix file matched) get the built-in command; otherwise return `(false, \"\")`.\n- Return `(true, command)` if any manifest entry matches a glob.\n\n**Stage 9 block modification** (lines 180–230):\n1. Parse stage 9 JSON (unchanged, line 102).\n2. **NEW**: Call `ResolveBootstrapCheck`. If `shouldRun`:\n   - Run `_dependencies.TestRunner.RunAsync(rootPath, bootstrapCmd, ct)`.\n   - On timeout → `FlagAsync` with hint.\n   - On non-zero exit → set `bootstrapFailed = true`, capture `bootstrapFailureOutput`.\n3. Run normal test (unchanged).\n4. Determine `check`: if `bootstrapFailed` → `\"red\"`; else use test exit code.\n5. Build `failureOutput` for fix-verify:\n   - Both failed → `testOutput + \"\\n\\n--- Bootstrap check output ---\\n\" + bootstrapOutput`\n   - Only bootstrap failed → `\"Bootstrap check failed:\\n\" + bootstrapOutput`\n   - Only test failed → test output (unchanged).\n6. Pass `bootstrapCmd` (or null) as new parameter to `RunVerifyFixLoopAsync`.\n\n---\n\n### 4. `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs` — Bootstrap re-check in fix-verify loop\n\nAdd `string? bootstrapCheckCmd` parameter to `RunVerifyFixLoopAsync`.\n\nIn the for-each-attempt loop, after the LLM returns valid JSON (line 167):\n1. **NEW**: If `bootstrapCheckCmd` is not null — run it. On timeout → Flag. On non-zero → `check = \"red\"`, capture output as `failingResult`.\n2. If bootstrap passed or wasn't needed — run the test command (existing code, lines 168–174). If test fails → `check = \"red\"`, capture output.\n3. Use `failingResult?.Output` as `failingTestOutput` for the next iteration (line 206).\n\nThe recording/sealing logic (lines 178–200) stays unchanged.\n\nCall site in `RelayDriver.cs` line 217: add the new `bootstrapCheckCmd` argument.\n\n---\n\n### 5. `tests/VisualRelay.Tests/RelayDriverBootstrapTests.cs` — Four driver-level tests\n\n**Test (a):** `RunTaskAsync_ManifestIncludesFlakeNix_RunsBootstrapCheck`\n- `ScriptedSubagentRunner` returns manifest `[\"flake.nix\", \"src/app.cs\"]`.\n- `ScriptedTestRunner` queued: `[green(bootstrap), red(test-stage9), green(test-fixverify)]`.\n- Assert `Committed`, verify 3 test-runner calls consumed (bootstrap + test + fixverify re-verify) vs 2 baseline.\n\n**Test (b):** `RunTaskAsync_ManifestWithoutBootstrapFiles_SkipsBootstrapCheck`\n- Manifest: `[\"src/app.cs\", \"tests/app.tests.cs\"]` only.\n- `ScriptedTestRunner` queued: `[red(test-stage9), green(test-fixverify)]`.\n- Assert `Committed`, verify only 2 test-runner calls.\n\n**Test (c):** `RunTaskAsync_BootstrapFailsTestPasses_EntersFixVerifyWithBootstrapOutput`\n- Manifest includes `flake.nix`.\n- `ScriptedTestRunner`: `[bootstrap fail \"nix build of nono failed\", test pass \"all green\", test pass fixverify]`.\n- Use `CapturingSubagentRunner`. Assert stage 10 `LastTestOutput` contains `\"nix build of nono failed\"`. Assert `Committed`.\n\n**Test (d):** `RunTaskAsync_ConfiguredBootstrapGlobsAndCommand_OverridesDefaults`\n- Write config with `bootstrapFiles: [\"custom.bootstrap\"]`, `bootstrapCheckCmd: \"custom-check\"`.\n- Manifest includes `custom.bootstrap`.\n- Inline `RecordingTestRunner` captures the command string.\n- Assert `\"custom-check\"` was passed to `RunAsync`.\n\n---\n\n### Config backward-compatibility\n- `bootstrapFiles` absent → `[]` → driver uses built-in globs.\n- `bootstrapCheckCmd` absent → `null` → driver auto-detects (nix repos only).\n- Existing `.relay/config.json` files load unchanged — no new required fields.\n- No LLM prompt contract changes. Stage 9 JSON output contract stays identical.",
  "manifest": [
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs",
    "tests/VisualRelay.Tests/RelayDriverBootstrapTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RelayDriverBootstrapTests.cs",
    "tests/VisualRelay.Tests/TestDoubles.cs"
  ],
  "rationale": "Four driver-level tests mirroring the TestRunner fakes pattern: (a) manifest with flake.nix triggers bootstrap check — fails because only 2 calls observed; (b) no bootstrap files → zero-cost skip — passes as baseline; (c) bootstrap fails but test passes → must enter fix-verify with bootstrap output — fails because test passes green at stage 9 with no stage 10; (d) configured bootstrapFiles/bootstrapCheckCmd override defaults — fails because custom command never invoked. RecordingTestRunner and CommandAwareTestRunner added to TestDoubles.cs for call-count and command-aware assertions. All 481 existing tests still pass."
}

## Stage 6 - Implement

{
  "summary": "## Changes made\n\n### 1. `src/VisualRelay.Domain/RelayConfig.cs`\nAdded two optional fields to the `RelayConfig` record:\n- `IReadOnlyList<string>? BootstrapFiles = null` — glob patterns for bootstrap files. Null means use built-in defaults.\n- `string? BootstrapCheckCommand = null` — smoke command. Null means auto-detect (nix repos get `nix develop --command true`, others skip).\n\n### 2. `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`\nAdded two private helpers:\n- `OptionalStringArray(JsonElement, string)` → returns `[]` when absent/not-array, or the string array.\n- `OptionalStringOrNull(JsonElement, string)` → returns `null` when absent/non-string, or the string value.\n\nParsing added in `TryLoadAsync` for `bootstrapFiles` and `bootstrapCheckCmd` keys in `.relay/config.json`. Existing configs without these keys load unchanged.\n\n### 3. `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs`\nAdded bootstrap detection infrastructure:\n- `BuiltInBootstrapGlobs` — default glob list: `flake.nix`, `flake.lock`, `*.nix`, `Brewfile`, `Dockerfile*`, `.tool-versions`, `rust-toolchain*`\n- `BuiltInBootstrapCommand` — `\"nix develop --command true\"`\n- `MatchesBootstrapGlob(path, glob)` — filename-based matching: prefix `*`, suffix `*`, or exact.\n- `ResolveBootstrapCheck(config, manifest)` — resolves globs and command; returns `(shouldRun, command)`.\n\n### 4. `src/VisualRelay.Core/Execution/RelayDriver.cs` (stage 9 block)\nBootstrap smoke check runs **before** the normal test command. On timeout → Flag. On non-zero exit → `bootstrapFailed = true`, `check = \"red\"`. Baseline verify is skipped when bootstrap itself is broken (the failure is definitely new). Failure output combines bootstrap + test output when both fail. `RunVerifyFixLoopAsync` receives `bootstrapCmd` so it can re-check in the fix-verify loop.\n\n### 5. `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`\nAdded `string? bootstrapCheckCmd` parameter. In each fix-verify iteration, the bootstrap command re-runs before the test. If bootstrap fails → `check = \"red\"`, failing output is the bootstrap failure. If bootstrap passes but test fails → `check = \"red\"`, failing output is test output. The `failingTestOutput` fed to the next LLM iteration carries whichever check failed.\n\n### 6. `tests/VisualRelay.Tests/RelayDriverBootstrapTests.cs` (already written)\nFour driver-level tests: (a) manifest with flake.nix triggers bootstrap check → 3+ calls, one containing `nix develop`; (b) no bootstrap files → zero-cost skip, exactly 2 calls; (c) bootstrap fails, test passes → enters fix-verify with bootstrap output, stage 9 seal is red, stage 10 seal is green; (d) configured `bootstrapFiles`/`bootstrapCheckCmd` override defaults — custom command observed.\n\n**Result:** All 484 tests pass (0 failures)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Stage 7 review returned verdict: pass with zero issues. No blockers or warnings to resolve. All 484 tests pass. Implementation is complete." }

## Stage 9 - Verify

{
  "summary": "Stage 9 verify: all five manifest files reviewed and confirmed correct. RelayConfig.cs adds two optional bootstrap fields (BootstrapFiles, BootstrapCheckCommand) with null defaults. RelayConfigLoader.cs parses them via OptionalStringArray/OptionalStringOrNull helpers, absent keys produce []/null. RelayDriver.Artifacts.cs contains the bootstrap detection core: BuiltInBootstrapGlobs (7 entries plus nix auto-detect), MatchesBootstrapGlob (prefix/suffix/exact), and ResolveBootstrapCheck (custom config overrides built-ins, empty-string disables detection). RelayDriver.cs stage-9 gate runs bootstrap smoke check before the test command, merges failure output, skips baseline-verify when bootstrap breaks, and passes bootstrapCmd to RunVerifyFixLoopAsync. RelayDriver.VerifyFix.cs re-checks bootstrap in each fix-verify iteration before the test command, carrying whichever check failed as failingTestOutput. Test doubles RecordingTestRunner and CommandAwareTestRunner enable call-count and command-aware assertions. Four driver-level tests cover: (a) flake.nix triggers bootstrap check, (b) no bootstrap files → zero-cost skip, (c) bootstrap failure enters fix-verify with correct output and red/green seals, (d) custom config overrides built-in defaults. Backward-compatible: existing configs without bootstrap keys load unchanged. No interface changes to ITestRunner. No LLM prompt contract changes.",
  "commitMessages": [
    "feat(verify): detect env-bootstrap file changes and validate fresh provisioning before commit",
    "Add bootstrap-aware stage-9 gate with auto-detected globs and fix-verify loop integration",
    "feat: run bootstrap smoke check when manifest touches flake.nix, Brewfile, Dockerfile, etc."
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "All 484 tests pass with zero failures. The `verify-env-bootstrap-changes` implementation is complete across all five manifest files: RelayConfig.cs (BootstrapFiles/BootstrapCheckCommand fields), RelayConfigLoader.cs (OptionalStringArray/OptionalStringOrNull parsing), RelayDriver.Artifacts.cs (BuiltInBootstrapGlobs, MatchesBootstrapGlob, ResolveBootstrapCheck), RelayDriver.cs (stage-9 bootstrap gate before test command), and RelayDriver.VerifyFix.cs (bootstrap re-check in fix-verify loop). Four driver-level tests validate: (a) flake.nix manifest triggers bootstrap check, (b) no bootstrap files → zero-cost skip, (c) bootstrap failure enters fix-verify with correct output and red/green seals, (d) custom config overrides built-in defaults. No fixes needed — the pipeline is green."
}

## Stage 11 - Commit

Committed by Visual Relay.

