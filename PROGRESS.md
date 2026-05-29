# Visual Relay Progress

## Goal
Rebuild the Relay-style staged task system in this repository as a C#/Avalonia GUI app with a test-driven core, root-folder selection, queue controls, task reordering, pause-at-boundary, task/stage/LLM-command inspection, reproducible tooling, commit hooks, screenshots, and a single build/launch entry point.

## Source Relay Findings
- Existing Relay is a Bun/TypeScript non-LLM driver that runs one task at a time through stages 1-11.
- Tasks live under `llm-tasks`; flat tasks are `llm-tasks/<id>.md`, nested tasks can include sibling context files.
- Queue discovery skips `DONE-*`, `IGNORE-*`, `_ideation`, `completed`, and `.relay/<task>/NEEDS-REVIEW`.
- `.relay/ACTIVE` is an atomic directory lock with pid and nonce.
- Stage definitions include tier, allowed files, allowed commands, system prompt, and a fenced JSON contract.
- Driver emits JSONL logs under `.relay/logs`, writes ledgers/seals/manifests, runs red and green test gates, loops verify/fix-verify, and commits with `Task:` and `Relay-Seal:` trailers.
- Swival traces are JSONL and expose assistant turns, tool calls, and tool results; these map directly to GUI command/trace panes.

## Design Direction
- Keep orchestration in framework-neutral C# libraries.
- Keep the Avalonia app as a thin shell over observable application services.
- Treat real Swival/LiteLLM execution and mocked execution as the same `ISubagentRunner` contract.
- Model pause as "finish current task or stage, then stop at a boundary" so the active process is not torn down unsafely.
- Support manual task ordering in the GUI without changing on-disk task discovery semantics.
- Use JSONL event streams as the durable source for run history and live UI updates.
- Preserve Relay's proof artifacts enough for a real run: `.relay/config.json`, `.relay/ACTIVE`, `.relay/<task>/ledger.md`, `.relay/<task>/manifest`, `.relay/<task>/<task>.seals`, and `.relay/logs/relay-*.jsonl`.

## Waypoints
- [x] Inspect existing Relay implementation.
- [x] Initialize git repository.
- [x] Add design and progress notes.
- [x] Scaffold .NET/Avalonia solution.
- [x] Write failing core tests before implementation.
- [x] Implement core domain, queue, locking, config, logs, trace parsing, and mocked driver.
- [x] Build Avalonia GUI with root picker, queue controls, task details, stage timeline, logs, and command traces.
- [x] Add linting, file-size guard, Nix flake, launch script, and conventional commit hook.
- [x] Run automated tests.
- [x] Run at least one real integration path using available local tooling/environment.
- [x] Visually verify UI and add screenshots to README.
- [ ] Final cleanup and commit.

## Current Checkpoint
Tooling rails are in place: `./visual-relay check` passes, Nix shell resolves .NET 10.0.300, commit hooks are installed, a real temporary git repo gets an actual Relay commit in tests, and the existing LiteLLM proxy accepted a live `cheap-kimi` chat completion returning `visual relay ok`. Existing the route test has a Bun parse bug around `?? ... ||`, so the live API smoke used direct `curl` against the proxy.
