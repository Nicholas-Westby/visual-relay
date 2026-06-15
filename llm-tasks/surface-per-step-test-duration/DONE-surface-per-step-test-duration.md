# Surface per-step test-command duration in the pipeline UI

The pipeline already shows per-stage total duration, cost, turns, and model on each
stage card. What is missing is the **test-command portion** of that duration — the time
spent running the configured `testCommand` (or `testFileCommand`) at the harness level.
Today, the displayed `DurationSeconds` lumps together LLM subagent thinking, tool calls,
bootstrap/guard checks, and the test run itself. An operator diagnosing a slow or wedged
test suite has no way to see whether the test runner is the bottleneck without correlating
timestamps in logs. This task isolates and surfaces the harness-controlled test-run wall-time
as a labeled metric beside the existing stage metrics.

**Scope boundary:** the harness directly controls test-command execution in exactly three
contexts — the Author-tests gate (stage 5), the Verify stage (stage 9), and the Fix-verify
loop (stage 10). Coding-stage agents (stages 6–8) may also self-run tests as tool calls, but
those are internal to the subagent and the harness has no visibility into that wall-time.
This task covers only the harness-run calls the harness owns; the spec says so explicitly.

## Current state (researched)

### Where the harness runs the test command (and timing is not captured)

There are three harness-owned test-command call sites, all via `ITestRunner.RunAsync`.
None wraps the call with its own stopwatch — the only stopwatch is the per-stage outer one
started at the top of each stage block, and it covers the entire stage lifetime.

**Stage 5 — Author-tests red-gate (`AuthorTestGate.cs:27`)**

```
src/VisualRelay.Core/Execution/AuthorTestGate.cs:27
    result = await testRunner.RunAsync(rootPath, command, cancellationToken);
```

`AuthorTestGate.RunAsync` is called from `RelayDriver.cs:147-155`. The call stashes
implementation files, runs the test-file command, and restores. No elapsed time is returned
by `AuthorTestGateResult` (`AuthorTestGate.cs:46-50`).

**Stage 9 — Verify (`RelayDriver.cs:222`)**

```
src/VisualRelay.Core/Execution/RelayDriver.cs:222
    var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
```

The stage stopwatch starts at `RelayDriver.cs:88` and covers: subagent invocation, optional
bootstrap check (`RelayDriver.cs:199`), optional guard check, the test run at line 222, and
the optional baseline-verify stash/restore (`RelayDriver.cs:251`). The test run itself is
not separately timed.

**Stage 10 — Fix-verify loop (`RelayDriver.VerifyFix.cs:160`)**

```
src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs:160
    var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);
```

Same pattern: the stage stopwatch starts at `RelayDriver.VerifyFix.cs:90` and wraps the
entire iteration including subagent, bootstrap, guard, and the test run. No separate timing
for the test call.

**`ShellTestRunner` discards elapsed time (`ShellTestRunner.cs:10-17`)**

```
src/VisualRelay.Core/Execution/ShellTestRunner.cs:10-17
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var result = await ProcessCapture.RunAsync(...);
        return new TestRunResult(result.ExitCode, output, result.TimedOut);
    }
```

`ProcessCapture.RunAsync` returns `(ExitCode, Output, TimedOut)` — no elapsed time.
`TestRunResult` (`Domain`) carries only `ExitCode`, `Output`, `TimedOut`. Elapsed time
must be captured by wrapping `TestRunner.RunAsync` at the call site with a `Stopwatch`,
or by returning it from `TestRunResult` (preferred, avoids scattering stopwatches).

### The per-stage metrics data model

**`StageStatusEntry` (`src/VisualRelay.Domain/StageStatus.cs:11-21`)**

```csharp
public sealed record StageStatusEntry(
    int Stage,
    string Name,
    string Status,
    string? Check = null,
    double? DurationSeconds = null,
    double? CostUsd = null,
    int? Turns = null,
    string? Model = null,
    string? Error = null,
    string? TaskInputHash = null);
```

A new nullable `double? TestDurationSeconds` field is the right addition here — consistent
with the existing nullable pattern, requires no migration (old status.json files simply
deserialize with `null`), and is camelCase-serialized to `testDurationSeconds` in
status.json (`StageStatus.cs:30`).

**Persistence: `StageStatus.cs:37-46`** — atomic write via `.tmp` rename.
**Read: `RelayRunHistory.cs:118-122`** — deserializes from `.relay/{taskId}/status.json`.

**Populated by `MarkStatusDone` (`RelayDriver.Artifacts.cs:264-277`):**

```csharp
private static void MarkStatusDone(List<StageStatusEntry> entries, RelayStageDefinition stage,
    TimeSpan elapsed, RelayCostEstimate? cost, string? check)
{
    ...
    entries[idx] = entries[idx] with
    {
        Status = "Done",
        DurationSeconds = cost?.DurationSeconds > 0 ? cost.DurationSeconds : elapsed.TotalSeconds,
        CostUsd = ...,
        Turns = ...,
        Model = ...,
        Check = check
    };
}
```

`TestDurationSeconds` should be added as a new parameter here, passed through from the
call sites that know the test-run elapsed time.

### The per-stage UI: how metrics reach the stage cards

**Event flow (live run):**

`PublishStageDoneAsync` (`RelayDriver.Events.cs:11-50`) builds a `data` dictionary with
`time`, `cost`, `sessionCost`, optionally `model` and `turns`, and publishes `stage_done`.
`ApplyStageEventMetric` (`MainWindowViewModel.Helpers.cs:250-275`) reads those keys and sets
`DurationLabel`, `CostLabel`, `ModelLabel`, `TurnsLabel` on `StageRowViewModel`.

A new `testTime` key in the `stage_done` event data (formatted with the same
`FormatDuration` helper) threads through to a new `TestDurationLabel` property on
`StageRowViewModel`.

**Display — `StageRowViewModel.MetricLabel` (`StageRowViewModel.cs:35-36`):**

```csharp
public string MetricLabel => (CostLabel == "No cost yet" ? DurationLabel : $"{DurationLabel}  {CostLabel}")
    + (string.IsNullOrEmpty(TurnsLabel) ? string.Empty : $"  {TurnsLabel}");
```

This computed string is what appears on the stage card at `StageBoard.axaml:81-86`. The new
test-duration chip should append to `MetricLabel` when `TestDurationLabel` is non-empty, e.g.
`…  🧪 12s` or `…  test 12s` (plain text, no emoji — keep consistent with existing labels).
Alternatively, render it as a separate `TextBlock` on a new row in the card grid
(`StageBoard.axaml:57-87`, `Grid RowDefinitions="Auto,Auto,Auto"`); a fourth row keeps it
legible without cramping `MetricLabel`.

**`ApplyMetric` on history load (`StageRowViewModel.cs:135-143`):** `StageRunMetric` (the
history DTO read from `RunMetrics`/`StageStatusEntry`) also needs a `TestDurationSeconds`
field so the label populates when the operator opens a past run.

**Total duration is already shown** (`StageRowViewModel.cs:86-96` `DurationLabel`). The new
label is therefore clearly the test portion only; label it `test Ns` to remove ambiguity.

## What to build

TDD: write each failing test first, confirm red, then implement.

### 1. Extend `TestRunResult` to carry elapsed time

Add a `TimeSpan Elapsed` property to `TestRunResult` (in `VisualRelay.Domain` or
`VisualRelay.Core`) and populate it in `ShellTestRunner.RunAsync` with a `Stopwatch`
around the `ProcessCapture.RunAsync` call. This is the single capture point; call sites
get elapsed without each needing their own stopwatch.

- `ShellTestRunner.cs:10-17` — wrap `ProcessCapture.RunAsync` with `Stopwatch`.
- Update `TestRunResult` record/class to include `TimeSpan Elapsed`.
- Update all `TestRunResult` construction sites (test doubles, stubs) to pass `Elapsed`
  (use `TimeSpan.Zero` for test doubles that don't care, `TimeSpan.FromSeconds(N)` for
  ones that do).

### 2. Accumulate `testDurationSeconds` per stage and persist it

Thread the test-run elapsed time from the `TestRunResult` (or from the `AuthorTestGateResult`)
into `MarkStatusDone` and `StageStatusEntry`.

- Add `double? TestDurationSeconds` to `StageStatusEntry`
  (`src/VisualRelay.Domain/StageStatus.cs:11-21`).
- Add `double? testDurationSeconds` parameter to `MarkStatusDone`
  (`RelayDriver.Artifacts.cs:264-277`) and set it in the `with` expression.
- **Stage 5:** after `AuthorTestGate.RunAsync`, pass `gateResult.TestResult.Elapsed.TotalSeconds`
  into the `MarkStatusDone` call for stage 5 (`RelayDriver.cs` around line 158+).
  If `AuthorTestGate` did not run (no impl files changed), pass `null`.
- **Stage 9:** after `_dependencies.TestRunner.RunAsync` at `RelayDriver.cs:222`,
  pass `testResult.Elapsed.TotalSeconds` (plus, optionally, the bootstrap/guard elapsed if
  you want total harness-test time, but start with just the `config.TestCommand` call).
- **Stage 10:** after `_dependencies.TestRunner.RunAsync` at `RelayDriver.VerifyFix.cs:160`,
  pass `testResult.Elapsed.TotalSeconds`.

`status.json` will gain `"testDurationSeconds": <number>` for stages 5, 9, 10 when they ran
the test command; all other stages serialize it as `null` (omitted by `JsonIgnoreCondition`
or simply `null`).

### 3. Emit `testTime` in `stage_done` event and display in UI

- In `PublishStageDoneAsync` (`RelayDriver.Events.cs:31-46`), add a new optional `double?
  testDurationSeconds` parameter and, when non-null, set `data["testTime"] =
  FormatDuration(testDurationSeconds.Value)`.
- In `ApplyStageEventMetric` (`MainWindowViewModel.Helpers.cs:250-275`), read `testTime`
  from event data and set a new `TestDurationLabel` property on `StageRowViewModel`.
- Add `TestDurationLabel` to `StageRowViewModel` (backing field + property, raises
  `MetricLabel` changed). Extend `MetricLabel` to append `  test {TestDurationLabel}` when
  `TestDurationLabel` is non-empty.
- In `StageRowViewModel.ApplyMetric` (`StageRowViewModel.cs:135-143`), populate
  `TestDurationLabel` from the history DTO (`StageRunMetric` / `StageStatusEntry`).
- In `ClearMetric`, reset `TestDurationLabel` to `string.Empty`.

The label format `test 12s` is sufficient — short, unambiguous, consistent with how
`DurationLabel` already formats seconds. Render it as part of `MetricLabel` so it appears in
the existing `TextBlock` at `StageBoard.axaml:81-86` with no XAML changes required; or as a
second `TextBlock` row if the card becomes too wide (judgment call at implementation time —
the spec accepts either, provided the metric is visible without expanding the card).

## Done when

- **Test-run duration captured:** `TestRunResult` carries `Elapsed`; `ShellTestRunner` sets
  it from a `Stopwatch` around `ProcessCapture.RunAsync`. Unit test: construct a
  `ShellTestRunner` with a fast command and assert `result.Elapsed > TimeSpan.Zero`.
- **Persisted in `status.json`:** after a harness run, `status.json` for stages 5, 9, and 10
  contains `"testDurationSeconds": <positive number>` when the test command ran; stages that
  do not run a harness test command have `null` / field absent. Integration test or snapshot
  test asserts the field is present and positive for a stage-9 run.
- **Shown in UI alongside existing metrics:** the stage card for stage 9 (and 5/10 when they
  ran) shows `test Ns` (or `test Nm Ns`) beside the existing `DurationLabel`/`CostLabel`/
  `TurnsLabel`. A headless Avalonia UI test asserts that after `ApplyMetric` with a
  non-null `TestDurationSeconds`, the rendered `MetricLabel` string contains `"test "`.
  Stages that did not run a harness test command show no `test` label (empty).
- **History load populates the label:** opening a past run (via `ApplyMetric` from
  `StageStatusEntry`) also shows the `test` label when `TestDurationSeconds` is non-null.
- **`./visual-relay check` green** after all changes.
- **No file exceeds 300 lines** added or modified.
- **Conventional Commit subject**, e.g.
  `feat(metrics): surface harness test-command duration per pipeline stage`.

## PITFALL — keep the new timing test FAST (this task previously FLAGGED on a 10-min suite timeout)
The prior attempt's `TestRunnerElapsedTests` ran a real long/blocking subprocess to measure elapsed time and
hung the full `dotnet test` suite past the 10-min `testTimeoutMs`. The new test(s) MUST:
- Use a MOCK/fake `ITestRunner` (e.g. `ScriptedTestRunner`/`RecordingTestRunner`) or at most a tiny bounded
  sleep (<=0.3s) to exercise the duration-capture logic — NEVER a real multi-second/blocking subprocess.
- Bound every wait; the whole suite must stay well under 10 min (it normally runs ~2 min).
- After writing, run ONLY the new test class and confirm it finishes in SECONDS.
