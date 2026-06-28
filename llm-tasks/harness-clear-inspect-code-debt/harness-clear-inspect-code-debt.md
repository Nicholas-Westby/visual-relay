# Harness: clear the inspect-code findings so `./visual-relay check` is green

## Problem
`./visual-relay check` fails at its **inspect-code zero-findings gate** (step 6, before the
suite/screenshot steps). `HEAD` carries ~54 findings, most in files untouched by recent work
(pre-existing debt) plus 3 `MergeIntoPattern` suggestions in
`tools/VisualRelay.Guards/RealBuildSubprocessGuard.cs`. The other check steps (file-size,
shell-size, source-enumeration, format, build, suite, screenshot) pass.

## Important: this gate is NOT enforced by the per-task verify
The per-task verify is `guardCmd` (source-enumeration + file-size + format) + `testCmd` (the
suite) — it does **not** run inspect-code. So a green suite does NOT prove inspect-code is
clear. The implementing agent must therefore **run the inspect-code step itself** (the same
one `./visual-relay check` runs) to enumerate the findings and to confirm zero remain — do
not rely on the suite verify to catch this.

## What to do
1. Enumerate the findings: run the inspect-code analysis used by `./visual-relay check` (its
   SARIF step) and list every finding.
2. Fix each **behavior-preservingly** (prefer the analyzer's suggested refactor — e.g. merge
   into pattern, simplify, remove dead code). Where a finding is a deliberate false positive,
   suppress it narrowly with a justified inline/attribute suppression, not a blanket disable.
   Make focused commits grouped by area (the findings span ~25 files).
3. After each batch, re-run the full suite to confirm **no behavioral regression** (exit code +
   stored output), and re-run inspect-code to confirm the count is dropping toward zero.

## Done when
- The inspect-code step reports **zero findings**, so `./visual-relay check` is fully green.
- The full suite stays GREEN under nono (no regressions), validated by exit code.
- Changes are behavior-preserving; Conventional Commit subjects. General-purpose: no new
  language/VR-specific assumptions introduced while cleaning.

## Note
Lower priority than `harness-revert-split-verify` (that one touches the live verify gate; this
is dev-gate hygiene). Best done as its own pass since it touches many files.
