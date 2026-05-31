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
- [x] Pull applicable current Relay fixes from the original Relay implementation.
- [x] Final cleanup and commit.

## Current Checkpoint
Tooling rails are in place: `./visual-relay check` passes, Nix shell resolves .NET 10.0.300, commit hooks are installed, a real temporary git repo gets an actual Relay commit in tests, and Visual Relay now completes a real Swival-backed sample task without routing through its own runner.

## 2026-05-29 Relay Fix Sync
- Reviewed current Relay history. The new applicable fix was `45d7f4e fix(relay): skip absent strip-set paths so the red-gate stash succeeds`.
- Ported the red-gate strip helper into C# and wired stage 5 author-tests through it so premature implementation edits are temporarily stashed while authored tests prove red.
- Matched the upstream absent-path behavior: manifest entries that are not present on disk are skipped before `git stash push`, while the stash list is trusted if Git creates the stash despite a pathspec quirk.
- Added regression coverage for direct red-gate stash/restore and for the driver restoring a premature implementation before continuing to green verify.

## 2026-05-30 Visual Refresh
- Reworked the Avalonia shell toward the provided dark command-center inspiration: compact top rail, native folder-picker surface, dense queue cards, task tabs, stage board, terminal-style run log, and LLM command cards.
- Split the window into focused controls (`TopBar`, `QueuePanel`, `TaskDetailPanel`, `StageBoard`, `ActivityColumn`) and moved shared styles into `VisualRelayTheme.axaml`.
- Added root folder display helpers and tests so the selected project name remains visible while parent paths are trimmed.
- Hardened the single entry point for managed/sandboxed machines by disabling Avalonia/.NET telemetry, forcing single-node MSBuild, and rendering screenshots without rebuilding during `check`.
- Verified the refreshed UI at 1440x900 and 1060x720; text wraps or truncates inside its containers at the smaller size.

## 2026-05-31 Direct Runner And Review Visibility
- Synced applicable Relay fixes from the original Relay implementation: crash-to-`NEEDS-REVIEW`, repeated commit-gate halt marker, scoped git diagnostics, broader Swival profile aliases, and commit-gate handling for deleted files under a manifest directory.
- Fixed the GUI queue so `NEEDS-REVIEW` tasks remain visible with their reason instead of disappearing from the task list; retrying a review task clears the marker before execution.
- Made Visual Relay own Swival execution directly by generating a temporary `swival.toml` in the selected root when one is absent. The default profile set targets the local LiteLLM proxy and includes the current cheap/balanced/frontier/vision plus direct model aliases.
- Added live trace streaming from Swival JSONL into the GUI event/LLM-command panes, including assistant text, tool calls, tool results, and thinking entries.
- Hardened fenced JSON parsing so JSON string content containing nested Markdown fences no longer breaks stage parsing.
- Added `./visual-relay sample-reset` to regenerate `/Users/admin/Dev/sample-tasks` and `./visual-relay run-task` for real end-to-end smoke runs through the same core runner the GUI uses.
- Ported Relay's post-commit completion behavior: committed tasks are renamed `DONE-` and batch tasks move under `llm-tasks/completed/batch-N`, with failures logged but not converted into task failures after a real commit.
- Real smoke: `./visual-relay run-task /Users/admin/Dev/sample-tasks nested-todo-summary` completed stages 1-11, produced trace/report artifacts, ran the Python tests red then green, committed `81013f0` in the sample repo, and archived the nested task folder in `llm-tasks/completed/batch-1`.
- Verification: `./visual-relay check` builds the full solution, passes 20 tests, verifies formatting/file-size limits, and regenerates both README screenshots.

## 2026-05-31 Cost, Archive, And Step Logs
- Added a C# cost estimator that matches Relay's report-based pricing model: prompt tokens are read from Swival timelines, cache hits from report stats, and output tokens are estimated from answer length plus turn count.
- Task cards and the task header now show run time and rounded dollar cost; stage cards show per-step time and rounded dollar cost.
- Added archive mode for `llm-tasks/completed/batch-*`, including nested completed task folders with sibling context files.
- Stage cards now act as log filters: selecting a stage narrows the run log and LLM command pane to that step, and selecting the same stage again restores the full task log.
- Active task and active stage styling were strengthened with selected borders, darker selected surfaces, and clearer scope chips.
- Verification added for archive toggling, run-history metrics, cost estimation, and stage-log filter toggling.

## 2026-05-31 Active State And Dollar Costs
- Compared the Visual Relay selected queue card against the reference active state and moved the styling closer: full bright outline, blue left rail, selected fill, and a subtle glow.
- Strengthened selected stage cards with the same brighter outline/glow language so stage log filtering is visually obvious.
- Replaced cent labels with rounded dollar labels everywhere (`$0.30`, `$0.00` for sub-cent estimates), including task cards, stage cards, run events, screenshots, and tests.

## 2026-05-31 Sample Reset
- Regenerated `/Users/admin/Dev/sample-tasks` to the pre-run state with three pending tasks: `add-multiply`, `improve-slugify`, and `nested-todo-summary`.
- Updated the sample generator to include `scripts/reset-sample.sh`; the script reruns `./visual-relay sample-reset`, stages the regenerated sample state, and commits it when a real run has changed the repo.

## 2026-05-31 Live State Accuracy
- Split queue cards into task row view models so persisted task state (`Pending`, `Needs review`, `Completed`) no longer hides live execution state.
- Running tasks now keep a green rail, border, status, and current step label even when the selected/log-filtered task or stage changes.
- Stage cards now distinguish blue log-filter selection from green execution state, and live task events are retained per task so clicking away and back reconstructs the active stage instead of falling back to stale report history.
