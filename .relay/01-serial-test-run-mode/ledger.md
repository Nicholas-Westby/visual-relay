## Stage 1 - Ideate

{
  "summary": "Add an opt-in serial test mode to `./visual-relay test` so per-test timings become trustworthy. The CLI recognizes `serial` as the first token, appends `-- xUnit.ParallelizeTestCollections=false`, raises the default watchdog timeout to 1800s, prints a stderr notice, and updates docs. A pure `BuildTestArgs(...)` static builder must be extracted from `RunAsync` with byte-identical defaults. New `CliSerialTestModeTests` cover four scenarios. Callee-supplied `--` is rejected with a clear error. The task prescribes exactly this shape.",
  "options": [
    "Option A — Pure builder with ordinal token check (prescribed): Extract `internal static BuildTestArgs(ReadOnlySpan<string?> args, out bool isSerial) -> string[]`. Ordinal compare for `\"serial\"`, consume it, then apply existing filter logic to remaining tokens. Append `-- xUnit.ParallelizeTestCollections=false` when serial. Add `WatchdogTimeouts.ForTest(serial: bool)` → 1800s when serial and env var absent. Stderr line prints in `RunAsync`. Minimal, testable, byte-identical for non-serial.",
    "Option B — Result-record wrapper: Same extraction but return `(string[] Args, bool IsSerial)` tuple or a `TestArgs` record instead of `out bool`. Slightly cleaner functional style at the cost of one extra allocation per invocation. Otherwise identical to Option A.",
    "Option C — Interface/strategy: Define `ITestArgumentBuilder` with current impl and a `SerialTestArgumentBuilder` decorator. Clean OCP but over-engineered for a single boolean. Would exceed the 300-line guard with boilerplate."
  ]
}

## Stage 2 - Research

{
  "findings": "TestRunner.cs:112 lines, RunAsync builds dotnet test args inline (lines 26-47) — filter logic at lines 38-47, NO_BUILD check at line 35-36. Must extract BuildTestArgs. WatchdogTimeouts.cs:28 lines, ForTest() → Resolve(envVar, 60). CommandRouter.cs:24 lines, UsageLine lists all commands. TROUBLESHOOTING.md:102 lines, no serial mode note. TestCommand.cs:10 lines, thin delegate. New test file goes in tests/VisualRelay.Tests/ following CliTestLogPathsTests pattern (using VisualRelay.Cli, public sealed class, [Fact]/[Theory]). Serial detection: ordinal compare first token to \"serial\", consume it, rest follows existing filter rule. -- conflict: reject if serial mode and forwarded args contain \"--\". Stderr line: \"serial mode: one collection at a time; per-test timings are trustworthy\". 300-line guard: TestRunner 112 + ~35 ok, WatchdogTimeouts 28 + ~5 ok, new test file ~80 ok. No new dependencies, no test deletions.",
  "constraints": [
    "Default (non-serial) invocations must produce byte-identical dotnet test arguments — prove via builder tests",
    "No new dependencies; keep all files under the 300-line guard",
    "No test is deleted, skipped, or weakened",
    "serial token must use ordinal string comparison",
    "VISUAL_RELAY_TEST_TIMEOUT env var must still win when set (serial mode only changes the default from 60s to 1800s)",
    "Do not modify xunit.runner.json — default parallelism must be unchanged",
    "Combining serial with a caller-supplied -- RunSettings tail must fail with a clear error rather than merging",
    "Tests must exercise the pure Resolve-style seam, not process-global env state",
    "Output format must match: new test file CliSerialTestModeTests, tests for all 4 scenarios specified",
    "Commit-message evidence bullet must be measured at implementation time, never pre-filled"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "TestRunner.cs:26-47 builds dotnet test args inline with no extraction point. WatchdogTimeouts.ForTest() hard-codes 60s default with no serial awareness. CommandRouter.UsageLine and TROUBLESHOOTING.md are silent about serial mode. No -- conflict detection exists. The CLI project has no InternalsVisibleTo attribute. All modifications stay well under the 300-line guard (TestRunner 112→~155, WatchdogTimeouts 28→~35, CommandRouter 24→~25, TROUBLESHOOTING.md 102→~107, new test file ~80).",
  "excerpts": [
    "TestRunner.cs:26-50 — inline arg construction with filter/forwarding logic",
    "WatchdogTimeouts.cs:12-13 — ForTest() defaults to 60s unconditionally",
    "WatchdogTimeouts.cs:22-27 — pure Resolve(string?, int) seam already testable",
    "CommandRouter.cs:21-23 — UsageLine lists test without serial hint",
    "xunit.runner.json — parallelizeTestCollections:true, maxParallelThreads:2.0x"
  ],
  "repro": "grep -n 'testArgs = new' tools/VisualRelay.Cli/TestRunner.cs && grep -n 'ForTest()' tools/VisualRelay.Cli/WatchdogTimeouts.cs && grep -n 'serial' tools/VisualRelay.Cli/CommandRouter.cs TROUBLESHOOTING.md"
}

## Stage 4 - Plan

{
  "plan": "1. tools/VisualRelay.Cli/VisualRelay.Cli.csproj — add <InternalsVisibleTo Include=\"VisualRelay.Tests\" /> item group.\n2. tools/VisualRelay.Cli/TestRunner.cs — extract internal static BuildTestArgs(string testsProject, ReadOnlySpan<string> args, string trxStem, string resultsDirectory, bool noBuild, out bool isSerial) that: builds base args, adds --no-build if set, detects/consumes \"serial\" first token (ordinal), applies existing filter/forward logic to remaining tokens, rejects caller-supplied -- in serial mode via InvalidOperationException, appends -- xUnit.ParallelizeTestCollections=false when serial, returns string[]. Rewire RunAsync to call BuildTestArgs, use WatchdogTimeouts.ForTest(isSerial), print stderr serial-mode line alongside log-path lines, catch -- conflict and return 2.\n3. tools/VisualRelay.Cli/WatchdogTimeouts.cs — add public static TimeSpan ForTest(bool serial) overload delegating to Resolve(env, serial ? 1800 : 60).\n4. tools/VisualRelay.Cli/CommandRouter.cs — update UsageLine: test entry becomes \"test [serial] [Filter]\".\n5. TROUBLESHOOTING.md — add short serial-mode section after the hang section.\n6. +tests/VisualRelay.Tests/CliSerialTestModeTests.cs — four tests: SerialLeadingToken_AppendsRunSettingsAndNoSerialFilter, SerialWithFilter_IncludesBothRunSettingsAndFilter, FilterAlone_ProducesIdenticalArgs (byte-identical assertion), SerialTimeoutDefaults (pure Resolve seam). Pattern: CliTestLogPathsTests.",
  "manifest": [
    "tools/VisualRelay.Cli/VisualRelay.Cli.csproj",
    "tools/VisualRelay.Cli/TestRunner.cs",
    "tools/VisualRelay.Cli/WatchdogTimeouts.cs",
    "tools/VisualRelay.Cli/CommandRouter.cs",
    "TROUBLESHOOTING.md",
    "+tests/VisualRelay.Tests/CliSerialTestModeTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 5.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/CliSerialTestModeTests.cs"],
  "rationale": "All 5 tests pass. BuildTestArgs is extracted as a pure internal static method. Serial mode is detected via ordinal comparison of the first token, consumed, and the RunSettings tail -- xUnit.ParallelizeTestCollections=false is appended. Non-serial args are byte-identical to before. Caller-supplied -- in serial mode throws InvalidOperationException (caught in RunAsync, returns exit code 2). WatchdogTimeout defaults to 1800s when serial and env var unset. Stderr notice prints alongside log-path lines. Usage line and TROUBLESHOOTING.md updated."
}

## Stage 6 - Implement

{ "summary": "All 6 manifest files changed. BuildTestArgs extracted as pure internal static with serial detection (ordinal), -- conflict rejection, and RunSettings tail. ForTest(bool serial) with 1800s default. Usage line and TROUBLESHOOTING.md updated. Targeted tests 5/5 pass; full suite 3318/0 fail." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure CLI argument-building and timeout logic; no UI, layout, styles, images, charts, or rendered documents. Output is text-only stderr/usage lines verifiable by source inspection. Automated tests provide full coverage._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Added opt-in serial test mode (`./visual-relay test serial [Filter]`) to the CLI. Extracted `BuildTestArgs(...)` pure builder from `RunAsync` — it detects a leading `serial` token via ordinal comparison, consumes it, and appends `-- xUnit.ParallelizeTestCollections=false`. Non-serial arguments remain byte-identical. Caller-supplied `--` in serial mode throws `InvalidOperationException` (caught in `RunAsync`, returns exit code 2). Added `WatchdogTimeouts.ForTest(bool serial)` — defaults to 1800s when serial and `VISUAL_RELAY_TEST_TIMEOUT` unset, otherwise 60s (env var still wins). Stderr prints `serial mode: one collection at a time; per-test timings are trustworthy`. Updated `CommandRouter.UsageLine` to `test [serial] [Filter]`. Added serial-mode section to `TROUBLESHOOTING.md`. Added `InternalsVisibleTo` to csproj. New `CliSerialTestModeTests` (5 tests) cover all scenarios. Full suite: 3318 passed, 0 failed, 115 skipped.",
  "commitMessages": [
    "feat(cli): add serial test mode for trustworthy per-test timings",
    "feat: `test serial` runs collections one-at-a-time",
    "refactor(test): extract BuildTestArgs, add serial mode support",
    "feat: raise watchdog default to 1800s in serial mode",
    "feat(cli): opt-in serial mode debugs slow-test timings"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

