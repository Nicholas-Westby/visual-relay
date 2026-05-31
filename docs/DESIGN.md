# Visual Relay Design

## Product Shape
Visual Relay is a desktop control room for Relay-style LLM task processing. The first screen is the operational UI, not a landing page: choose a repository root, inspect pending tasks, start or pause a run, reorder the queue, and drill into each task's stages, logs, prompts, and tool commands.

## Architecture
The solution is split into small projects:

- `VisualRelay.Domain`: immutable domain types such as tasks, stages, run events, command traces, and config.
- `VisualRelay.Core`: filesystem-backed services for task discovery, config loading, logs, traces, queue orchestration, and process execution.
- `VisualRelay.App`: Avalonia UI, view models, and platform dialogs.
- `VisualRelay.Tests`: fast unit tests and integration-style tests with temporary repositories and mocked runners.

The GUI depends on Core, but Core does not depend on Avalonia. This keeps test coverage broad and makes real headless validation possible.

## Relay Mapping
The original Relay pipeline is represented as stage definitions:

1. Ideate
2. Research
3. Diagnose
4. Plan
5. Author-tests
6. Implement
7. Review
8. Fix
9. Verify
10. Fix-verify
11. Commit

Each stage carries its tier, file access scope, command scope, prompt, and JSON contract. The UI shows stage progress and the runner uses those definitions to build prompts for real Swival calls or mocked tests.

## GUI Views
- Root bar: selected repository, browse button, refresh button, install/config status.
- Queue column: pending, needs-review, and archived completed tasks, review reason, task-level time/cost, button reorder, start selected, drain queue, pause/resume.
- Task detail: task markdown preview, nested context files, status, manifest, proof artifacts.
- Stage timeline: current stage, active selection, attempts, tier escalation, red/green checks, elapsed time, and rounded dollar cost.
- LLM command pane: rendered trace records with assistant text, tool calls, and tool results.
- Run log pane: structured events filtered by task, stage, level, and run id; clicking a stage toggles between that stage log and the full task log.

## Execution Model
Runs are serialized. A `RelayQueueController` owns the in-memory queue order, selected task, run state, pause request, and boundary decisions. A `RelayDriver` owns one task's staged execution and emits structured events. The app invokes Swival directly through a C# runner, generates a temporary local proxy profile when needed, tails Swival JSONL traces live, and supports reliable simulated runners for tests.

## Safety
- The app never runs two Relay tasks at once.
- Pause is cooperative and boundary-based.
- Root selection must contain or be installable with `.relay/config.json`.
- Git and process calls are wrapped behind interfaces so tests can use fakes.
- Commit hooks in this repo enforce conventional commits for Visual Relay itself.
- Unexpected runner crashes are flagged into `.relay/<task>/NEEDS-REVIEW` with details so queue drains do not loop on the same broken task.
- Repeated commit-gate rejections halt drain with `.relay/DRAIN-HALTED`.
