# Visual Relay

Visual Relay is an Avalonia desktop control room for Relay-style LLM task processing. It rebuilds the staged Relay pipeline from the original Relay implementation with a GUI for choosing a repository root, inspecting the task queue, reordering work, pausing at safe boundaries, and drilling into stages, logs, and LLM command traces.

![Visual Relay main window](docs/images/visual-relay-main.png)

## Run

Use the single entry point from the repository root:

```bash
./visual-relay launch
```

Common commands:

```bash
./visual-relay build
./visual-relay test
./visual-relay check
./visual-relay screenshot
./visual-relay install-hooks
```

With Nix:

```bash
nix develop
./visual-relay launch
```

## What It Does

- Discovers pending `llm-tasks` while skipping `DONE-*`, `IGNORE-*`, `_ideation`, `completed`, and tasks marked `NEEDS-REVIEW`.
- Loads `.relay/config.json` and keeps Relay defaults for stages, tier profiles, test commands, heartbeats, and verify limits.
- Runs one task at a time through the Relay stage model with `.relay/ACTIVE`, ledger, manifest, seal, event, and trace artifacts.
- Shows root selection, queue controls, task markdown/context, stage status, structured run logs, and parsed LLM tool calls.
- Supports mocked execution in tests and real Swival/test command execution in the app.

## Development Rails

- `Directory.Build.props` enables nullable, latest analyzers, code style checks, and warnings as errors.
- `.githooks/commit-msg` enforces Conventional Commits after `./visual-relay install-hooks`.
- `tools/guards/check-file-size.sh` keeps C# source files under 300 lines by default.
- `tools/VisualRelay.Screenshots` renders the README screenshot through Avalonia Headless.

## Tests

```bash
./visual-relay check
```

The current automated coverage includes config loading, task discovery, nested task context, queue reordering, pause-at-boundary, trace parsing, and mocked staged driver artifacts.

