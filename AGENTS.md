# AGENTS.md

Guidance for AI agents and contributors working in this repository.

## Workflow

All work done in this project should be a commit against `main`; we don't use
feature branches or PRs.

Make focused commits directly on `main` with Conventional Commit subjects
(enforced by the `commit-msg` hook once `./visual-relay install-hooks` has been
run).

## Build & checks

- Build, run, and test through the single entry point: `./visual-relay build`,
  `./visual-relay test`, `./visual-relay launch`.
- Run the full gate before considering work done: `./visual-relay check`
  (file-size guard, format verification, build, tests, screenshot render).
- Keep C# and Avalonia XAML source files under 300 lines
  (`tools/guards/check-file-size.sh`).
- If `./visual-relay test` hangs (sits at `Testing (NNNs)` with nothing completing), it's a
  deadlock, not a slow test. Find the culprit with
  `./visual-relay test --blame-hang --blame-hang-timeout 30s`. See `TROUBLESHOOTING.md`.
- Headless UI tests must use `[AvaloniaFact]`/`[AvaloniaTheory]` (Avalonia.Headless.XUnit);
  `HeadlessUnitTestSession` is banned (BannedApiAnalyzers) — reintroducing it fails the build.

See `README.md` for the full project overview and `TROUBLESHOOTING.md` for diagnosing the
dev loop.

## Driving the running app (control API — PREFERRED over the CLI)

When the desktop app is running it exposes a **loopback-only HTTP control API** so you
can drive it from the shell exactly as a user would by clicking — and fetch screenshots
of the live window. **Prefer this over the dev-only `run-task` CLI**: it performs the
real UI actions (honoring each button's enabled/disabled state), shares the app's single
backend/run lifecycle, and gives visual observability for troubleshooting.

- Bound to `http://127.0.0.1:8765/` (loopback only — not remotely reachable). Override the
  port with `VR_CONTROL_PORT`, disable entirely with `VR_CONTROL_DISABLE=1`. If
  `VR_CONTROL_TOKEN` is set, send it as an `X-VR-Token` request header. On startup the app
  logs `vr-control: listening on http://127.0.0.1:<port>` to stderr.

Endpoints:

- `GET /health` — liveness, `{ "status": "ok", "app": "Visual Relay" }`.
- `GET /state` — JSON snapshot: `rootPath`, `isBusy`, `pauseRequested`, `statusText`,
  `backend`, `selectedTask`, `tasks[]`, `stages[]`, and a `commands` map giving each
  command's `enabled` flag (mirrors which buttons are clickable).
- `POST /command/{name}` — invokes the same command the button binds. A **disabled**
  command is refused with `409` (never executed); unknown names return `404`. Async run
  commands are fire-and-forget (like a click) — poll `/state` to follow progress. Names:
  `run-all`, `run-selected`, `resume`, `refresh`, `pause-toggle`, `archive-toggle`,
  `new-task`, `follow-running`, `start-backend`, `edit`, plus property actions
  `select-task` (body `{"id":"<taskId>"}`), `bypass-sandbox` (body `{"value":true|false}`),
  `boost-turns` (body `{"value":true|false}`).
- `GET /screenshot[?path=/abs/file.png]` — renders the live window to PNG (`image/png`);
  with `?path=` it also writes the file and returns the location in `X-Screenshot-Path`.

Examples:

    curl -s http://127.0.0.1:8765/state | jq .
    curl -s -X POST -d '{"id":"my-task"}' http://127.0.0.1:8765/command/select-task
    curl -s -X POST -d '' http://127.0.0.1:8765/command/run-all
    curl -s http://127.0.0.1:8765/screenshot -o /tmp/vr.png

NOTE: macOS's managed `HttpListener` rejects a POST with no `Content-Length`, so bodyless
commands must pass an empty body: `curl -X POST -d '' …`.

## Sample Tasks (dev-only)

The following tools are available in source checkouts but are **not shipped** in the
Homebrew formula:

- `./visual-relay sample-reset <path>` — regenerates a sample tasks repository with
  repeatable demo state (runs `tools/VisualRelay.SampleTasks`).
- `./visual-relay run-task <path> <task>` — runs a single task headlessly through the
  full Relay pipeline (runs `tools/VisualRelay.RunTask`).
- `./visual-relay screenshot` — renders README screenshots via Avalonia Headless
  (runs `tools/VisualRelay.Screenshots`).

These require a .NET SDK and a full source checkout. Brew-installed users only have
`launch` and `init`.
