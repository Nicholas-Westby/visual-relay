# Review what Fix-verify changed before it is committed

Review (stage 7) and Visual-review (stage 8) run before Verify (10). Whatever
Fix-verify (11) edits afterwards goes straight to Commit (12) without anyone
looking at it. On i18next both tasks' Fix-verify agents edited the project's
test configuration (a time zone pin in `vitest.config.mts`) to get a green
suite; no reviewer ever saw that file, and one of the two commits carried it.

## Evidence

Windows arm, i18next, 2026-09-14 (0.367 to 0.372); evidence on the Windows
PC at `~/vr-eval/i18next-run1-evidence`, the commit kept on branch
`eval/run1-task2-tzpin` there.

- Task 2 (returnDetails): stages 7 and 8 passed on a diff of
  `src/Translator.js` and its test. Fix-verify then added
  `process.env.TZ = 'UTC'` to `vitest.config.mts`; the resumed Commit made
  8157f31 with four files including `vitest.config.mts`.
- Task 1 (skipOnVariables): the same pin appeared in its Fix-verify attempt 2
  and was in the flagged-work snapshot (f6c8674, three files).
- What pushed the agents there was three time-zone failures the clone already
  had on its base commit, which Visual Relay does not set out to handle (it is
  for repositories whose suite is green). The gap does not depend on that: a
  Fix-verify agent facing any red suite may edit a file the plan never listed,
  and nothing looks at the edit.

## Current state (researched at 37403a8a)

- Stage order and contracts: `Execution/RelayStages.cs:10-22`. Fix-verify's
  contract is `{ "summary": string, "amendManifest"?: string[] }`;
  `SandboxedStage.ManifestValidation.cs:20` reads `amendManifest` for stages
  other than 4.
- The Fix-verify loop records its ledger section and seal per attempt and
  returns on green (`Execution/RelayDriver.VerifyFix.cs:199-230`); nothing
  compares the files it touched with the plan's manifest.
- The existing post-hoc filter for stage 5 (`WorktreeFilter.DiscardNonTestEditsAsync`,
  referenced at `RelayStages.cs:13-16`) shows the pattern of diffing a stage's
  edits against an allowed set.

## Prescribed approach

1. Before the first Fix-verify attempt, record the working tree's changed
   paths (the same listing the commit stage uses). After a green attempt,
   compute the paths changed since then.
2. Paths outside the plan's manifest (stage 4 `manifest` plus any
   `amendManifest` from stage 11 itself) are "unreviewed edits". When there
   are none, commit as today.
3. When there are some, run the Review stage prompt once more on the diff of
   those paths only (cheap tier, read-only), with the task and the Fix-verify
   summary. A `pass` verdict commits; `changes` flags the task "Needs review"
   with the reason `fix-verify edited <paths> outside the plan` and the
   reviewer's issues, keeping the flagged-work snapshot as today.
4. Publish `fix_verify_unreviewed_edits` with the path list whichever way it
   goes, so the log shows it even when the second review passes.

## Tests

- `RelayDriverFixVerifyReviewTests` (new, driver fakes): a green Fix-verify
  that only edited manifest files commits without a second review call; one
  that edited `vitest.config.mts` outside the manifest calls the reviewer once
  with only that file's diff; a `changes` verdict flags with the path in the
  reason; `amendManifest` naming the file counts as in the plan.
- Watch each fail first; mutate the manifest comparison to always-empty and
  confirm a test catches it.

## Verification (through the control API)

Mac: a small repository whose suite is green, in which one value is asserted
by a test in a file far from its source (a default page size checked in
`tests/legacy-defaults.test.js`, say). Create a task that changes the value
and whose text names only the source file and its own test. Verify goes red on
the distant test, and Fix-verify has to edit a file the plan never listed.
Expected: `fix_verify_unreviewed_edits paths=tests/legacy-defaults.test.js`,
one extra review call on that file's diff alone, and a commit when it passes.
If the plan listed the distant test after all, the run proves the other
branch (no event, no extra call); run it once more with the file name left
out of the task text. Put the event line in the commit body.

## Out of scope

The Fix-verify prompt rule about environment errors
(`copy-ignored-folders-the-same-way-inside-wsl` carries it); failures a
repository already has on its base commit (not something Visual Relay sets
out to handle).
