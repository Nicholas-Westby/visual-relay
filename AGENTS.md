# AGENTS.md

Guidance for AI agents and contributors working in this repository.

## Workflow

All work done in this project should be a commit against `main`; we don't use
feature branches or PRs.

Make focused commits directly on `main` with Conventional Commit subjects.
The `commit-msg` hook (a C# validator wired by `./visual-relay install-hooks`)
enforces a fuller ruleset than a bare subject regex: the fixed type set, a 72-char
subject, lowercase after the prefix, no trailing period, no em dashes, and a body
of at most three `- ` hyphen bullets (≤ 20 words each). Human commits also avoid
naming changed files and path-like tokens; the driver's in-run sealed commit is
exempt from those contextual checks. See `docs/commit-messages.md`.

## Build & checks

- Build, run, and test through the single entry point: `./visual-relay build`,
  `./visual-relay test`, `./visual-relay launch`.
- Run the full gate before considering work done: `./visual-relay check`
  (file-size guard, format verification, build, tests, screenshot determinism check).
- Keep C# and Avalonia XAML source files under 300 lines (the C# file-size guard
  in `tools/VisualRelay.Guards`, run by `./visual-relay check`).
- Shell scripts are shfmt-formatted (tabs, no custom style flags). Apply with
  `./visual-relay format`, verified by `./visual-relay check`. Scripts stay ≤ 24 logic
  lines; only the `visual-relay` bootstrap has a 100-line structural carve-out — all
  other logic moves to C#.
- If `./visual-relay test` hangs (sits at `Testing (NNNs)` with nothing completing), it's a
  deadlock, not a slow test. Find the culprit with
  `./visual-relay test --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none`. See `TROUBLESHOOTING.md`.
- On Windows, the tests that run bootstrap or the run gate against the machine's own WSL
  distro skip when it has no usable one. On a machine that does have one, set
  `VR_TEST_EXPECT_WSL=1` so a probe that wrongly finds none fails them instead.
- Output copied from a real machine into a test keeps no personal names: use `alice` for the
  user (home directories, git identities) and a made-up host name, as the rest of the suite does.
- Headless UI tests must use `[AvaloniaFact]`/`[AvaloniaTheory]` (Avalonia.Headless.XUnit);
  `HeadlessUnitTestSession` is banned (BannedApiAnalyzers) — reintroducing it fails the build.
- New UI tests instantiate the specific panel/control under test, not the whole app,
  unless the test is explicitly about whole-app wiring (cog→dialog plumbing, the live
  control server, cross-panel interaction). Booting a full `MainWindow` +
  `LoadInitialAsync` to assert on one panel is slow and races the shared headless
  dispatcher under load. Scope down: construct the panel + the minimal view-model slice
  it binds to (patterns: `SettingsTestHelpers.ShowScopedSettings`,
  `ActivityColumnTabsUiTests.ShowActivityColumn`, `ChevronAffordanceRenderTests`). A
  guard (`SplitGuardVerificationTests.NoTestFile_BootsWholeAppOutsideAllowlist`) flags
  `new MainWindow` outside an allowlist of justified whole-app classes.

See `README.md` for the full project overview and `TROUBLESHOOTING.md` for diagnosing the
dev loop.

## Which agent runs a stage

One does: the in-process turn loop, built by `SubagentRunnerFactory`. It calls
providers directly over their OpenAI-compatible endpoints, with no local service
and no subprocess, and streams model output into the Activity column while the stage is
still running.

There used to be a `VR_AGENT` selector choosing between this and a third-party
CLI subprocess behind a local model gateway. Both were removed on 2026-09-01, so
the variable now selects nothing and is ignored.

Every command a stage runs is sandboxed by `nono`. On Windows that means WSL2 is
required, with nono installed inside the distro (`.\visual-relay.cmd setup-wsl` sets up
the distro, git and nono): the commands themselves run there,
under the same `vr-guard` profile and the same Landlock enforcement Linux gets. Visual
Relay on Windows therefore drives repositories whose toolchain lives in the distro —
the workspace is opened through a `\\wsl.localhost\<distro>\...` path, and a
Windows-only toolchain (MSBuild against .NET Framework, Visual Studio build tools,
Unity, anything that needs an `.exe`) is not supported.

## Driving the running app (control API — PREFERRED over the CLI)

When the desktop app is running it exposes a **loopback-only HTTP control API** so you
can drive it from the shell exactly as a user would by clicking — and fetch screenshots
of the live window. **Prefer this over the dev-only `run-task` CLI**: it performs the
real UI actions (honoring each button's enabled/disabled state), shares the app's single
run lifecycle, and gives visual observability for troubleshooting.

- Bound to `http://127.0.0.1:8765/` (loopback only — not remotely reachable). Override the
  port with `VR_CONTROL_PORT`, disable entirely with `VR_CONTROL_DISABLE=1`. If
  `VR_CONTROL_TOKEN` is set, send it as an `X-VR-Token` request header. On startup the app
  logs `vr-control: listening on http://127.0.0.1:<port>` to stderr.

Endpoints:

- `GET /` — HTML index page documenting the API surface (routes and commands).
- `GET /health` — liveness, `{ "status": "ok", "app": "Visual Relay" }`.
- `GET /state` — JSON snapshot: `rootPath`, `isBusy`, `pauseRequested`, `cancelRequested`,
  `statusText`, `selectedTask`, `tasks[]`, `stages[]`, and a `commands` map giving each
  command's `enabled` flag (mirrors which buttons are clickable). It also answers
  "is it stuck?", "what is it doing right now?", "did the drain halt?" and "what has
  it cost?" — no file reading required:
  - `nowUtc` — server time the snapshot was built. `lastActivityUtc` — when the app
    last handled ANY relay event (null until the first one arrives). A large
    `nowUtc` − `lastActivityUtc` gap while `isBusy` is true means wedged, not working.
  - `lastEvent` — `{utc, level, name, taskId, stage, tier, message}` for that event, or
    null. `message` is the event's human text clipped to 240 characters, so a trace
    event never dumps whole model output into the response.
  - `runningTasks[]` — `{taskId, stageNumber, stageName, tier}` for every concurrently
    executing task (`stageNumber`/`stageName`/`tier` null between stages); empty when idle.
  - `sessionCostUsd` — cumulative USD accrued since launch, 0 before the first priced
    stage completes.
  - `testCommandIsPlaceholder` — true when `testCmd` is Visual Relay's no-op placeholder
    (it exits 0 having run nothing), so a green Verify proves nothing about the change.
  - `configDiagnostic` — why `.relay/config.json` was refused (a malformed entry), or null.
    A refused config also leaves `tasks` empty, so check this before concluding tasks are gone.
  - `drainHalted` / `haltReason` — the drain circuit breaker's halt marker and its
    reason (clipped to 500 characters); false/null when no root is open or no marker exists.
  - `cancelRequested` — true from a `cancel` request until the run has finished winding
    down, so a caller can tell "stopping" from "stopped". `statusText` then reads
    `Cancelled <task>`.
  - every `tasks[]` entry, and `selectedTask`, also carries `reviewReason`, `costUsd`,
    `durationSeconds`, `completedStageCount`, `settledStageCount`, `pipelineStageCount`.
- `POST /command/{name}` — invokes the same command the button binds. A **disabled**
  command is refused with `409` (never executed); for `run-all`, `run-selected` and
  `resume` the body's `reason` names every blocker and what lifts it (a run in progress,
  a pause, which outlives the drain it stopped and a folder switch, the archive view, an
  empty queue, no selection). Unknown names return `404`. Async run
  commands are fire-and-forget (like a click) — poll `/state` to follow progress. Names:
  `bootstrap` (greenfield setup — git init + an EMPTY HEAD commit when missing, a runnable
  `.relay/config.json` with a placeholder test command when no toolchain is detected,
  and the pre-commit hook. When none of its built-in candidates passes it asks a small
  agent run for one and keeps the answer only if it passes the same check, saying so in
  the status; the config is written but never committed, and the placeholder
  is upgraded to the real test command automatically once the project gains a
  toolchain. Its outcome — including the note that the config was left uncommitted,
  and any warning about a pre-commit hook it refused to overwrite — is left in
  `/state.statusText`. It runs each candidate test command and the formatter check, so
  it can take minutes: `/state.isBusy` is true for as long as it runs and the commands
  that refuse a busy app refuse it), `run-all`, `run-selected`,
  `resume`, `cancel`, `refresh`, `pause-toggle`, `archive-toggle`,
  `new-task`, `follow-running`, `edit`, `rewrite-selected`, `cancel-rewrite`,
  `revert-rewrite`, `mark-done`, `reset-selected`, plus property actions
  `open-folder` (body `{"path":"<dir>"}` — the programmatic Browse: point the app at a
  project; `409` with `reason` for a folder Browse would refuse, such as a Windows drive
  folder or a non-WSL share on Windows, and `409` for a missing folder), `create-task` (body `{"title":"<text>","body":"<markdown>"}`, `body`
  optional — authors a task headlessly through the same view-model command the New
  Task dialog's Create button runs: writes `llm-tasks/<slug>/<slug>.md` holding the
  title line, a blank line and the body, and answers
  `{"ok":true,"id":"<slug>","path":"<markdown path>"}` only once `/state.tasks[]`
  lists it. Creating a task is a queue action, so the app switches to the queue view
  when the archive is showing and the answer always comes from the listed row.
  `400` when the title is missing or its slug is empty, unsafe, reserved or
  already taken — the error text is the one the dialog would show; `409` while the app
  is busy or no project folder is open), `select-task` (body `{"id":"<taskId>"}`),
  `boost-turns` (body `{"value":true|false}`), `skip-tests` (body `{"value":true|false}`),
  `obsidian-scan`, `obsidian-bridge` (body `{"value":true|false}` or `{"path":"<vault>"}`),
  `select-activity-tab` and `select-detail-tab` (body `{"name":"<tab header>"}` or
  `{"index":<n>}`).
  `cancel` stops the run in flight — `run-all`, `run-selected` and `resume` alike — and
  is enabled only while one is active (`409` when idle). It returns immediately and the
  run winds down asynchronously: the interrupted task's partial work is captured, its
  worktree is reset to the run base, and it is marked `NEEDS-REVIEW` with the reason
  `cancelled by operator` (a `cancelled` event lands in its `run.log`). A task whose
  commit had already started stays committed; queued tasks stay pending and no
  `DRAIN-HALTED` marker is written. Poll `/state.cancelRequested` for the wind-down.
  The destructive commands — `mark-done`, `rewrite-selected`, `reset-selected` — mirror the
  GUI confirm modal: each needs `{"confirm":true}` (else `409`, no-op) and is awaited to
  completion, so `{"ok":true}` means the effect took.
  `reset-selected` also restores the working tree to the run base when no run is active,
  after re-capturing the tree into the archived bundle; while a run IS active the running
  task owns the tree, so it archives only and `statusText` says so.
- `GET /screenshot[?path=/abs/file.png]` — renders the live window to PNG (`image/png`);
  with `?path=` it also writes the file and returns the location in `X-Screenshot-Path`.

Examples:

    curl -s -X POST -d '{"path":"/Users/me/Dev/my-project"}' http://127.0.0.1:8765/command/open-folder
    curl -s http://127.0.0.1:8765/state | jq .
    curl -s -X POST -d '{"title":"My Task","body":"What to do.\n"}' http://127.0.0.1:8765/command/create-task
    curl -s -X POST -d '{"id":"my-task"}' http://127.0.0.1:8765/command/select-task
    curl -s -X POST -d '' http://127.0.0.1:8765/command/run-all
    curl -s http://127.0.0.1:8765/screenshot -o /tmp/vr.png

NOTE: The control server runs on Kestrel (RFC 9112): a POST with no `Content-Length` is a
valid empty-body request and executes the command. Use `curl -X POST …` for bodyless commands.

## Sample Tasks (dev-only)

The following tools are available in source checkouts but are **not shipped** in the
Homebrew formula:

- `./visual-relay gen-sample <path>` — regenerates a sample tasks repository with
  repeatable demo state (runs `tools/VisualRelay.SampleTasks`).
- `./visual-relay run-task <path> <task>` — runs a single task headlessly through the
  full Relay pipeline (runs `tools/VisualRelay.RunTask`).
- `./visual-relay screenshot` — renders README screenshots via Avalonia Headless
  (runs `tools/VisualRelay.Screenshots`). This is how `docs/images/` is refreshed:
  deliberately, by running the verb after a UI change and committing both PNGs.
  `check` renders into scratch and only proves the render is deterministic.

These require a .NET SDK and a full source checkout. Brew-installed users only have
`launch` and `init`.
