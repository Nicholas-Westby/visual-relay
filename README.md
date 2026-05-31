# Visual Relay

Visual Relay is an Avalonia desktop control room for Relay-style LLM task processing. It rebuilds the staged Relay pipeline from the original Relay implementation with a modern dark GUI for choosing a repository root, inspecting the task queue, reordering work, pausing at safe boundaries, and drilling into stages, logs, and LLM command traces.

![Visual Relay main window](docs/images/visual-relay-main.png)

Compact-width check:

![Visual Relay compact window](docs/images/visual-relay-compact.png)

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

With Nix:

```bash
nix develop
./visual-relay launch
```

## What It Does

- Discovers `llm-tasks` while skipping `DONE-*`, `IGNORE-*`, `_ideation`, and `completed`; tasks marked `NEEDS-REVIEW` stay visible in the GUI with their review reason.
- Loads `.relay/config.json` and keeps Relay defaults for stages, tier profiles, test commands, and verify limits.
- Runs one task at a time through the Relay stage model with `.relay/ACTIVE`, ledger, manifest, seal, event, report, and trace artifacts.
- Shows native root selection, queue controls, task markdown/context, stage status, structured run logs, and parsed LLM tool calls in a dense command-center layout.
- Streams Swival trace events into the GUI as assistant text, tool calls, tool results, and thinking records.
- Marks committed tasks `DONE-` and archives batch tasks under `llm-tasks/completed/batch-N`.
- Uses Verify-supplied Conventional Commit subjects when available and allows the final staged set to be a subset of the manifest, matching current Relay behavior.
- Owns Swival execution directly, including temporary profile generation for the local LiteLLM proxy; it does not route work through its own runner.
- Supports mocked execution in tests and real Swival/test command execution in the app and `run-task` smoke command.

## Development Rails

- `Directory.Build.props` enables nullable, latest analyzers, code style checks, and warnings as errors.
- `.githooks/commit-msg` enforces Conventional Commits after `./visual-relay install-hooks`.
- `tools/guards/check-file-size.sh` keeps C# and Avalonia XAML source files under 300 lines by default.
- `tools/VisualRelay.Screenshots` renders the README screenshots through Avalonia Headless at desktop and compact widths.
- `tools/VisualRelay.SampleTasks` regenerates `/Users/admin/Dev/sample-tasks`.

## Tests

```bash
./visual-relay check
```

The current automated coverage includes config loading, task discovery, review-marker surfacing, nested task context, queue reordering, pause-at-boundary, trace parsing, Swival profile/trace handling, crash flagging, and mocked staged driver artifacts.

Real smoke last run: `./visual-relay run-task /Users/admin/Dev/sample-tasks nested-todo-summary` completed stages 1-11, streamed trace events, ran the Python tests red/green, committed `81013f0`, and archived the nested task folder under `llm-tasks/completed/batch-1`.
