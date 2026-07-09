# Skip Fix Stages in the Driver When the Preceding Review/Verify Verdict Is Clean

When Review passes with zero issues, the Fix stage still runs its full unconditional protocol.
Measured today (2026-07-07, task `fix-vision-tier-backend-routing`, artifacts under
`.relay/fix-vision-tier-backend-routing/`): stage 7 Review returned
`{ "verdict": "pass", "issues": [] }`, and stage 8 Fix then ran **14m 27s** (run.log
`stage_done name=Fix time=14m 27s`) making **zero edits** — the whole time went to the
stage-mandated verification workload (targeted tests, full suite, task-spec probes). The Fix
input demonstrably contained the pass verdict (`## Stage 7 - Review` section of
`stage8-attempt1.input.json`), but nothing in the stage contract lets a clean pass short-circuit
the work. Fix exists to resolve review findings; no findings should mean no stage.

Make the skip a **driver decision, not a prompt suggestion**: pure mechanical logic in
`RelayDriver`, keyed on the structured verdict the driver already parses. No LLM involvement,
no prompt changes.

## What to build

1. **Skip rule for Fix (stage 8).** After Review's output is sealed and parsed, if the verdict
   JSON is well-formed with `verdict == "pass"` **and** an empty `issues` array, do not launch
   Fix. Reuse the exact parser/validation the driver already applies to stage contracts — do
   not add a second ad-hoc JSON parse. Anything else — `pass` with non-empty issues (the Fix
   prompt says "resolve every blocker **and warning**"), a failing verdict, or a
   malformed/unparseable verdict — runs Fix exactly as today (**fail open to running the
   stage**; the skip must never trigger on uncertainty).
2. **Symmetric rule for Fix-verify (stage 10).** Apply the same mechanism keyed on Verify's
   (stage 9) sealed verdict. Inspect stage 9's contract shape in `RelayStages.cs` first and key
   the cleanliness check to its actual fields — do not assume it matches Review's shape.
3. **Record the skip everywhere state is read:**
   - **Ledger:** append the stage's normal `## Stage N - <Name>` section containing a one-line
     note, e.g. `Skipped — review passed with no issues.` (downstream stage inputs include the
     ledger, so later stages see an explicit record rather than a missing section).
   - **Seals/status:** persist the stage as completed-by-skip so pause/resume and the
     flagged-work restore path treat it as done and never re-run it mid-run. Check
     `RelayDriver.FlaggedWork.cs` resume-range assumptions (stages 5–10) against a skipped
     member.
   - **UI stage board:** show a distinct skipped state (reuse an existing skipped/neutral
     affordance if one exists; otherwise add a minimal one — grey/“Skipped” label, not
     green-Done and not red-failed). Run summary math (stage count, total time, cost) must
     handle a 0-second, $0 stage.
4. **Tests** (driver tests with the existing fake runner patterns):
   - Review pass + empty issues → no stage-8 invocation, ledger carries the skip note, board
     state is Skipped, run continues to Verify;
   - pass with non-empty issues → Fix runs;
   - fail verdict → Fix runs;
   - malformed/truncated verdict JSON → Fix runs (fail-open);
   - the symmetric Verify → Fix-verify cases keyed to stage 9's real contract;
   - resume after a skipped stage does not re-run it and does not corrupt the stage sequence.

## Coordination with the pending visual-review task

`llm-tasks/add-parallel-visual-review-stage/` (pending in the same queue) renumbers stages and
makes Fix consume the **combined** output of Review ∥ Visual-review. Whichever task lands
second must adapt the other's work; the generalized rule is: **skip Fix only when every
review-family stage that feeds it reported a clean pass (or was itself skipped)**; Fix-verify
skips only on Verify's clean verdict. State this generalization in code comments so the second
task has an unambiguous contract to extend.

## Done when

- A run whose Review verdict is a clean pass proceeds Review → (Fix skipped, recorded) →
  Verify with no stage-8 subagent launched, visible in run.log and the stage board.
- All verdict branches above are pinned by driver tests; resume-over-skip is tested.
- `./visual-relay check` passes.

## Guardrails

- **Mechanical only**: no new LLM/agent calls, no time-based reasoning anywhere.
- **Do not touch stage prompts or the verification mandates inside Fix's input** (the
  full-suite instruction stays — explicitly out of scope by owner decision); this task only
  decides whether the stage launches at all.
- Nothing repo- or stack-specific: the rule reads VR's own structured verdicts, nothing else.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs; touched files
  stay under the 300-line guard.
