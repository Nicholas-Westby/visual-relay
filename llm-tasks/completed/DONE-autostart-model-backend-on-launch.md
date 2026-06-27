# Visual Relay assumes the model backend is already running, so the common failure is "backend down"

Visual Relay generates a temporary `swival.toml` whose every profile targets a local
OpenAI-compatible proxy on `http://127.0.0.1:4000`
(`src/VisualRelay.Core/Execution/SwivalProfileSession.cs:39-100`, models `cheap-kimi`,
`balanced-kimi`, `frontier`, `vision`, `claude`, `claude-opus-1m`, `claude-sonnet`, `gpt-5`,
`hf-qwen3-coder-next`, `kimi-k2`). But nothing in the project starts or owns that proxy. If the
operator hasn't manually launched it, the very first stage fails with a connection error. The
app owns the client contract (the profiles) but not the server it depends on — so the most
common run failure is purely environmental.

`./visual-relay launch` goes straight to `dotnet run` (`visual-relay`, `launch|run` case) with
no backend bootstrap.

## Recommended fix

Make Visual Relay own bringing its model backend up, so a fresh `./visual-relay launch` can run
a task end-to-end without a manual proxy step.

1. **Check in a proxy config** that defines exactly the model aliases the profiles reference
   (the names above are the source of truth — keep them in sync with `SwivalProfileSession`).
   Providers/keys come from the environment (a git-ignored `.env`/`.env.example` pair), so no
   secrets are committed; a request only fails if its specific provider key is missing.
2. **Add an idempotent start script** that launches the proxy on `127.0.0.1:4000` in the
   background, records a PID file, streams logs to a file, and exits 0 if a healthy instance is
   already running (re-runnable any time). Pair it with a stop script (SIGTERM, then SIGKILL
   after a grace period).
3. **Poll for readiness** (`GET /health/readiness`) after start, with a bounded timeout, before
   handing off — so the app doesn't race a still-booting proxy.
4. **Hook it into `./visual-relay launch`**: ensure-the-backend-is-up runs before `dotnet run`.
   Keep it idempotent and fast when the proxy is already healthy, and degrade gracefully (clear
   message, app still launches) if the backend toolchain isn't installed — the in-app pre-flight
   probe (`preflight-model-backend-readiness.md`) then surfaces the down state.

Keep the backend lifecycle scripts self-contained and documented in the README, and make sure
`stop` cleans up the PID file even after a SIGINT/SIGKILL so the next `start` isn't blocked by a
stale pidfile.

## Sequencing

- Independent of the in-app tasks, but **complementary to
  `preflight-model-backend-readiness.md`** (autostart *prevents* a down backend; preflight
  *detects* one and degrades gracefully when the toolchain is missing). The hint in
  `error-message-resolution-hints.md` and the recovery action in
  `backend-status-indicator-in-topbar.md` reference this task by name — keep the start/stop path
  stable so those references hold.

## Done when

- A clean `./visual-relay launch` with no proxy already running brings the backend up on
  `127.0.0.1:4000`, waits for readiness, and can complete stage 1 of a sample task without a
  manual proxy step.
- Re-running launch while the proxy is healthy is a no-op (no duplicate process, no error).
- A stop path cleanly terminates the proxy and removes the PID file (including after an abrupt
  kill).
- Missing backend toolchain produces a clear message and still launches the app rather than
  crashing.
- README documents start/stop/status and the provider-key `.env`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
