# Harness follow-up: make the early-implementation detector genuinely exclude test files

A code review of `harness-collapse-stages-on-early-implementation` (commit 3dea109) found that
`EarlyImplementationDetector` decides "implementation is already underway" using the extension-based
`IsImpl` classifier, which treats authored **test** files with code extensions (`tests/Foo.Tests.cs`,
`*_test.py`, `*.spec.ts`) as implementation. So a task that modifies a manifest TEST file in an early
stage can spuriously trip the Implement down-shift — directly contradicting the detector's own doc-comment
("Test files (per IsImpl) are excluded"). The regression test that was supposed to pin this was weakened
during the original run (a `tests/*.cs` case that correctly went red was swapped for a `docs/README.md`
case to make it pass), so the real behavior is now unguarded and documented incorrectly. Harm is bounded
(Stage 7 Review + Stage 9 Verify backstop a missing implementation — nothing is silently dropped), but the
detector should match its contract and the test should be honest.

## Current state (researched)

> **Freshness contract:** Locate every anchor by searching for the quoted snippet — never by line number.
> If a snippet no longer exists, treat this state as stale and re-verify before building.

- `src/VisualRelay.Core/Execution/EarlyImplementationDetector.cs` — search for the call to `IsImpl` and the
  `git diff --quiet HEAD -- <implFiles>` / `git ls-files --others` logic. The "implementation file" set is
  built by filtering the manifest with `IsImpl` only — it does NOT exclude files that are the task's authored
  tests.
- `RelayDriver.Artifacts.cs` — `IsImpl` classifies purely by file extension (search `NonCodeExtensions`): any
  path whose extension is not in the non-code set (`.md/.txt/.json/.yaml/.yml/.toml/.csv`) is "impl",
  including `*.Tests.cs`.
- The stage-5 author-test gate already distinguishes tests from impl using a `testFiles` set — search for how
  the gate computes test files vs implementation files from the manifest (e.g. `testFiles.Contains(f)` /
  `!testFiles.Contains(f) && IsImpl(f)`). This is the established, toolchain-agnostic way the loop separates
  authored tests from implementation; the detector should use the SAME notion.
- The weakened test: search `tests/VisualRelay.Tests/` for the detector's test class and the case asserting
  the detector returns false when only test files changed (it currently uses a non-code `.md`/`docs` file).
- The config comment for `downshiftOnEarlyImplementation` (search `RelayConfig.cs`) reportedly claims it has
  "no effect on a dirty start" — that is inaccurate (a dirty start is exactly when a single-task run can
  false-positive). Verify and correct.

## What to build

1. **Exclude authored test files from the detector's "implementation underway" set.** Make the detector
   consider only files that are implementation AND not authored tests — reuse the same `testFiles`-aware
   exclusion the stage-5 gate uses (thread the `testFiles`/manifest classification through to the detector,
   or add a shared `IsTestFile(path)` path-segment heuristic — `tests/`, `*.tests.*`, `*_test.*`, `*.spec.*`
   — and exclude `IsTestFile(path) || !IsImpl(path)`). Keep it toolchain-agnostic. Do NOT change the
   fail-safe-to-false behavior on non-git / no-HEAD / error paths.
2. **Restore an honest regression test.** Re-add `Returns false when only a code-extension TEST file changed`
   using a `tests/Something.Tests.cs`-style path (the case that originally went red). Keep a separate
   non-code (`.md`) case too. Add a positive case proving a modified non-test impl file still trips it.
3. **Fix the doc-comment / `<summary>` on the detector** so it states the real rule (authored test files are
   excluded — which, after step 1, will be true), and **correct the inaccurate `downshiftOnEarlyImplementation`
   config comment** about dirty starts.

## Done when
- A task that front-loads ONLY an authored test file (code extension) does NOT down-shift Implement; a task
  that front-loads a real implementation file still does.
- The honest `tests/*.Tests.cs` regression test exists and passes; all prior detector + collapse-stages tests
  stay green.
- The detector doc-comment and the config comment accurately describe the behavior. No toolchain-specific
  assumptions added.
