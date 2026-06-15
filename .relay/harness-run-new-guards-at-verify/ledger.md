## Stage 1 - Ideate

{
  "summary": "Add `NewGuardPatterns` to `RelayConfig` (default `[\"tools/guards/**/*.sh\"]`), implement `NewGuardProbeAsync` in the repo-guards file to run newly-detected guard scripts unsandboxed on the host, and wire it into stage 9 before `IntegrateGuardAsync` — with failing TDD tests first. Three options differ in how the probe's failure signal feeds the Fix-verify loop.",
  "options": [
    "Option A (recommended): Add dedicated `newGuardFailed`/`newGuardOutput` variables parallel to `bootstrapFailed`/`bootstrapFailureOutput` — clean separation, preserves both outputs on simultaneous failure, minimal diff.",
    "Option B: Reuse the existing `bootstrapFailed` flag by prepending new-guard output to `bootstrapFailureOutput` — smallest change (fewer new variables) but conflates two failure modes into one string.",
    "Option C: Extract `RunProbesAsync` returning aggregated `(label, output)` tuples — most extensible for future probes but over-engineered for this scope and requires a larger refactor of existing stage-9 logic."
  ]
}

## Stage 2 - Research

{
  "findings": "1. Four files to modify: RelayConfig.cs (+1 property), RelayConfigLoader.cs (+3 lines + OptionalStringArray overload), RelayDriver.RepoGuards.cs (+NewGuardProbeAsync ~40 lines), RelayDriver.cs (+probe call in stage-9 block ~8 lines). Two new test files to create. 2. Microsoft.Extensions.FileSystemGlobbing not referenced anywhere; use a simple inline glob matcher (like existing MatchesBootstrapGlob) extended for ** path patterns. 3. Existing test doubles (ScriptedTestRunner, RecordingTestRunner, CommandDispatchTestRunner, CapturingSubagentRunner) are sufficient; no new test doubles needed. 4. OptionalStringArray currently returns [] when absent; needs an overload with fallback param for NewGuardPatterns default. 5. BuildFailureOutput must be extended if using dedicated newGuardFailed/newGuardOutput variables (Option A recommended by ideate stage). 6. Stage-9 wiring: insert NewGuardProbeAsync call after bootstrap check (line 198) and before IntegrateGuardAsync (line 201). 7. Integration tests should follow RelayDriverRepoGuardTests.cs pattern with CommandDispatchTestRunner to route guard-script commands separately from test commands. 8. Config loader tests follow RelayConfigLoaderTests.cs pattern with explicit JSON files.",
  "constraints": [
    "Files must stay under 300 lines each",
    "New test files must be under 150 and 60 lines respectively",
    "Must not add FileSystemGlobbing NuGet dependency if avoidable (use inline glob matching)",
    "Must follow existing OptionalStringArray pattern in RelayConfigLoader",
    "Tests must be TDD-first (failing before implementation)",
    "New guard probe must run unsandboxed via _dependencies.TestRunner (same as bootstrap/integrate paths)",
    "guardCmd coverage gap warning is advisory only (no blocking, no auto-config-write)",
    "./visual-relay check must pass after all changes"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Three independent gaps converge to make newly-added guard scripts invisible to the stage-9 Verify gate. (1) Static config snapshot: `guardCmd` in `.relay/config.json` (line 28) is the frozen string `\"tools/guards/guard-source-enumeration.sh && tools/guards/check-file-size.sh && dotnet format VisualRelay.slnx --verify-no-changes\"` — set once at init time by `GuardCommandDetector.Detect()` (`src/VisualRelay.Core/Init/TestCommandDetector.cs:120–155`), loaded once by `RelayConfigLoader.cs:151` (`GuardCommand = OptionalStringOrNull(root, \"guardCmd\")`), and executed as an opaque shell command by `IntegrateGuardAsync` (`RelayDriver.RepoGuards.cs:151`). No code re-scans `tools/guards/` mid-run. After task 10 added `inspect-code.sh`, `guardCmd` still omits it. (2) Manifest is a Plan-stage snapshot, not a live inventory: `.relay/10-adopt-inspectcode-standards-repo-wide/manifest.txt` lists only 4 files (`src/MainWindowViewModel.cs`, `src/LiveState.cs`, `src/Commands.cs`, `src/App.axaml`) — the Fix stage (stage 8) created `tools/guards/inspect-code.sh`, `.config/dotnet-tools.json`, `.editorconfig` changes, and new test files, none of which entered the manifest. A new-guard probe that scans the manifest would miss `inspect-code.sh`. (3) Stage 9 Verify ran the old `guardCmd` only: `status.json` shows stage 9 `check: \"green\"`, no fix-verify loop entered; `run.log` confirms all 11 stages completed without interruption. The stage-9 agent verified the script exists (`stage9-attempt1.report.json`: 'tools/guards/inspect-code.sh | ✅ Syntax-valid, SARIF parsing logic verified') but could not execute it (sandbox blocks NuGet). The harness never ran it either. Result: the InspectCode gate shipped unverified through the harness; the new guard had zero runtime coverage at the harness level despite being wired into `visual-relay check` (bash entrypoint lines 380–383 include `inspect-code.sh` after build).",
  "excerpts": [
    ".relay/config.json:28 → `\"guardCmd\": \"tools/guards/guard-source-enumeration.sh && tools/guards/check-file-size.sh && dotnet format VisualRelay.slnx --verify-no-changes\"` — inspect-code.sh absent",
    ".relay/10-adopt-inspectcode-standards-repo-wide/manifest.txt:1–4 → only 4 source files; inspect-code.sh not in manifest",
    ".relay/10-adopt-inspectcode-standards-repo-wide/status.json — stage9: check=green, stage10: not entered (no fix-verify loop)",
    ".relay/10-adopt-inspectcode-standards-repo-wide/run.log:1–10 — full run stages 1→11, no guard failure interruption",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:151 → `GuardCommand = OptionalStringOrNull(root, \"guardCmd\")` — read once, never re-scanned",
    "src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs:151 → `if (config.GuardCommand is null) return (false, null, false);` — runs opaque string only, no discovery",
    "visual-relay:380–383 → `inspect-code.sh` IS wired into bash `check`, but harness uses config.guardCmd, not the bash script",
    "tools/guards/inspect-code.sh exists on disk (62 lines, SARIF-based gate) — created by task 10 Fix stage, never executed by harness"
  ],
  "repro": "1. Confirm guardCmd gap: `grep guardCmd .relay/config.json` → inspect-code.sh absent. 2. Confirm manifest gap: `cat .relay/10-adopt-inspectcode-standards-repo-wide/manifest.txt` → only 4 files, no tools/guards/. 3. Confirm harness never ran it: `grep inspect-code .relay/10-adopt-inspectcode-standards-repo-wide/run.log` → no harness-level invocation. 4. Confirm bash check includes it: `grep -n inspect-code visual-relay` → lines 383,403,405 — wired in bash but invisible to harness."
}

## Stage 4 - Plan

{
  "plan": "## Option A — dedicated newGuardFailed/newGuardOutput variables\n\n### Step 1 — RelayConfig property\n**File:** `src/VisualRelay.Domain/RelayConfig.cs` (+2 lines)\nAfter `GuardCommand` (line 55), add:\n```csharp\nIReadOnlyList<string> NewGuardPatterns = [\"tools/guards/**/*.sh\"],\n```\nNo change to `Defaults()` — the record default covers the absent-JSON case.\n\n### Step 2 — Config loader\n**File:** `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (+5 lines)\n- Add fallback overload of `OptionalStringArray(JsonElement root, string name, IReadOnlyList<string> fallback)` — returns `fallback` when property absent or not an array.\n- Wire into `defaults with { … }` block: `NewGuardPatterns = OptionalStringArray(root, \"newGuardPatterns\", defaults.NewGuardPatterns),`\n\n### Step 3 — NewGuardProbeAsync + glob matcher\n**File:** `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs` (~+40 lines)\n- Add private `MatchesGuardGlob(string relativePath, string glob)` — splits on `/`, matches segment-by-segment with `**` (zero-or-more dirs) and `*` (within-segment wildcard).\n- Add private `NewGuardProbeAsync(rootPath, manifest, patterns, ct)` — filters manifest by glob, runs each candidate unsandboxed via `_dependencies.TestRunner.RunAsync`, aggregates non-zero/timeout output.\n- Extend `BuildFailureOutput` signature with optional `string? newGuardOutput` — emits `--- New guard probe ---\\n{newGuardOutput}` when non-null.\n\n### Step 4 — Wire into stage 9\n**File:** `src/VisualRelay.Core/Execution/RelayDriver.cs` (~+12 lines)\nAfter bootstrap check block (line 198), before `// ── Repo guard check ──` (line 200):\n```csharp\nvar newGuardFailed = false;\nstring? newGuardOutput = null;\n{\n    newGuardOutput = await NewGuardProbeAsync(\n        rootPath, manifest, config.NewGuardPatterns, cancellationToken);\n    if (newGuardOutput is not null) newGuardFailed = true;\n}\n```\nThen update: `bootstrapFailed || guardFailed` → `… || newGuardFailed`; pass `newGuardOutput` to `BuildFailureOutput`; include `newGuardFailed` in the baseline-verify skip guard.\n\n### Step 5 — Config-loader tests\n**+tests/VisualRelay.Tests/RelayConfigLoaderNewGuardPatternsTests.cs** (~55 lines)\nThree `[Fact]` tests following `RelayConfigLoaderBoostTurnsTaskIdsTests` pattern:\n1. `absent_defaults_to_tools_guards_glob` — omits field → `[\"tools/guards/**/*.sh\"]`\n2. `present_populates_custom_patterns` — `\"newGuardPatterns\": [\"custom/guards/*.sh\"]` → custom list\n3. `empty_array_disables_probes` — `\"newGuardPatterns\": []` → empty list\n\n### Step 6 — Driver integration tests\n**+tests/VisualRelay.Tests/RelayDriverNewGuardProbeTests.cs** (~140 lines)\nFive `[Fact]` tests using `CommandDispatchTestRunner` to route new-guard-script invocations separately:\n1. `Stage9_NoMatchingGuardsInManifest_VerifyProceeds` — manifest has no guard paths → probe returns null → green commit.\n2. `Stage9_NewGuardPassesProbe_VerifySucceeds` — manifest has `tools/guards/new.sh`, exit 0 → green commit.\n3. `Stage9_NewGuardFailsProbe_EntersFixVerifyLoop` — manifest has `tools/guards/new.sh`, exit 1 with output → red stage 9, stage 10 receives guard output.\n4. `Stage9_EmptyNewGuardPatterns_NeverProbes` — `newGuardPatterns: []` → test runner receives zero calls for guard scripts.\n5. `Stage9_NewGuardTimesOut_Flags` — test runner returns `TimedOut=true` → stage flagged.",
  "manifest": [
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "+tests/VisualRelay.Tests/RelayDriverNewGuardProbeTests.cs",
    "+tests/VisualRelay.Tests/RelayConfigLoaderNewGuardPatternsTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RelayConfigLoaderNewGuardPatternsTests.cs",
    "tests/VisualRelay.Tests/RelayDriverNewGuardProbeTests.cs"
  ],
  "rationale": "8 tests total (3 config-loader + 5 driver-integration). 4 fail before implementation — exactly the failures that the implementation must fix. The 4 passes guard zero-cost / baseline properties:\n\n**Config loader (1 FAIL, 2 PASS):**\n- FAIL: `absent_defaults_to_tools_guards_glob` — absent JSON returns `[]` instead of default `[\"tools/guards/**/*.sh\"]`; needs the fallback `OptionalStringArray` overload.\n- PASS: `present_populates_custom_patterns` — JSON field parsed correctly.\n- PASS: `empty_array_disables_probes` — empty array returns `[]`.\n\n**Driver integration (3 FAIL, 2 PASS):**\n- FAIL: `Stage9_NewGuardPassesProbe_VerifySucceeds` — guard script never called (no `NewGuardProbeAsync` wired).\n- FAIL: `Stage9_NewGuardFailsProbe_EntersFixVerifyLoop` — stage 10 never entered (probe not detecting failure).\n- FAIL: `Stage9_NewGuardTimesOut_Flags` — timeout not detected, run Committed instead of Flagged.\n- PASS: `Stage9_NoMatchingGuardsInManifest_VerifyProceeds` — zero-cost baseline, no guards to probe.\n- PASS: `Stage9_EmptyNewGuardPatterns_NeverProbes` — empty patterns skips probing (coincidentally passes before loader fix because probe isn't wired, but guards the disabled path after implementation)."
}

## Stage 6 - Implement

{
  "summary": "Implemented new-guard-probe gate in stage-9 Verify: harness runs any guard scripts newly added to the manifest unsandboxed on the host before accepting Verify. Changes: (1) Added NewGuardPatterns positional param with null! default to RelayConfig. (2) Added OptionalStringArray fallback overload to RelayConfigLoader, wired into newGuardPatterns config loading with default [\"tools/guards/**/*.sh\"]. (3) Added NewGuardProbeAsync method to RelayDriver.RepoGuards.cs — detects manifest entries matching configured globs, executes each via TestRunner, returns failure output or times-out. Added compact segment-based glob matcher (MatchesGuardGlob, SegmentListMatch, SegmentMatch). Extended BuildFailureOutput with optional newGuardOutput parameter. (4) Wired probe into stage 9 of RelayDriver.cs before IntegrateGuardAsync — non-zero exits trigger fix-verify loop, timeouts flag. (5) 8 TDD tests: 5 driver-integration + 3 config-loader, all passing. Moved GuardProbeTestDoubles to separate file. All files under 300 lines. Full suite: 899/899 passed."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    "src/VisualRelay.Domain/RelayConfig.cs: NewGuardPatterns defaults to null! instead of the required inline default [\"tools/guards/**/*.sh\"]; direct RelayConfig construction can leave it null.",
    "src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs: NewGuardProbeAsync omits the .Where(File.Exists) existence filter from the spec, so non-existent manifest entries are executed and fail spuriously.",
    "tests/VisualRelay.Tests/RelayDriverNewGuardProbeTests.cs: file is 267 lines, exceeding the required <150-line limit.",
    "src/VisualRelay.Core/Execution/RelayDriver.cs: contains unrelated style-only changes (removing braces/blank lines) that should be reverted to keep the diff focused."
  ]
}

## Stage 8 - Fix

{
  "summary": "Fixed all 4 Stage-7 review issues: (1) Moved NewGuardPatterns from a positional-record parameter (which C# restricts to compile-time constants, rejecting collection expressions) to an init-only record-body property with the required default [\"tools/guards/**/*.sh\"], preventing null on direct construction. Updated Defaults() factory and the with-expression in LoadAsync accordingly. (2) Added the missing .Where(File.Exists) filter in NewGuardProbeAsync so non-existent manifest entries are skipped instead of executed. Updated test Setup helper to create actual guard-script files on disk for the three tests that depend on File.Exists. (3) Compacted RelayDriverNewGuardProbeTests.cs from 267 to 132 lines by extracting a shared Setup helper and removing verbose XML doc comments. (4) Reverted unrelated style-only changes in RelayDriver.cs — restored braces on single-statement if blocks, the two-statement bootstrapFailed assignment, and the removed comments, then trimmed blank lines and comment verbosity to keep the file at exactly 300 lines. All 899 tests pass."
}

## Stage 9 - Verify

{
  "summary": "Added NewGuardProbeAsync to RelayDriver.RepoGuards.cs and wired it into stage-9 Verify to run newly-added guard scripts (matched against configurable NewGuardPatterns globs, default `tools/guards/**/*.sh`) unsandboxed before accepting Verify. Non-zero exit feeds real output into the fix-verify loop; timeout flags the stage. 8 TDD tests pass. Full 899-test suite green.",
  "commitMessages": [
    "feat(driver): probe newly-added guard scripts unsandboxed before accepting Verify",
    "feat(harness): run task-added guards at stage-9 gate to catch unverified new checks",
    "fix(verify): execute new guard scripts found in manifest to close unverified-gate gap",
    "feat: detect and run task-added guard scripts on host before Verify accepts",
    "feat(config): add newGuardPatterns with default tools/guards/**/*.sh for stage-9 guard probe"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

