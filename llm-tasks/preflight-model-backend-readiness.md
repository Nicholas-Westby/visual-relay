# No pre-flight check: a down model backend burns ~36s of retries, then flags the task

Every Swival profile points at the local model backend on `http://127.0.0.1:4000`
(`src/VisualRelay.Core/Execution/SwivalProfileSession.cs:39-100`). When that endpoint is not
listening, stage 1 still launches, the LLM call retries ~4 times for ~36s, and only then fails
with `OpenAIException - Connection error`, flagging the task. There is no cheap up-front check,
so the most common environment problem (backend not running) looks like a slow task failure.

Today nothing probes the endpoint before a run:

- The runner goes straight to `swival` (`ProcessRunners.cs:42`) with no reachability check.
- App startup (`src/VisualRelay.App/Program.cs`, launched via `./visual-relay launch`) does no
  backend health check.

## Recommended fix

Add a fast readiness probe against the backend base URL and fail fast with a clear, actionable
state instead of a 36s retry burn:

- Add a small reachability check (e.g. `GET {base_url}/health/readiness`, ~1-2s timeout)
  against the same `base_url`/port the profiles use (`127.0.0.1:4000`). Centralize the base URL
  so the profile generator and the probe share one source of truth rather than hard-coding the
  port twice.
- Run the probe **before launching a run** (in the driver/runner path) and short-circuit with a
  "model backend not reachable at `http://127.0.0.1:4000`" outcome rather than spawning swival.
- Also run it **at app startup** and show a non-blocking banner/indicator when the backend is
  down, so the user knows before they hit Run All. (Pairs with the top-bar status indicator
  task if present.)
- Make the message remediation-oriented, not a raw stack string — see
  `error-message-resolution-hints.md`.

This is a guard, not a behavior change for the happy path: when the backend is up, the probe
passes quickly and the run proceeds exactly as today.

## Sequencing

- **Foundational: do this before `backend-status-indicator-in-topbar.md`**, which reuses this
  task's readiness probe — there must be exactly one probe implementation.
- Reuse the classifier from `error-message-resolution-hints.md` for the "backend not reachable"
  wording instead of hand-writing it; if that task hasn't landed, leave a clear seam to swap it
  in.
- Complementary to `autostart-model-backend-on-launch.md` (this *detects* a down backend; that
  *prevents* it). **Centralize the `base_url`/port here** so that task and
  `persist-run-diagnostics-log.md` reuse one source of truth rather than re-deriving it.

## Done when

- With nothing listening on `127.0.0.1:4000`, starting a run fails in ~1-2s with a clear
  "backend not reachable" message instead of ~36s of retries.
- With the backend up, runs proceed normally; the probe adds no meaningful latency.
- App startup surfaces backend-down state (banner/indicator) without blocking the UI.
- Unit coverage over the probe: reachable vs. refused yields the expected ready/not-ready
  result. Write the failing test first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
