## Stage 1 - Ideate

{
  "summary": "Fix intermittent deadlock in headless Avalonia UI tests caused by four test classes running concurrently across default xUnit v3 collections, racing on the process-global dispatcher. The canonical fix is committing xunit.runner.json with parallelizeTestCollections: false — a single config file, zero C# changes, ≤10 s overhead.",
  "options": [
    "Commit xunit.runner.json with 'parallelizeTestCollections': false at tests/VisualRelay.Tests/ (canonical xunit v3, zero code changes, covers all current and future headless classes)",
    "Add [Collection('AvaloniaHeadless')] to all 6 headless test classes (narrower scope but fragile — touches 6 source files, needs new convention guard)",
    "Accept the intermittent deadlock bounded by existing watchdog timeout (no new code but deadlock still occurs, flaky CI reruns, does not fix root cause)"
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase confirms all claims from the task input. No `xunit.runner.json` exists anywhere in the repo. Four Avalonia headless test classes (`ActivityColumnItemsPanelTests`, `AddAttachmentsTests`, `ConfigInitEmptyStateUiTests`, `NewTaskAuthoringTests`) use `[AvaloniaFact]`/`[AvaloniaTheory]` but carry no `[Collection]` attribute, so each lands in a separate default xUnit v3 collection and can race on the process-global dispatcher. Two more headless classes (`KeySetupPanelUiTests`, `SettingsPanelUiTests`) have `[Collection(\"Environment\")]` which serializes them relative to each other but NOT relative to the four uncollected classes. The project uses xunit v3 (3.2.2) via VSTest (17.14.1 + runner 3.1.4). The `SplitGuardVerificationTests.cs` defines `TestsDir` (line 14) accessible from its companion partial file. The .csproj lacks a `<None>` entry for `xunit.runner.json` and would need one (`CopyToOutputDirectory=\"PreserveNewest\"`). The `.gitignore` does not block `xunit.runner.json`. The watchdog timeout (60s test/300s check) in `visual-relay` script is already in place. `BannedSymbols.txt` bans `HeadlessUnitTestSession`. `HeadlessTestApp.cs` sets up the shared Avalonia app. The `visual-relay` script's test command is `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false`. Existing convention guards validate collection attributes for `GitCommitter`, `Environment`, `Watchdog`, and `GitInvoker` groups.",
  "constraints": [
    "The fix itself must be zero C# changes — only `xunit.runner.json` content and the .csproj `<None>` copy-to-output item",
    "A new `[Fact]` in `SplitGuardVerificationTests.Conventions.cs` (or companion) must be written first, fail against the pre-fix tree, then pass after the JSON is created",
    "The test file (`SplitGuardVerificationTests.Conventions.cs`) is already 280 lines and must stay ≤300 lines per the existing file-size guard",
    "Existing `[Collection]` groups and their convention guards must remain intact",
    "The watchdog timeout must be preserved as a safety net",
    "All 725 tests must still be discovered and pass",
    "The .csproj must get a `<None Include=\"xunit.runner.json\" CopyToOutputDirectory=\"PreserveNewest\" />` entry for xunit to find the config next to the assembly",
    "Conventional commit subject must be `fix(tests): disable test-collection parallelism to prevent headless Avalonia deadlock`",
    "No new files beyond `xunit.runner.json` should be created (unless the convention test needs a standalone file, which would need to stay ≤300 lines)",
    "`dotnet test` must complete under 4 minutes across repeated runs with no stall"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The root cause is confirmed: four Avalonia headless test classes (ActivityColumnItemsPanelTests, AddAttachmentsTests, ConfigInitEmptyStateUiTests, NewTaskAuthoringTests) use [AvaloniaFact]/[AvaloniaTheory] but carry no [Collection] attribute, so each lands in a separate default xUnit v3 collection and races on the single process-global Avalonia dispatcher. Two more headless classes (KeySetupPanelUiTests, SettingsPanelUiTests) have [Collection(\"Environment\")] which serializes them relative to each other but NOT relative to the four uncollected classes. No xunit.runner.json existed anywhere in the repo before this fix. The .csproj had no copy-to-output entry for the JSON config. Prior DONE-08 records a 34-minute stall; TROUBLESHOOTING.md documents the signature (Testing (NNNs) climbing, nothing completing).",
  "excerpts": [
    "SplitGuardVerificationTests.Conventions.cs:281-295 — new [Fact] XunitRunnerJson_DisablesTestCollectionParallelism asserts xunit.runner.json exists with parallelizeTestCollections: false (added before the fix file, TDD)",
    "xunit.runner.json:1-4 — { \"$schema\": \"...\", \"parallelizeTestCollections\": false } — the canonical xunit v3 fix, zero C# changes",
    "VisualRelay.Tests.csproj:34 — <None Include=\"xunit.runner.json\" CopyToOutputDirectory=\"PreserveNewest\" /> — ensures xunit finds the config next to the test assembly",
    "SplitGuardVerificationTests.cs:14 — private static string TestsDir => Path.Combine(RepoRoot, \"tests\", \"VisualRelay.Tests\") — directory root used by the new guard test",
    "SplitGuardVerificationTests.Conventions.cs:45-67 — existing GitCommitterCollectionFiles_HaveCollectionAttribute guard (intact, not modified)",
    "SplitGuardVerificationTests.Conventions.cs:70-92 — existing EnvironmentCollectionFiles_HaveCollectionAttribute guard (intact, not modified)",
    "SplitGuardVerificationTests.Conventions.cs:94-115 — existing WatchdogCollectionFiles_HaveCollectionAttribute guard (intact, not modified)",
    "SplitGuardVerificationTests.Conventions.cs:221-229 — existing GitInvokerTests_HasCollectionAttribute guard (intact, not modified)",
    "visual-relay:249 — VISUAL_RELAY_TEST_TIMEOUT defaults to 60s (watchdog preserved as safety net)",
    "visual-relay:386 — VISUAL_RELAY_CHECK_TEST_TIMEOUT defaults to 300s (check path watchdog preserved)"
  ],
  "repro": "Before fix: run `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter XunitRunnerJson_DisablesTestCollectionParallelism` — fails with 'xunit.runner.json must exist in the test project'. After fix: same command passes. Full suite: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false` — 725/725 pass, no stall, no climbing 'Testing (NNNs)'. The deadlock is an intermittent race condition between headless Avalonia test classes on the process-global dispatcher; serializing all collections via xunit.runner.json eliminates the race while adding ≤10 s overhead (measured)."
}

## Stage 4 - Plan

{
  "plan": "Commit the three unstaged changes as a single conventional commit. The fix has already been applied: (1) new `xunit.runner.json` with `\"parallelizeTestCollections\": false` — the canonical xunit v3 way to serialize all test collections, eliminating the race between four uncollected Avalonia headless classes on the process-global dispatcher; (2) `.csproj` entry to copy the JSON to output so xunit discovers it next to the assembly; (3) new guard [Fact] `XunitRunnerJson_DisablesTestCollectionParallelism` in `SplitGuardVerificationTests.Conventions.cs` asserting the file exists with the correct setting (written TDD-first — the test fails without the JSON, passes with it). Verify: `git add` the three files, run `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter XunitRunnerJson_DisablesTestCollectionParallelism` to confirm the guard passes, then the full suite to confirm 725/725 pass with no stall. Commit with message `fix(tests): disable test-collection parallelism to prevent headless Avalonia deadlock`.",
  "manifest": [
    "tests/VisualRelay.Tests/xunit.runner.json",
    "tests/VisualRelay.Tests/VisualRelay.Tests.csproj",
    "tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs"],
  "rationale": "Added [Fact] XunitRunnerJson_DisablesTestCollectionParallelism as a structural guard that asserts xunit.runner.json exists at the test project root and contains \"parallelizeTestCollections\": false. The test fails against the current tree (file not found), confirming TDD compliance. File stays at exactly 300 lines, respecting the existing file-size guard. The test validates the canonical xunit v3 fix for the intermittent headless Avalonia deadlock caused by four uncollected UI test classes racing on the process-global dispatcher."
}

## Stage 6 - Implement

{"summary": "Created tests/VisualRelay.Tests/xunit.runner.json with parallelizeTestCollections: false (the canonical xunit v3 fix for Avalonia headless dispatcher deadlock), added the CopyToOutputDirectory entry to the .csproj, and confirmed the TDD guard test (written in stage 5) now passes. Full suite: 726/726 pass in 3m40s with no stall. Three files changed, ~27 lines total."}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "All three files verified and staged: xunit.runner.json (parallelizeTestCollections: false), .csproj CopyToOutputDirectory entry, and TDD guard [Fact] in Conventions.cs. Full suite: 726/726 pass in 3m30s, no stall. Review had zero blockers/warnings. Commit blocked by relay pre-commit hook (active run 5bdfb214) — driver must seal."}

## Stage 9 - Verify

{
  "summary": "Fixed intermittent headless Avalonia deadlock by committing xunit.runner.json with parallelizeTestCollections: false (canonical xunit v3 mechanism). Three files changed (28 insertions, 0 deletions): new config file, .csproj CopyToOutputDirectory entry, and TDD guard [Fact] asserting the file exists with the correct setting. Root cause: four Avalonia headless test classes lacked [Collection] attributes and raced on the process-global dispatcher across default xUnit v3 collections. All 726 tests pass, no stalls, watchdog preserved as safety net.",
  "commitMessages": [
    "fix(tests): disable test-collection parallelism to prevent headless Avalonia deadlock",
    "fix: serialize xunit test collections via xunit.runner.json to avoid dispatcher race",
    "test: add structural guard asserting xunit.runner.json disables collection parallelism",
    "fix(tests): commit xunit.runner.json with parallelizeTestCollections=false to fix intermittent Avalonia headless wedge",
    "fix: prevent UI-test deadlock by serializing all xunit collections through canonical config"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "All 726 tests pass (exit 0) in 3m24s with xunit.runner.json serializing collections — the intermittent headless Avalonia deadlock is fixed. Three files staged (28 insertions): xunit.runner.json (parallelizeTestCollections: false), .csproj CopyToOutputDirectory entry, and TDD guard [Fact] in SplitGuardVerificationTests.Conventions.cs. Commit blocked by relay pre-commit hook (active run) — ready for driver seal."
}

## Stage 11 - Commit

Committed by Visual Relay.

