# Make the subagent-timeout flag distinguish a stalled model call from a hung test command

When a stage's `swival` subagent exceeds `SubagentTimeoutMilliseconds` (default
1,200,000ms / 20 min), `SwivalSubagentRunner` (in
`src/VisualRelay.Core/Execution/ProcessRunners.cs`, ~lines 70-77) flags the task
with a hard-coded message that always blames a hung **test command**:

```
swival timed out after {N}ms. If swival was running a test command that hung, fix
the hang and in the interim re-run only the specific tests you need rather than
the whole suite (use a targeted subset for this project, e.g. the TestFileCommand
"{files}" pattern).
```

This is actively misleading when the real cause is a **stalled model-backend
call**. Observed case: JobFinder `fix-timing-estimates` flagged at stage 6 with
this message, but no test ever ran — `swival` made a single `/v1/chat/completions`
request to the LiteLLM backend that never returned, produced **zero** output and
an **empty** trace directory (`.relay/<task>/stage6-attempt2/` had 0 files), and
sat blocked on the open socket until the 20-minute wall-clock killed it. The
"re-run only specific tests" advice sends whoever reads the flag (human or an
auto-fixer) down the wrong path entirely.

The two cases are distinguishable from data already in hand at the timeout site:
a stalled-backend / swival-startup hang produces **no stdout and no trace
entries**, whereas a genuinely hung test command produces partial output and/or
trace activity before stalling.

## Goal

On a subagent timeout, emit a message that reflects what actually happened:

- If swival produced **no output and no trace entries** (empty `result.Output`
  and an empty `invocation.TraceDirectory`): report it as a likely stalled
  model-backend call or swival startup failure — e.g. "swival produced no output
  before the {N}ms timeout — likely a stalled model-backend call (check backend
  latency / the `/v1/chat/completions` path or backend logs), not a hung test
  command."
- Otherwise (swival was active before stalling): keep the existing hung-test-command
  hint, since that is the likely cause when work was already in progress.

Keep the `{N}ms` duration in both branches.

## Approach (suggested)

- In `ProcessRunners.cs` where `result.TimedOut` is handled (~70-77), branch on
  whether any work was produced. Cheapest reliable signal: `result.Output` is
  null/whitespace AND `invocation.TraceDirectory` contains no files
  (`Directory.EnumerateFileSystemEntries(invocation.TraceDirectory).Any()` is
  false). Choose the message accordingly.
- Leave the `ErrorHintClassifier.WithHint(...)` wrapping and the
  `SubagentResult(...)` shape unchanged.

## Files

- `src/VisualRelay.Core/Execution/ProcessRunners.cs` (the `result.TimedOut` block)

## Tests

Use the existing subagent-runner test doubles / a fake `ProcessCapture` that
returns a `TimedOut` result.

- **stalled backend**: timed-out result with empty output + empty trace dir →
  message mentions a stalled model-backend call and does NOT tell the reader to
  re-run specific tests.
- **hung command (regression guard)**: timed-out result with non-empty output
  (or a non-empty trace dir) → message keeps the existing hung-test-command hint.
- The `{N}ms` timeout value appears in both messages.

## Notes

This is the diagnostic half of the stalled-call problem found while draining
JobFinder. The functional half — giving the backend a real per-request timeout so
a stalled call fails fast and triggers LiteLLM's already-configured `fallbacks`
instead of burning the full 20-minute wall-clock — is a `request_timeout` setting
in `tools/backend/litellm-config.yaml` (backend config, handled separately).
Optional future enhancement (out of scope here): a "no-trace-progress" watchdog
that kills swival after a shorter idle interval than the full subagent timeout.
Keep `ProcessRunners.cs` within its line guard.
