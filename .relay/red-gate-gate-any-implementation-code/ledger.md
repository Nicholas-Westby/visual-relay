## Stage 1 - Ideate

{
  "summary": "Replace the `.cs`-specific extension check in RelayDriver.cs stage-5 with a language-agnostic check using a small allowlist of non-code extensions (`.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.csv`, and no-extension). Everything else is treated as implementation code. The gate applies iff at least one implementation file (code file not in testFiles) exists. This ensures XAML, TS, Python, Go, etc. all trigger the red gate, while docs/config and test-only changes skip it cleanly. Update the test `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` and `SeedPresentationOnly` to use a genuinely non-code file (e.g., `.md`), and add a test proving `.axaml`-only triggers the gate.",
  "options": [
    "Option 1 — Static non-code allowlist: A hard-coded HashSet<string> of non-code extensions in the method. Simplest, fail-safe (unknown → code).",
    "Option 2 — Configuration-driven non-code list: Same logic as Option 1 but the list is configurable (constructor parameter / config file), allowing per-repo customization without source changes.",
    "Option 3 — Code-marker whitelist: Explicitly list code extensions and treat everything else as non-code. Fail-open (unknown → no gate), which contradicts the task's safety direction — not recommended."
  ]
}

## Stage 2 - Research

{
  "findings": "The red-gate applicability decision is in `src/VisualRelay.Core/Execution/RelayDriver.cs` lines 104 & 111: `manifest.Any(file => file.EndsWith(\".cs\", StringComparison.OrdinalIgnoreCase))`. This must be replaced with a language-agnostic check using a static non-code allowlist (`.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.csv`, no-extension). Everything else is code. Implementation files = code files not in `testFiles`. Gate applies iff at least one implementation file exists. The `AuthorTestGate.RunAsync` and `RedGate.ComputeStripSet` logic (manifest minus testFiles) is already correct and unchanged. Test helper `SeedPresentationOnly` in `TestDoubles.cs` line 88 seeds a single `.axaml` file with empty testFiles — under the new logic that triggers the gate (XAML is code). A new non-code test case (e.g., `.md`) is needed for the 'skips' scenario. Existing happy-path test structure (code file + test file) remains valid.",
  "constraints": [
    "Must replace .cs-specific check with non-code extension allowlist: .md, .txt, .json, .yaml, .yml, .toml, .csv, and no-extension — everything else is code (fail-safe).",
    "Gate applies iff at least one implementation file exists: a code file not listed in testFiles.",
    "Gate is skipped when manifest is entirely non-code (docs/config) or entirely test files; stage-9 still verifies full suite green.",
    "RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits test must be updated: 'skips' case uses non-code file (e.g., .md); add test proving .axaml-only triggers the gate.",
    "SeedPresentationOnly helper in TestDoubles.cs must be updated (or supplemented) — .axaml is now implementation code, not a 'skip' case.",
    "Normal code+test tasks must still strip implementation, require red, restore — no regression.",
    "./visual-relay check must pass (build + test + file-size guard).",
    "C# and XAML files under 300 lines per file-size guard.",
    "Conventional Commit subjects required.",
    "Unknown extensions default to code (fail-safe toward requiring a test).",
    "AuthorTestGate.RunAsync and RedGate.ComputeStripSet need no changes — only the stage-5 decision logic in RelayDriver.cs.",
    "Files in testFiles are never implementation code regardless of extension."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The stage-5 red gate applicability is governed by a single C#-specific guard on `RelayDriver.cs:104`: `manifest.Any(file => file.EndsWith(\".cs\", StringComparison.OrdinalIgnoreCase))`. The gate runs only when `testFiles.Count > 0 || touchesCode` (line 111). Since `touchesCode` is true exclusively for `.cs` files, any non-C# code change (`.axaml`, `.ts`, `.py`, `.go`, etc.) with no authored tests is silently waved through without the TDD red gate. Conversely, a test-only manifest where every entry is in testFiles still enters the gate block (because `testFiles.Count > 0`), strips nothing, runs green, and flags 'author-tests did not go red' — there is no path to a test-only commit.\n\nThe downstream logic (`RedGate.ComputeStripSet` in RedGate.cs:14-18, `AuthorTestGate.RunAsync` in AuthorTestGate.cs:7-43) is already language-agnostic: ComputeStripSet returns `manifest \\ testFiles` regardless of file extension. Only the stage-5 entry guard in RelayDriver.cs needs to change.\n\nThe test `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` (RelayDriverTests.cs:74) encodes the old assumption by using `SeedPresentationOnly(\"src/Panel.axaml\")` and asserting `Committed`. Under the fix, `.axaml` must be treated as code and this test must use a genuinely non-code file (e.g., `.md`) for the 'skips' case.",
  "excerpts": [
    "src/VisualRelay.Core/Execution/RelayDriver.cs:104 — `var touchesCode = manifest.Any(file => file.EndsWith(\".cs\", StringComparison.OrdinalIgnoreCase));` — only .cs triggers the gate.",
    "src/VisualRelay.Core/Execution/RelayDriver.cs:111 — `if (testFiles.Count > 0 || touchesCode)` — gate entered for any testFiles OR .cs manifest, but not for non-.cs code.",
    "src/VisualRelay.Core/Execution/RedGate.cs:14-18 — `ComputeStripSet` already language-agnostic: returns `manifest.Where(file => !tests.Contains(file))`.",
    "tests/VisualRelay.Tests/TestDoubles.cs:88-92 — `SeedPresentationOnly` defaults to .axaml and sets `_presentationOnly=true` with empty testFiles, encoding the old skip assumption.",
    "tests/VisualRelay.Tests/RelayDriverTests.cs:74-94 — `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` uses .axaml, passes because .axaml != .cs → touchesCode=false → gate skipped."
  ],
  "repro": "1. Run `./visual-relay check` — all 167 tests pass green.\n2. The test `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` seeds manifest=[\"src/Panel.axaml\"], testFiles=[], test runner returns green (exit 0). Because `touchesCode` checks `.cs` only, the gate block is skipped entirely and the task commits. Assert passes.\n3. To see the gap: change the seeded manifest to [\"src/Panel.cs\"] with same empty testFiles. Now `touchesCode` is true → gate runs → strips nothing (no files to strip), runs tests green → flags \"author-tests did not go red\" → outcome Flagged. The `.axaml` variant should behave identically to the `.cs` variant but does not."
}

## Stage 4 - Plan

{
  "plan": "## Changes (5 files, ~+90 / −30 lines)\n\n### 1. `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs` (+2 lines, 128 total)\nAdd `private static bool IsImpl(string path)` after `ReadStringArray` (line 51). Uses `Path.GetExtension` with `{ Length: > 0 }` pattern match; returns `false` for no-extension and the non-code allowlist (`.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.csv`). Everything else is implementation code (fail-safe).\n\n### 2. `src/VisualRelay.Core/Execution/RelayDriver.cs` (−4 lines, 295 total)\n**Lines 103–111** — Replace the `.cs`-specific `touchesCode` variable and the `if (testFiles.Count > 0 || touchesCode)` guard with a single expression: `manifest.Any(f => !testFiles.Contains(f, StringComparer.Ordinal) && IsImpl(f))`. Update the comment to describe the new language-agnostic rule. Remove the `touchesCode` variable; `testFiles` is still read for the command interpolation below. The downstream gate logic (strip, run, check red) is unchanged.\n\n### 3. `tests/VisualRelay.Tests/TestDoubles.cs` (~+15 / −8 lines, ~195 total)\nReplace the single `_presentationOnly` bool + `_presentationFile` string with three mode flags: `_nonCodeOnly`/`_nonCodeFile` (default `\"docs/README.md\"`), `_codeOnly`/`_codeFile` (default `\"src/View.axaml\"`), `_testOnly`/`_testOnlyFile` (default `\"tests/app.tests.cs\"`). Rename `SeedPresentationOnly` → `SeedNonCodeOnly`, add `SeedCodeOnly`, add `SeedTestOnly`. Update stage 4/5 switch arms to dispatch on the new flags. Keep `SeedHappyPath` unchanged.\n\n### 4. `tests/VisualRelay.Tests/RelayDriverTests.cs` (−22 lines, 273 total)\nDelete `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` (lines 73–94). The three replacement tests in the new file cover the old scenario (non-code skips) plus the two new scenarios (axaml triggers, test-only skips).\n\n### 5. `tests/VisualRelay.Tests/RedGateApplicabilityTests.cs` (new, ~85 lines)\nThree [Fact] tests in `sealed class RedGateApplicabilityTests`:\n- **`NonCodeOnlyChange_SkipsRedGateAndCommits`** — Seeds `\"docs/README.md\"` via `SeedNonCodeOnly`, test runner returns green. Gate skipped → `Committed`.\n- **`AxamlOnlyChange_TriggersRedGate`** — Seeds `\"src/Panel.axaml\"` via `SeedCodeOnly`, test runner returns green (exit 0). Gate runs, strips axaml, tests green → `Flagged` with reason `\"author-tests did not go red\"`.\n- **`TestOnlyChange_SkipsRedGateAndCommits`** — Seeds `\"tests/regression.cs\"` via `SeedTestOnly` (manifest entry == testFiles entry), test runner returns green. Gate skipped → `Committed`.\n\n### No regression\nExisting `RunTaskAsync_WritesLedgerSealsManifestAndStructuredEvents` still exercises code+test happy path (`.cs` → gate runs, goes red then green, commits). `RedGate.ComputeStripSet` and `AuthorTestGate.RunAsync` are unchanged — only the stage-5 entry guard in RelayDriver changes.",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "tests/VisualRelay.Tests/TestDoubles.cs",
    "tests/VisualRelay.Tests/RelayDriverTests.cs",
    "tests/VisualRelay.Tests/RedGateApplicabilityTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RedGateApplicabilityTests.cs",
    "tests/VisualRelay.Tests/TestDoubles.cs",
    "tests/VisualRelay.Tests/RelayDriverTests.cs"
  ],
  "rationale": "Three tests encoding the language-agnostic red-gate applicability: (1) AxamlOnlyChange_TriggersRedGate — proves .axaml code triggers the gate when current .cs-only check skips it; (2) NonCodeOnlyChange_SkipsRedGateAndCommits — proves .md non-code skips the gate (already green); (3) TestOnlyChange_SkipsRedGateAndCommits — proves test-only changes commit without flagging when current testFiles.Count>0 check flags them. Two tests fail against current code, establishing the red gate. Existing RunTaskAsync_PresentationOnlyChange updated to use .md (non-code) since .axaml is now code."
}

## Stage 6 - Implement

{
  "summary": "Implemented language-agnostic red gate applicability. Added `IsImpl()` helper with a non-code extension allowlist (`.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.csv`, no-extension) to `RelayDriver.Artifacts.cs`. Replaced the `.cs`-only `touchesCode` check in `RelayDriver.cs` stage-5 with `hasImpl = manifest.Any(f => !testFiles.Contains(f) && IsImpl(f))`. Cleaned up dead `_presentationOnly` code from `TestDoubles.cs`. Updated pre-existing tests that used `.txt`/`.json`/no-extension files as implementation to use `.cs` extensions. All 170 tests pass, all files under 300 lines."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "No blockers or warnings to resolve. Review verdict was \"pass\" with zero issues. All 170 tests pass, build is clean (0 warnings, 0 errors), and all five manifest files are under 300 lines. The language-agnostic red gate is fully implemented: IsImpl() treats .axaml/.ts/.py/.go/etc. as code, non-code allowlist (.md/.txt/.json/.yaml/.yml/.toml/.csv/no-extension) correctly skips the gate, and test-only changes commit without flagging."}

## Stage 9 - Verify

{
  "summary": "Replaced the C#-specific `.cs` extension check in RelayDriver.cs stage-5 with a language-agnostic `IsImpl()` helper using a non-code allowlist (`.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.csv`, and no-extension — everything else is implementation code). The red gate now applies whenever the manifest contains any code file not listed in `testFiles`, regardless of language (`.axaml`, `.ts`, `.py`, `.go`, etc.). A code-only change with no authored tests correctly flags `\"author-tests did not go red\"`. The gate is skipped for entirely non-code manifests (docs/config) and for test-only manifests (every entry is a declared test file). Updated `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` to use `.md` (non-code), added three dedicated tests in `RedGateApplicabilityTests.cs` proving `.axaml`-only triggers the gate, `.md`-only skips, and test-only skips. No regression: normal code+test tasks still strip implementation, require red, and restore. All 170 tests pass, build is clean (0 warnings, 0 errors), all files under 300 lines.",
  "commitMessage": "feat: make red gate language-agnostic via non-code extension allowlist"
}

## Stage 10 - Fix-verify

{
  "summary": "Verified: all 170 tests pass, build is clean (0 warnings, 0 errors), all five manifest files are under 300 lines (143/299/204/294/86), format check passes. The pinned suite is all green. No failures to fix. The language-agnostic red gate is correctly implemented: IsImpl() treats unknown extensions as code (fail-safe), the non-code allowlist (.md/.txt/.json/.yaml/.yml/.toml/.csv/no-extension) correctly skips the gate, and test-only changes commit without flagging. AxamlOnlyChange_TriggersRedGate, NonCodeOnlyChange_SkipsRedGateAndCommits, and TestOnlyChange_SkipsRedGateAndCommits all pass. Normal code+test tasks still strip implementation, require red, and restore — no regression."
}

## Stage 11 - Commit

Committed by Visual Relay.

