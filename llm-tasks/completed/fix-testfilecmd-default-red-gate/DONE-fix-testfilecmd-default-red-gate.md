# Stop the bun-specific testFileCmd default from silently neutering the stage-5 red gate

`RelayConfigLoader.Defaults` hardcodes `TestFileCommand: "bun test {files}"`
(src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:11). Stage 5's author-tests red gate
(RelayDriver.Stage5.cs:96-122) runs that command when the stage-5 agent also produced
implementation files: the gate strips the implementation, runs the authored test files, and
requires RED (tests must fail before implementation). On any non-bun repo without an explicit
`testFileCmd` — most real repos (Go, Python, C#, Ruby, plain-npm JS) — `bun test <files>` fails
because bun can't run those tests at all. That failure is indistinguishable from "tests correctly
failed pre-implementation": the gate reports red, passes, and its protective assertion becomes a
silent no-op. It can also never produce the "author-tests passed after implementation files were
stripped" flag on such repos. Observed across this campaign: every non-bun target ran with the
meaningless default (operators had to learn to override testFileCmd manually).

## What to build

1. **Fall back to the full test command when testFileCmd is not explicitly configured.** Change the
   default to unset/null semantics: when the repo's `.relay/config.json` has no `testFileCmd`, the
   stage-5 red gate uses `testCmd` (the whole suite — slower but correct: authored failing tests
   make the full suite red just the same). The `"bun test {files}"` literal must survive ONLY where
   a bun toolchain was actually detected (TestCommandDetector already knows; init may still write it
   explicitly for bun repos).
2. **Distinguish "runner failed to start" from "tests ran and failed".** In the red-gate result
   classification, treat command-not-found (exit 127), usage errors from the runner itself, and
   zero-tests-collected outputs as GATE-INFRA failures, not red: emit a warn event
   (`author_test_gate_unusable`) naming the command and output tail, and skip the gate's
   pass/fail assertion rather than passing it vacuously. Keep this heuristic general: exit code +
   "no tests found/collected" patterns already surfaced by the runner result — no per-toolchain
   parsing.
3. Config docs/comment: `testFileCmd` remains an optimization ({files}-scoped, faster); absence now
   means "use testCmd".

## Tests (red first)

- Config default test: absent testFileCmd → gate command equals testCmd (assert via the stage-5
  gate seam/tests' existing pattern); explicit testFileCmd honored verbatim.
- Gate classification test: a gate run returning 127 (command not found) produces the
  `author_test_gate_unusable` warn event and does NOT count as a satisfied red gate.
- Regression: bun-detected repos keep `bun test {files}` via init/detector paths.

## Verification

- `./test.sh` fully green including the new tests.
