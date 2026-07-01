# Make Fix-Task Authoring Read-Only

## Problem

`FixTaskAuthorRunner` is described as a read-only prompt-and-parse step:

> No worktree - this is a read-only prompt-and-parse step.

But its synthetic stage definition currently grants broad capability:

- `Files: "all"`
- `Commands: "all"`

That gives the task-authoring model more power than the feature needs. It only needs the failure
context already assembled into the prompt and should return JSON markdown for a new task. It should
not be able to edit the repository or execute arbitrary commands.

## Goal

Make "Create task to fix" authoring genuinely read-only and low-capability while preserving the button
behavior and task-writing flow owned by the app.

## What to build

1. Reduce `FixTaskAuthorRunner` stage capabilities.
   - Use no write access.
   - Use no command execution unless a narrowly justified read-only command set is truly necessary.
   - Prefer passing all needed failure context in the prompt, as it does today.
2. Keep the app-owned write step unchanged.
   - The app may still write the returned markdown through `RelayTaskWriter.CreateAsync` after it
     validates and disambiguates the slug.
   - The subagent itself must not write files.
3. Add tests that inspect the captured `StageInvocation`.
   - The fix-task author invocation must not use `Files: "all"` or `Commands: "all"`.
   - The happy path still creates a new task from returned JSON.
   - Invalid JSON/error paths still surface a clean status message and write nothing.
4. Keep the behavior general. Do not add language- or toolchain-specific logic to the authoring path.

## Done criteria

- The fix-task authoring subagent cannot edit files or run arbitrary commands.
- Existing create-fix-task tests still pass.
- New capability-scope tests fail before the fix and pass after it.
- The full `./visual-relay check` gate passes.
