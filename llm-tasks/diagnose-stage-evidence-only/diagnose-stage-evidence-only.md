# Diagnose Stage Must Not Implement — Evidence Only

Harden the stage system prompts so the read-intent pipeline stages (Diagnose above all) never
implement the change. In a real run on 2026-07-06, stage 3 (Diagnose) spent 131 turns / ~18
minutes **implementing the entire feature** — 33 `edit_file` + 3 `write_file` calls, all
succeeding — inside its ephemeral planning worktree, then wrote into the ledger: *"Implemented the
per-task skip-automated-testing toggle end-to-end … 19 files changed (18 .cs + 1 .axaml)"*. The
planning worktree is deleted after stage 4 by design, so none of that code survived — but the
ledger claim did. Stage 4 planned on top of it ("The implementation is 95% complete"), stages 5–6
implemented only the claimed residual gap, the stage-7 reviewer correctly flagged the feature as
"almost entirely unimplemented", and stage 8 then had to re-implement everything in one stage's
budget and was killed at the 30-minute absolute ceiling. One misplaced spike wasted the whole run.

## Current state (researched)

- `src/VisualRelay.Core/Execution/RelayStages.cs` — `SystemPromptFor(name)` is a switch over stage
  names. The read-intent stages are inconsistent about forbidding edits:
  - `"Ideate" => "Frame the task and list 2-3 solution options. Do not edit files."`
  - `"Research" => "Investigate the codebase; record findings and constraints. Do not edit files."`
  - `"Diagnose" => "Read application logs and extract evidence that explains the issue."` — **no
    do-not-edit directive at all.**
  - `"Plan" => "Write a concrete plan and exact impacted code and test files. …"` — no directive.
  - `"Review" => "Review the actual diff and classify issues. …"` — no directive.
  - `"Verify" => "Summarize the final state; … Do not edit files. …"` — has it.
- Same file, the stage table: `Stage(3, "Diagnose", "balanced", "some", …)` — stages 1–4, 7, and 9
  declare `files: "some"` or `"none"`; stages 5/6/8/10 declare `"all"`. A comment above stage 5
  says `"some" = read-only`, but **that is not enforced in practice**: the driver passes the mode
  to swival as `--files <mode>` (`SwivalSubagentRunner.BuildArguments` in
  `src/VisualRelay.Core/Execution/ProcessRunners.cs`), and in the observed run swival 1.0.35
  executed `edit_file` under `--files some` with `"is_error": false` results. The nono sandbox does
  not block it either: planning worktrees live under the temp dir, which is inside nono's writable
  set. So the system prompt is currently the only line of defense, and Diagnose doesn't have one.
- Planning isolation: `src/VisualRelay.Core/Execution/PlanningWorktree.cs` — stages 1–4 run per
  task in a detached git worktree under the temp root ("Deleted on completion; pruned on next
  drain after crash"). Anything a planning stage writes is structurally guaranteed to be
  discarded; only the ledger text it returns flows into the execute phase (stages 5–11), which
  starts from HEAD in the real repo.
- `RelayStages.ConfirmImplementationSystemPrompt` ("The implementation appears to already be in
  the working tree (an earlier stage wrote it)…") exists for the *execute-phase* early-
  implementation path. Planning-worktree spikes never feed it — one more reason Diagnose
  implementing is pure waste.
- Prompt-content tests live in `tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs`.

## What to build (TDD-first)

1. **Tests first** in `tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs`:
   - Assert the Diagnose prompt contains `"Do not edit files"`, states that written code is
     discarded, and forbids claiming work as implemented.
   - Add an invariant theory over `RelayStages.All`: every stage whose owner is `"llm"` and whose
     `Files` mode is not `"all"` must contain `"Do not edit files"` in its system prompt. (Today
     that set is Ideate, Research, Diagnose, Plan, Review, Verify — three of six already comply.)

2. **Rewrite the Diagnose prompt** in `SystemPromptFor` to keep the evidence mission and add the
   constraints. Suggested wording (tighten as needed, keep all three content points):
   *"Read application logs and code; extract evidence that explains the issue. Do not edit files —
   do not implement or prototype the change. Any code you write in this stage is discarded and
   never reaches later stages, but your written claims DO carry forward: describe the needed
   change in prose, and never state that work is already implemented."*

3. **Add the missing sentence to Plan and Review**: append `"Do not edit files."` to both prompts
   (Plan runs in the same disposable planning worktree right after Diagnose and has the same
   hazard; Review runs in the real tree during execute, where an editing reviewer is worse).

## Done when

- The Diagnose system prompt forbids editing/implementing, says spike code is discarded, and
  forbids "already implemented" claims; Plan and Review carry "Do not edit files."
- The invariant test enforces the directive for every non-`"all"`-files LLM stage, so a future
  stage added without it fails the suite.
- Existing prompt tests still pass; `./visual-relay check` passes (file-size guard, format
  verification, build, full test suite, README screenshot render).

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset). See
  `docs/commit-messages.md` and `AGENTS.md`.
- Prompt-only change in `RelayStages.cs` (currently 111 lines — plenty of headroom under the
  300-line guard). Do NOT change stage numbers, tiers, `files`/`commands` modes, or JSON
  contracts.
- Keep the new prompt text concise — it is sent as the system prompt on every stage invocation.
- Mechanical write-enforcement (making `--files some` actually read-only in swival/nono, or a
  driver-side dirty-tree check after read-intent stages) is explicitly **out of scope** — the
  observed non-enforcement above is context, not a deliverable.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
