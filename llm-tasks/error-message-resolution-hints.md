# Error messages are raw stack strings with no hint at how to resolve them

When a stage fails, the reason surfaced to the user is the raw subagent output. A backend
outage reads `swival exit 1: Error: LLM call failed (model: cheap-kimi):
litellm.InternalServerError: InternalServerError: OpenAIException - Connection error.` — which
says nothing about the actual fix (start the model backend). The reasons are produced verbatim:

- `src/VisualRelay.Core/Execution/ProcessRunners.cs:50` —
  `$"swival exit {result.ExitCode}: {TrimForError(result.Output)}"`.
- `:45` — the timeout message.

These flow into `SubagentResult.Error` and on into the `NEEDS-REVIEW` reason / report, unmodified.

## Recommended fix

Add a small, well-tested error classifier that maps known failure signatures to a short,
actionable hint, and attach the hint to what the user sees (queue card reason, run log, and the
detail-pane error surface). Keep the original text — append the hint, don't replace it.

Signatures worth handling first:

- **Connection refused / `Connection error` to the backend** → "Can't reach the model backend at
  `http://127.0.0.1:4000` — is the LiteLLM proxy running? Start it (see
  `autostart-model-backend-on-launch.md`) and re-run." (the most common case).
- **Timeout** (`swival timed out after …`, `ProcessRunners.cs:45`) → suggest raising
  `maxTurns`/timeout or checking backend latency.
- **Auth / missing key** (401/403/`api_key`) → "Provider key missing or invalid — check the
  backend's provider config."
- **No valid fenced json block** (`ProcessRunners.cs:54`) → "The model didn't return the
  required JSON contract — usually a model/prompt issue, retry or try a stronger tier."

Implement as a pure function (input: raw error string → optional hint) so it's trivially
unit-testable and reusable by the pre-flight probe
(`preflight-model-backend-readiness.md`) and any UI error surface.

## Sequencing

- **Land early — it's a shared dependency.** The classifier is a pure function reused by
  `preflight-model-backend-readiness.md` (message wording) and by the error surfaces in
  `error-reason-truncated-in-ui.md` and `surface-stage-error-in-detail-pane.md`. Building it
  first lets those consume it instead of hand-rolling messages.
- It only enriches existing error text, so it has no ordering dependency *on* other tasks — it
  can go anytime, but earlier maximizes reuse.

## Done when

- A connection-refused failure surfaces the raw error **plus** a hint naming the backend URL and
  how to recover.
- Timeout, auth, and missing-JSON failures each surface their matching hint; an unrecognized
  error surfaces the raw text unchanged (no misleading hint).
- The classifier is a pure, unit-tested function covering each signature above. Write the
  failing tests first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
