# Make Verify Stage Observational Only

## Problem

The Verify stage is supposed to summarize the harness result after Visual Relay has already run the
authoritative gate. In recent runs it did more than observe:

- `07-centralize-colors-and-font-sizes` reached stage 9, the mechanical gate returned nonzero, and the
  cheap Verify agent spent 30 minutes in a model turn before timing out.
- `disable-new-buttons-when-project-isn-t-selected` stage 9 ran shell commands and edited source files
  while the stage was named Verify. It even removed blank lines to repair a file-size failure before
  Fix-verify started.

This is risky and expensive. Verify should not run tests, edit files, or try to fix failures. When the
mechanical gate is red, Verify should summarize the captured output and let Fix-verify do the repair.

## Goal

Make stage 9 an observational, low-capability stage. It should read the already captured verify output,
produce the summary and commit-message candidates, and return quickly. It must not have enough tool
capability to edit files or run the test suite again.

## What to build

1. Restrict the stage-9 capability surface in `RelayStages` and/or invocation construction.
   - Keep file access read-only.
   - Do not allow write/edit tools.
   - Do not allow shell/test execution. If the model needs context, it should use the captured
     `## Verify output` and `VerifyOutputPath` already provided by the harness.
2. Preserve the normal stage-9 output contract:
   - summary
   - 3 to 5 conventional commit message candidates
3. If the mechanical verify result is red, keep the existing flow into Fix-verify. The Verify agent
   should not attempt to make the gate green itself.
4. Add tests that fail before the fix:
   - stage-9 invocation has no command/write capability;
   - stage-9 prompt tells the agent not to run the test suite or edit files;
   - a red mechanical verify proceeds to Fix-verify without relying on stage-9 edits.
5. Keep stages 6, 8, and 10 behavior intact. Coding and Fix-verify stages still need the tools they
   already have.

## Done criteria

- A Verify-stage agent cannot edit files or execute shell/test commands.
- Red verify output still produces a useful summary and then routes to Fix-verify.
- The full `./visual-relay check` gate passes.
- No tests are weakened, skipped, or deleted.
