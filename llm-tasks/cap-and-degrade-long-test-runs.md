# Cap long-running test-command execution and degrade to a targeted subset

When a project's test command hangs or runs pathologically long, Visual Relay has no good
defense, and the failure modes are opaque and expensive:

- **The driver runs the test command with no wall-clock cap.** `ShellTestRunner.RunAsync`
  passes `Timeout.InfiniteTimeSpan` (`src/VisualRelay.Core/Execution/ProcessRunners.cs:14`)
  for both the stage-5 red gate (runs `config.TestFileCommand`,
  `src/VisualRelay.Core/Execution/RelayDriver.cs` ~110) and the stage-9 baseline verify (runs
  `config.TestCommand`, `RelayDriver.cs` ~144). A hung suite there wedges the whole run until
  the outer cancellation token fires.
- **Subagent-driven test runs are only bounded by the coarse 20-min `subagentTimeoutMs`**
  (`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:26`, default `1_200_000`). When
  swival runs `dotnet test` itself as a tool call and the suite hangs, the whole stage burns
  the full 20 minutes before failing with a generic timeout — exactly what timed out stages 5
  and 10 in the `new-task-editor-in-detail-pane` run.

## What to build

- **Give the driver's test-command execution a finite, configurable cap** (~5 min default;
  add a config field, e.g. `testTimeoutMs`) — replace the `Timeout.InfiniteTimeSpan` in the
  `ShellTestRunner` path. On breach, kill the process tree and fail the stage fast.
- **Make the halt signal actionable for the LLM, not a generic failure.** The message should
  say the test suite exceeded the cap and was halted, that this likely indicates a hang/perf
  problem that should be **fixed**, and that in the interim the agent should re-run only the
  specific tests it needs rather than the whole suite. There is already an
  `ErrorHintClassifier` with a `TimeoutHint`
  (`src/VisualRelay.Domain/ErrorHintClassifier.cs`) — route the halt through it / extend it so
  the hint is surfaced consistently.
- **Don't hardcode "the subset" — it is project-specific.** Running a narrowed set differs by
  stack (`dotnet test --filter …`, `pytest path::test`, `bun test <files>`, `jest -t …`). The
  signal should instruct the agent to determine the project's subset-invocation and narrow
  scope itself. The existing `TestFileCommand` `{files}` mechanism (the scoped command already
  used by the red gate) is the natural lever to point at.
- **Open design point for the implementer:** the per-command cap covers the driver paths
  (stages 5 & 9). The subagent's own in-stage test runs can't be intercepted directly (swival
  is external); at minimum document that those remain governed by `subagentTimeoutMs`, and
  consider whether that timeout's message should carry the same "halt + run a subset" guidance.

## Done when

- Driver test-command runs (stages 5 & 9) are capped at a configurable duration (~5 min
  default) instead of `InfiniteTimeSpan`; on breach the process tree is killed and the stage
  fails fast rather than hanging.
- The halt produces a clear, actionable message: the cap was exceeded, it should be addressed,
  and the agent should fall back to a targeted subset (mechanism determined per-project),
  surfaced via `ErrorHintClassifier`.
- Existing fast runs are unaffected; a suite that legitimately needs longer is configurable.
- Covered by a test: a fake slow/hanging `ITestRunner` causes the stage to fail with the
  timeout signal (not hang).
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
