# Task: Anchor verify flag reasons on the failing test line, not the log tail

When a stage-10/11 verify goes red, everything a human sees — the LATEST RUN
FAILED banner, the flag reason, NEEDS-REVIEW — is built from
`ExtractFailureReason`'s distillation of the test log. Test frameworks that
run suites concurrently (Swift Testing in the patternsmith incident) print
the failing test mid-log and keep streaming passing lines after it, no
current failure anchor matches their lowercase `failed …with N issue` rows,
and the distiller falls back to the last-600-chars tail. The surfaced
"reason" was `…fter 0.198 seconds.` — a mid-word fragment of a PASSING
test's duration line — while the actual failure never appeared in the GUI.
Make the first line of a distilled reason be the failing line itself.

### Evidence (2026-07-19 patternsmith `better-explain-the-1-stuff` flag)

- Flag reason shown in GUI + NEEDS-REVIEW: `verify failed after 3 fix-verify
  attempts — last: …fter 0.198 seconds. (see .relay/better-explain-the-1-
  stuff/stage11-attempt3.verify-output.txt)`. The fragment is the tail of
  `􁁛 Test contextSnippetExtractsSurroundingLines() passed after 0.198
  seconds.` — a passing test.
- The real failure sat mid-log in the 746-line verify output: `􀢄  Test
  everySourceFileUnder200Lines() recorded an issue at
  FileSizeTests.swift:19:9: Expectation failed: (offenders →
  ["Sources/RegexUI/ViewModels/AppModel.swift — 210 lines"]).isEmpty →
  false`, plus the run summary `􀢄  Test run with 244 tests in 25 suites
  failed after 0.437 seconds with 1 issue.` near EOF.
- `ProcessRunners.Diagnostics.cs:32` `ExtractFailureReason` →
  `DistillFailure`. Strong anchors (`:177-186`): `cannot find binary path`,
  `command execution failed`, `command not found`, line-start `Failed `,
  uppercase `\bFAIL\b` (bun/jest). Weak anchors (`:191-193`): whole-word
  `error|fatal|traceback|exception|critical`. Swift Testing failure rows
  (`recorded an issue`, `Expectation failed`, lowercase `failed after …
  with 1 issue`) match NONE of these — lowercase fail is deliberately
  unanchored so a benign `0 failed` summary can never anchor.
- With no anchor, `DistillFailure` (`:94-97`) joins ALL surviving lines and
  `TrimForTail` (`ProcessRunners.Helpers.cs:125-128`) keeps the LAST 600
  chars with a `…` prefix, entering mid-line. `RelayDriver.VerifyFix.cs:236-
  238` then puts only the reason's FIRST line (capped at 200 chars) into the
  flag — so even though the `Test run … failed` summary was inside the tail
  window, the surfaced line was the mid-word passing-test fragment.
- Anchoring alone is NOT enough: even when an anchor matches,
  `kept.Skip(firstFailure)` → `TrimForTail` still keeps the tail of the
  anchored block. In a concurrent log the failure line is followed by
  hundreds of passing lines, so tail-keeping would evict it again. The head
  of the anchored block must win.
- Downstream consumers of the same reason string:
  `RelayDriver.VerifyObservability.cs:35-36` (verify events + artifacts),
  `RelayDriver.VerifyFix.cs:186,189-192` (per-attempt signatures — see
  companion task `08-identical-failure-advisory-robust-signatures`),
  `RelayDriver.VerifyFix.cs:232-244` (flag reason / NEEDS-REVIEW),
  `RelayDriver.FailureOutput.cs:28` (fix-verify agent prompt tail). Fixing
  the distiller fixes all four.

### What to build

1. **Failure-row anchors for concurrent-runner output.** Extend the strong
   marker list with shapes that match real failure rows and cannot match
   benign summaries: `recorded an issue`, `Expectation failed`, a
   `failed after <digits…> with <digits> issue(s)` shape, and a
   `Test run … failed` summary shape. Follow the existing philosophy — a
   marker list (like the bun/jest `\bFAIL\b` precedent), not a
   per-framework log parser. `0 failed`, `0 errors`, and `passed after …`
   must remain unanchorable, same as the existing contract documented at
   `ProcessRunners.Diagnostics.cs:183-186`.
2. **Head-first extraction when anchored.** When an anchor exists, the
   distilled reason starts AT the first anchor line and keeps FOLLOWING
   lines up to the char budget (head of the anchored block), instead of
   `TrimForTail`'s tail-keep. The unanchored fallback keeps today's tail
   behavior. Result: the reason's first line is the failing line, so the
   200-char flag cut, the NEEDS-REVIEW headline, and the GUI banner all
   stay informative without further changes.
3. **Multiple failing rows (optional).** When several distinct anchor lines
   exist (multiple failing tests), prefer a budget-capped join of the anchor
   lines over one contiguous block, so the reason names each failing test.
   Skip if it complicates the head-first change — one failing line is
   already the win.
4. **Fixture regression test.** Reconstruct the incident's log shape as a
   test fixture (interleaved `passed after` lines, one mid-log
   `recorded an issue` block, a passing tail, `Test run … failed` near EOF,
   a trailing `{ "session": }` epilogue) and assert the distilled reason's
   first line names `everySourceFileUnder200Lines`.

### Constraints

- Marker extension only — no per-framework parsers. Do not rely solely on
  the `􀢄`/`􁁛` glyphs (private-use SF Symbols); word-shape markers must
  carry the match on their own.
- Existing anchors and their tests keep passing. `BuildNonzeroExitReason`
  shares `DistillFailure` — its marker-vs-prompt-echo behavior must not
  regress.
- `ProcessRunners.Diagnostics.cs` is ~294 lines against the 300-line file
  guard — the new markers/extraction likely need a partial-class split.
- Reason strings stay within existing budgets (600-char distill cap,
  200-char flag first-line cap); no consumer signature changes.

### Tests (red first)

- Incident fixture (above): first line of the reason contains
  `everySourceFileUnder200Lines` and does not contain `passed after`; the
  `(see …verify-output.txt)` pointer behavior is unchanged.
- Anchored-but-buried regression: a log whose failure line is followed by
  more than 600 chars of passing lines still yields the failure line FIRST.
- Benign guard: output containing only `Executed 0 tests, with 0 failures`
  plus passing lines anchors nothing and keeps today's tail fallback.
- Existing strong-marker fixtures (`command not found`, uppercase `FAIL`)
  behave exactly as before.

### Verification

- `./visual-relay check` fully green.
- Manual (optional): force a mid-log test failure in a sandbox project, run
  a verify, confirm the GUI banner and NEEDS-REVIEW name the failing test.
