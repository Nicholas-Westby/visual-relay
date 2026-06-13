## Stage 1 - Ideate

{
  "summary": "Two independent fixes are needed: (1) route `check`'s raw `dotnet test` through the existing `_timeout_watchdog` (same as `test` already does), and (2) make `AppIconTests.AppIcon_ContainsMultipleResolutions` skip (not error) when `magick` is absent, plus add `imagemagick` to the nix devshell so it runs in the managed environment. The stall fix is the gate — without it `check` can never pass, blocking the two follow-up tasks.",
  "options": [
    "Option A — Reuse `_timeout_watchdog` for `check` with a higher default cap (e.g. 300 s / 5 min), plus add `--blame-hang` automatically gated behind an env flag to avoid unbounded `TestResults/` dumps. For the icon test: resolve `magick` from `PATH` and call `Assert.Skip(...)` when absent; add `imagemagick` to `flake.nix` packages. This is the smallest, most consistent change — zero new watchdog mechanisms, the existing tested code paths `visual-relay:226-284` are reused verbatim.",
    "Option B — Same `_timeout_watchdog` reuse for `check` (identical to Option A on that front), but for the icon test: instead of `Assert.Skip`, use conditional compilation or a runtime `[Fact(Skip = …)]` driven by a `PATH` probe at static initialization. Adds `imagemagick` to devshell same as A. Slightly more ceremony but keeps the skip decision visible at the class/fixture level rather than inside the test body.",
    "Option C — For the stall: embed a lightweight inline timeout inside `check`'s `dotnet test` call (e.g. `timeout 300` or a PowerShell equivalent) instead of routing through `_timeout_watchdog`. For the icon test: same as A. This avoids coupling to the watchdog's process-group-kill logic but duplicates timeout logic, diverges from `test`'s proven approach, and misses the existing actionable `--blame-hang` message. Not recommended — it's less consistent and less tested."
  ]
}

## Stage 2 - Research

{ "findings": "Two independent issues found: (1) `visual-relay` line 356 calls `dotnet test` raw without the `_timeout_watchdog` wrapper (which already exists at lines 226-284 for the `test` subcommand), allowing a wedged suite to stall unbounded (up to 34 min). (2) `AppIconTests.cs` line 48 hardcodes `/opt/homebrew/bin/magick` and throws `Win32Exception` when `magick` is absent — the test should skip instead. `flake.nix` (lines 20-30) lacks `imagemagick` in packages. No `xunit.runner.json` or `CollectionBehavior` attribute exists. `Assert.Skip(...)` is already used in `VisualRelayTestCommandTimeoutTests.cs` for dynamic skips. `TestResults/` is gitignored. Watchdog test infrastructure in `VisualRelayTestCommandTimeoutTests.cs` (RunWatchdogTestAsync) is reusable for testing the `check` path.", "constraints": ["Reuse `_timeout_watchdog` verbatim for `check` — no new watchdog mechanism", "Default cap for `check` must be higher than `test`'s 60s (e.g., 5 min / 300s) since `check` builds first", "`magick` must be resolved from `PATH`, never hardcoded to `/opt/homebrew/bin/magick`", "Use `Assert.Skip(...)` when `magick` absent (follow existing pattern in VisualRelayTestCommandTimeoutTests.cs)", "Add `imagemagick` to `flake.nix` packages so the test runs in the nix devshell", "Watchdog's actionable `--blame-hang` message must reach operator on `check` path (automatic via reuse)", "`check` must exit non-zero (124) on timeout (flag-file mechanism already handles this)", "Total changed files < 300 lines", "TDD: write failing test first before production changes"] }

## Stage 3 - Diagnose

{
  "evidence": "Two independent failures compound to make `./visual-relay check` unusable: (1) The stall: `visual-relay:356` calls `dotnet test` raw without the `_timeout_watchdog` wrapper that already exists at lines 226-284 and is used by the `test` subcommand at line 316. When the suite's known parallel-headless Avalonia deadlock triggers (TROUBLESHOOTING.md:5-27 — documented: xUnit class-parallelism collides with Avalonia's process-global app/dispatcher; HeadlessUnitTestSession banned in BannedSymbols.txt but global-state races persist), nothing caps the hung dotnet process. The suite sat at 'Testing (2025.0s)' for ~34 minutes until Ctrl-C. The watchdog already self-tests via VisualRelayTestCommandTimeoutTests.cs (RunWatchdogTestAsync) and exits 124 on timeout with actionable --blame-hang guidance — it just isn't wired to `check`. (2) AppIconTests.cs:48 hardcodes FileName = '/opt/homebrew/bin/magick'. flake.nix:20-30 lacks imagemagick in packages. When magick is absent, process.Start() throws Win32Exception at ~104 ms — the test errors instead of skipping. Assert.Skip(...) pattern already exists in VisualRelayTestCommandTimeoutTests.cs:25,30. The other four AppIconTests are pure file/XML checks and unaffected. The magick failure is NOT the stall cause (it failed instantly); the stall is a separate headless UI deadlock that went uncapped because check had no watchdog.",
  "excerpts": [
    "visual-relay:356 — check calls dotnet test raw (no watchdog):\n    dotnet test \"$SCRIPT_DIR/tests/VisualRelay.Tests/VisualRelay.Tests.csproj\" -m:1 -p:UseSharedCompilation=false",
    "visual-relay:316 — test already wraps via watchdog:\n    _timeout_watchdog dotnet test \"$SCRIPT_DIR/tests/VisualRelay.Tests/VisualRelay.Tests.csproj\" -m:1 -p:UseSharedCompilation=false \"$@\"",
    "visual-relay:226-284 — _timeout_watchdog function: default timeout 60s (line 227), launches command in own process group (set -m, line 234), on timeout prints 'visual-relay: test timed out after Ns' + TROUBLESHOOTING.md + --blame-hang guidance (lines 246-249), kills process group with TERM→KILL escalation (lines 250-252), flag-file overrides exit code to 124 (lines 278-280)",
    "TROUBLESHOOTING.md:5-27 — documents the hang: 'If it sits at Testing (NNNs) with the counter climbing and no test ever completing, a test has deadlocked'; attributes to parallel Avalonia headless classes colliding on the process-global dispatcher",
    "TROUBLESHOOTING.md:17-21 — 'two headless classes overlapping can wedge each other'",
    "BannedSymbols.txt — bans HeadlessUnitTestSession with explanation: 'xUnit's parallel test collections start two sessions at once — they deadlock (the whole suite hangs at \"Testing (Ns)\" with nothing completing)'",
    "AppIconTests.cs:48 — FileName = \"/opt/homebrew/bin/magick\", hardcoded Homebrew path. When missing, process.Start() throws Win32Exception (test errors, doesn't skip)",
    "flake.nix:20-30 — packages: dotnet-sdk_10 git bash icu openssl zlib nono uv python313. No imagemagick.",
    "VisualRelayTestCommandTimeoutTests.cs:25,30 — existing Assert.Skip(...) pattern: 'Assert.Skip(\"Watchdog uses Unix job control...\")' and 'Assert.Skip($\"visual-relay not found at {VisualRelayPath}\")'",
    "VisualRelayTestCommandTimeoutTests.cs:224-294 — RunWatchdogTestAsync helper: extracts _timeout_watchdog from visual-relay, runs bash test scripts, 15s CancellationToken safety cap. Reusable for testing check path.",
    ".gitignore:6 — TestResults/ is gitignored (safe to use --blame-hang)"
  ],
  "repro": "Stall repro: Run `./visual-relay check` on this machine. The dotnet test at line 356 will run the full suite. If the parallel-headless deadlock triggers (which it does intermittently — observed on 2026-06-13), the run will stall at 'Testing (NNNs)' with no timeout, burning minutes/hours until manual Ctrl-C. Contrast with `./visual-relay test` which self-kills in 60s via the watchdog.\n\nMagick repro: Run `./visual-relay check` or `dotnet test --filter AppIcon_ContainsMultipleResolutions`. With magick absent from PATH and /opt/homebrew/bin/magick nonexistent, the test errors with 'System.ComponentModel.Win32Exception: An error occurred trying to start process '/opt/homebrew/bin/magick' … No such file or directory'. The test should skip instead."
}

## Stage 4 - Plan

{
  "plan": "Two independent fixes, TDD-first:\n\n1. **Cap the check stall (priority):** Route `check`'s `dotnet test` through the existing `_timeout_watchdog` (visual-relay:356), same as `test` already does at line 316. Export `VISUAL_RELAY_TEST_TIMEOUT=\"${VISUAL_RELAY_CHECK_TEST_TIMEOUT:-300}\"` so the default cap is 300 s (5 min) — well above a healthy full-suite run, but a deadlocked suite self-terminates instead of burning 34+ minutes. The watchdog's existing exit-124 flag-file mechanism and actionable `--blame-hang` stderr message (lines 246-249) are reused verbatim. `--blame-hang` is NOT added to the dotnet test args (multi-GB TestResults/ dump is oppressive for everyday check); the watchdog message tells the operator how to re-run with it on-demand. Add `Check_UsesTimeoutWatchdog` test in `VisualRelayTestCommandTimeoutTests.cs` that does static analysis of the check) case body — fails against current raw dotnet test, passes after fix.\n\n2. **Make AppIconTests resilient to missing magick:** Add `FindInPath(string command)` helper to `AppIconTests.cs` that probes PATH directories for a binary. Modify `AppIcon_ContainsMultipleResolutions` to resolve `magick` from PATH (never the hardcoded `/opt/homebrew/bin/magick`) and `Assert.Skip(...)` with a clear message when absent. The other four AppIconTests (pure file/XML checks) stay unconditional. Add `AppIcon_ResolvesMagickFromPath` test that validates the resolution logic. Add `imagemagick` to `flake.nix` packages so the assertion runs inside the devshell.\n\nTDD order: (a) add `Check_UsesTimeoutWatchdog` → fails, (b) add `AppIcon_ResolvesMagickFromPath` → passes trivially but resolution test still errors, (c) implement visual-relay fix → watchdog test passes, (d) implement AppIcon guard → resolution test skips on bare host, (e) add imagemagick to flake.nix → assertion runs in devshell.",
  "manifest": [
    "visual-relay",
    "tests/VisualRelay.Tests/VisualRelayTestCommandTimeoutTests.cs",
    "tests/VisualRelay.Tests/AppIconTests.cs",
    "flake.nix"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/VisualRelayTestCommandTimeoutTests.cs",
    "tests/VisualRelay.Tests/AppIconTests.cs"
  ],
  "rationale": "TDD approach for both fixes. 1) Stall fix: Added Check_UsesTimeoutWatchdog static analysis test that fails against the raw dotnet test at old line 356, then routed check's dotnet test through _timeout_watchdog with VISUAL_RELAY_CHECK_TEST_TIMEOUT defaulting to 300s (5 min). The existing watchdog, process-group kill, exit-124 flag-file, and --blame-hang guidance are reused verbatim. 2) AppIcon fix: Added AppIcon_DoesNotHardcodeMagickPath (static analysis) and AppIcon_FindInPath_* tests, then implemented FindInPath helper, modified AppIcon_ContainsMultipleResolutions to resolve magick from PATH and Assert.Skip when absent, and added imagemagick to flake.nix. ./visual-relay check is green (700 passed, 0 failed, 0 skipped, 1m 41s) and demonstrably bounded."
}

## Stage 6 - Implement

{ "summary": "Two independent fixes land: (1) check's dotnet test now routes through _timeout_watchdog with a 300s default cap — a deadlocked suite self-terminates instead of stalling 34+ minutes. The watchdog's process-group kill, exit-124 flag-file, and --blame-hang guidance are reused verbatim. A Check_UsesTimeoutWatchdog static-analysis test guards this. (2) AppIcon_ContainsMultipleResolutions resolves magick from PATH via a new FindInPath helper and Assert.Skip()s with a clear message when absent — never again errors with Win32Exception. Added AppIcon_DoesNotHardcodeMagickPath guard test and FindInPath unit tests. imagemagick is in flake.nix packages so the assertion runs inside the devshell. ./visual-relay check: 700 passed, 0 failed, 0 skipped, 1m 36s — green and demonstrably bounded." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Review stage 7 returned verdict 'pass' with zero issues. All seven 'Done when' criteria are verified in the current code: (1) check) routes dotnet test through _timeout_watchdog with VISUAL_RELAY_CHECK_TEST_TIMEOUT defaulting to 300s, (2) Check_UsesTimeoutWatchdog static-analysis test guards this, (3) set -e ensures exit 124 fails the gate, (4) watchdog's --blame-hang message reaches the check operator via shared stderr diagnostics, (5) AppIcon_ContainsMultipleResolutions resolves magick from PATH via FindInPath and Assert.Skip()s when absent, (6) AppIcon_DoesNotHardcodeMagickPath guard test and FindInPath unit tests exist, (7) imagemagick is in flake.nix packages. ./visual-relay check is green (700 passed, 1m 36s). No fixes needed." }

## Stage 9 - Verify

{
  "summary": "Two independent fixes: (1) `check)` now routes `dotnet test` through the existing `_timeout_watchdog` with `VISUAL_RELAY_CHECK_TEST_TIMEOUT` defaulting to 300 s (5 min) — a deadlocked suite self-terminates instead of stalling 34+ minutes. The watchdog's process-group kill, exit‑124 flag-file, and actionable `--blame-hang` guidance are reused verbatim; a `Check_UsesTimeoutWatchdog` static-analysis test guards this. (2) `AppIcon_ContainsMultipleResolutions` resolves `magick` from `PATH` via a new `FindInPath` helper and `Assert.Skip()`s with a clear message when absent — never again throws `Win32Exception` from a hardcoded Homebrew path. Added `AppIcon_DoesNotHardcodeMagickPath` guard test and `FindInPath` unit tests. `imagemagick` is in `flake.nix` packages so the assertion runs inside the devshell. `./visual-relay check`: 700 passed, 0 failed, 0 skipped, ~1m 36s — green and demonstrably bounded.",
  "commitMessages": [
    "fix(check): cap dotnet test via the timeout watchdog with a 5 min default",
    "fix(ci): route check's raw dotnet test through _timeout_watchdog to prevent unbounded stalls",
    "test(icon): skip resolution check when magick is absent from PATH",
    "fix: bound the check command and harden AppIconTests against missing ImageMagick",
    "ci: add imagemagick to nix devshell and guard icon resolution behind PATH probe"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Verified: `./visual-relay check` exits 0 with 700 passed, 0 failed, 0 skipped in ~1m 36s. The check) case routes through `_timeout_watchdog` with VISUAL_RELAY_CHECK_TEST_TIMEOUT defaulting to 300s (5 min). AppIcon_ContainsMultipleResolutions resolves magick from PATH via FindInPath — magick is present at /opt/homebrew/bin/magick so the test ran and passed (0 skipped; skip-when-missing guard is in place for bare hosts). imagemagick is in flake.nix packages. The Check_UsesTimeoutWatchdog and AppIcon_DoesNotHardcodeMagickPath static-analysis tests are present and pass. The suite is bounded and green.",
  "amendManifest": []
}

## Stage 11 - Commit

Committed by Visual Relay.

