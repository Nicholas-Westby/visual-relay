# Show the Fix-verify agent the failing verify output (and make it run the real verify)

The stage-10 "Fix-verify" loop re-runs an agent up to `maxVerifyLoops` times to
repair a red stage-9 verify, but the agent is **never shown what actually failed**,
so it cannot fix it. The driver captures the failing output and threads it into the
invocation — `RelayDriver.Artifacts.cs:~340` passes `lastTestOutput: failingTestOutput`
→ `BuildInvocation` sets `StageInvocation.LastTestOutput` (`StageInvocation.cs:16`) —
but `SwivalSubagentRunner.BuildPrompt` (`ProcessRunners.cs:~127-152`) **does not read
`LastTestOutput`**. It is referenced nowhere when constructing the prompt. The
Fix-verify agent is therefore asked to "fix the failures from the pinned suite"
while being handed zero failure text.

Compounding it, nothing makes the agent reproduce the failure with the real verify
command. The agent self-certifies by running a subset it chooses (typically just the
task's manifest test files), which pass, then declares "all tests pass" — while the
full `config.TestCommand` the driver re-runs after each attempt stays red. The loop
exhausts every attempt and flags `"verify failed after N fix-verify attempts"`.

Concrete, reproducible case: JobFinder `ocr-fail-job-3936650`. The task introduced a
biome **parse error** (an unescaped backtick inside a TypeScript template literal in
`src/discovery/screenshot-ocr-task.ts`), which made `bunx biome format` abort so
`bun test` never even ran. Across all 5 Fix-verify attempts the agent reported "All
22 tests pass" (its manifest tests) and never saw the biome error, because the error
text was never in its prompt and it never ran the full `biome format && bun test`
command. The loop ran correctly; the agent was blind.

## Goal

Make the Fix-verify (stage 10) agent able to actually fix the failure:

1. Include the captured failing verify output in the stage-10 agent prompt, so the
   agent sees the real error (e.g. the biome parse error) it must fix.
2. Instruct the agent to reproduce the failure by running the project's real verify
   command (`config.TestCommand`) and to confirm it is green before claiming a pass —
   not a self-selected subset.

Other stages, which have no `LastTestOutput`, are unaffected.

## Approach (suggested)

- In `ProcessRunners.BuildPrompt` (`src/VisualRelay.Core/Execution/ProcessRunners.cs`),
  when `invocation.LastTestOutput` is non-empty, append a section such as
  `## Failing verify output` containing the output, truncated to a sane cap (reuse the
  existing trim helpers, e.g. `TrimForError`/a length bound). This is the single change
  that would have unblocked `ocr-fail-job-3936650`.
- Surface the verify command to the agent: include `config.TestCommand` in the
  stage-10 prompt (the driver already re-runs it in `RelayDriver.Artifacts.cs`) and
  tighten the stage-10 system prompt (`RelayStages.cs`, the Fix-verify stage text) to
  say: run that exact command, read its output, fix the cause, and re-run it until
  green before returning. `StageInvocation` may need to carry the verify command if it
  is not already available where the prompt is built — thread it the same way
  `LastTestOutput` is threaded.
- Keep the change scoped: do not alter the loop control or `maxVerifyLoops` behavior
  (those work); this is purely about giving the agent the failure text + the real
  command.

## Files

- `src/VisualRelay.Core/Execution/ProcessRunners.cs` (`BuildPrompt`; possibly `BuildArguments` context)
- `src/VisualRelay.Domain/StageInvocation.cs` (already has `LastTestOutput`; add the verify command if needed)
- `src/VisualRelay.Core/Execution/RelayStages.cs` (stage-10 Fix-verify system prompt)
- `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs` only if the verify command must be threaded through `BuildInvocation`

## Tests

Use the existing prompt-construction / subagent-runner test doubles.

- **failure is shown**: a stage-10 `StageInvocation` with `LastTestOutput` set →
  `BuildPrompt` output contains that failing text (under a `## Failing verify output`
  heading). A stage with no `LastTestOutput` → no such section (regression guard).
- **real command is surfaced**: the stage-10 prompt/system-prompt includes the
  configured verify command and the instruction to run it before passing.
- Compose with the existing Fix-verify loop tests (from the earlier self-fix): a red
  verify whose only failure is visible via the full command (not the manifest subset)
  now reaches the agent as failure text.

## Notes

This is the missing half of the Fix-verify loop added earlier: the loop re-verifies
with the real command and counts attempts correctly, but the agent driving each
attempt was never given the failing output or told to run the real command, so it
could only "fix" failures it happened to reproduce. With this change, a re-drive of
`ocr-fail-job-3936650` (and its siblings `-3936655`, `-3936656`) lets the agent see
the biome parse error and escape the backticks. Keep `ProcessRunners.cs` within its
line guard.
