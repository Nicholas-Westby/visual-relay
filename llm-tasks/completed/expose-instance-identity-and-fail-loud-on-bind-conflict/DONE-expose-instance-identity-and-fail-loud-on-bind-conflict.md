# Expose instance identity on the control API and fail loud on control-port bind conflict

Operating three instances concurrently surfaced a dangerous failure shape. An instance was
SIGTERMed but survived (only SIGKILL worked); relaunched instances then could not bind the
still-occupied `VR_CONTROL_PORT` — and **kept running anyway as windowed apps with no control
server**, while `/health` on the port kept answering from the old process. The operator believed
three fresh instances (new build) were serving the ports; in reality all three ports were served
by stale processes running the previous binary, and several runs executed on code that was thought
replaced. Nothing in the control API can detect this today: `/health` returns only
`{"status":"ok","app":"Visual Relay"}` — no pid, no version, no start time. Diagnosis required
`lsof`/`ps`, violating the project's operator principle (every operational state must be
diagnosable from the control API alone).

## What to build

1. **Instance identity in `/health` and `/state`.** Add fields: `pid`, `startedUtc` (process
   start), `version` (the informational version the CLI already computes, e.g.
   `0.55+<git-sha>` — reuse the same source so the build is identifiable), and `controlPort`.
   `/health` stays cheap — these are constants captured at startup.
2. **Bind conflict must be loud.** When the control server cannot bind its configured port at
   startup: log a prominent error AND make the failure visible in the UI (persistent banner in the
   main window: "Control API unavailable — port <N> in use by another process"), and exit non-zero
   if the app was launched headlessly-intended (i.e. when `VR_CONTROL_PORT` is explicitly set,
   treat the control API as load-bearing: fail fast rather than run as an undrivable zombie).
   When the env var is unset (default port, casual GUI use), the banner alone is acceptable.
3. Keep `VR_CONTROL_DISABLE=1` semantics unchanged (explicitly disabled ≠ failed).

## Tests (red first)

- Control server test: `/health` and `/state` include pid/startedUtc/version/controlPort.
- Bind-conflict test: with the port pre-occupied (bind a listener in the test), startup with
  explicit `VR_CONTROL_PORT` reports the failure and refuses to continue (assert the failure
  surface — exception/exit path — in the existing app-startup test style); without the explicit
  env var, startup continues and the view-model exposes the banner state.

## Verification

- `./test.sh` fully green including the new tests.
