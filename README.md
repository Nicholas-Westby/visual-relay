# Visual Relay

Visual Relay is an Avalonia desktop control room for Relay-style LLM task processing. It brings a staged task pipeline into a modern dark GUI for choosing a repository root, inspecting the task queue, reordering work, pausing at safe boundaries, and drilling into stages, logs, and LLM command traces.

![Visual Relay main window](docs/images/visual-relay-main.png)

## Run

Use the single entry point from the repository root:

```bash
./visual-relay launch
```

The app opens with a native folder picker button. For a quick end-to-end trial, choose:

```text
/Users/admin/Dev/sample-tasks
```

Common commands:

```bash
./visual-relay build
./visual-relay test
./visual-relay check
./visual-relay screenshot
./visual-relay sample-reset /Users/admin/Dev/sample-tasks
./visual-relay run-task /Users/admin/Dev/sample-tasks add-multiply
./visual-relay install-hooks
```

The launcher uses an existing `dotnet` installation when available. If `dotnet` is missing and `nix` is installed, it automatically re-enters the command through `nix develop`, so a separate shell step is not required.

The generated sample repository also includes `./scripts/reset-sample.sh`, which returns it to three pending tasks and commits that reset state after a real run.

## Model Backend

Every Visual Relay profile targets a local OpenAI-compatible proxy (LiteLLM) at `http://127.0.0.1:4000`. Visual Relay owns this proxy's lifecycle, so `./visual-relay launch` auto-starts it before opening the app: the launch hook calls `tools/backend/backend.sh start` best-effort. When the backend is already healthy this is a fast no-op, and if it cannot start (for example LiteLLM is not installed) the app still launches and the in-app pre-flight probe surfaces the down backend.

Manage the proxy directly with:

```bash
tools/backend/backend.sh start    # idempotent; brings the proxy up on 127.0.0.1:4000 and waits for /health/readiness
tools/backend/backend.sh status   # reports up/down
tools/backend/backend.sh stop     # SIGTERM then SIGKILL, and removes the PID file
```

`start` is re-runnable any time: a healthy instance exits 0 with no duplicate process, a stale PID file is cleaned up automatically, and after launching it polls `/health/readiness` (up to ~30s) before returning. `stop` always removes the PID file, even after an abrupt kill, so the next `start` is never blocked by a stale pidfile. The PID and log files live under the git-ignored `.relay-scratch/` (`litellm.pid`, `litellm.log`).

### Provider keys (`.env`)

The proxy config `tools/backend/litellm-config.yaml` defines the model aliases the profiles reference (`cheap-kimi`, `balanced-kimi`, `frontier`, `vision`, `claude`, `claude-opus-1m`, `claude-sonnet`, `gpt-5`, `hf-qwen3-coder-next`, `kimi-k2`). No secrets are committed: every key is read from the environment via `os.environ/<KEY>`. Provide the keys you need in a git-ignored `.env`:

```bash
cp .env.example .env   # then fill in the provider keys you use
```

`backend.sh start` sources `.env` automatically. A request only fails if its specific provider key is missing, so you can set just the providers you actually call.

To install the proxy: `pip install 'litellm[proxy]'`.

## What It Does

- Discovers `llm-tasks` while skipping `DONE-*`, `IGNORE-*`, `_ideation`, and `completed`; tasks marked `NEEDS-REVIEW` stay visible in the GUI with their review reason.
- Loads `.relay/config.json` and keeps Relay defaults for stages, tier profiles, test commands, and verify limits.
- Runs one task at a time through the Relay stage model with `.relay/ACTIVE`, ledger, manifest, seal, event, report, and trace artifacts.
- Shows native root selection, queue/archive controls, task markdown/context, stage status, structured run logs, and parsed LLM tool calls in a dense command-center layout.
- Streams Swival trace events into the GUI as assistant text, tool calls, tool results, and thinking records.
- Estimates time and rounded dollar cost per task and per stage from Swival reports, using Relay's current pricing model.
- Lets stage cards act as log filters: click a stage for that step's events/traces, click it again to return to the full task log.
- Marks committed tasks `DONE-` and archives batch tasks under `llm-tasks/completed/batch-N`.
- Uses Verify-supplied Conventional Commit subjects when available and allows the final staged set to be a subset of the manifest, matching current Relay behavior.
- Owns Swival execution directly, including temporary profile generation for the local LiteLLM proxy.
- Supports mocked execution in tests and real Swival/test command execution in the app and `run-task` smoke command.

## Development Rails

- `Directory.Build.props` enables nullable, latest analyzers, code style checks, and warnings as errors.
- `.githooks/commit-msg` enforces Conventional Commits after `./visual-relay install-hooks`.
- `tools/guards/check-file-size.sh` keeps C# and Avalonia XAML source files under 300 lines by default.
- `tools/VisualRelay.Screenshots` renders the README screenshots through Avalonia Headless at desktop and compact widths.
- `tools/VisualRelay.SampleTasks` regenerates `/Users/admin/Dev/sample-tasks` with a local reset script for repeatable demos.

## Tests

```bash
./visual-relay check
```

The current automated coverage includes config loading, task discovery, archive listing, cost estimation, review-marker surfacing, nested task context, queue reordering, pause-at-boundary, stage log filtering, trace parsing, Swival profile/trace handling, crash flagging, and mocked staged driver artifacts.

Real smoke last run: `./visual-relay run-task /Users/admin/Dev/sample-tasks nested-todo-summary` completed stages 1-11, streamed trace events, ran the Python tests red/green, committed `81013f0`, and archived the nested task folder under `llm-tasks/completed/batch-1`.
