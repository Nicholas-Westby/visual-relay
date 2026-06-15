# Harness: skip the Fix-verify LLM call when Verify is already green

## Intro

On the happy path, stage 10 "Fix-verify" runs a full LLM subagent call even when stage 9
"Verify" has already passed green — fixing nothing, because there is no failing test. In one
real run stage 9 went green yet stage 10 still made a balanced-tier call ($0.013 / 52s); in a
flakier environment the same do-nothing stage churned ~9 minutes. Stage 10 exists **only** to
fix a red Verify, so when Verify is green its LLM call is pure waste. This task suppresses the
stage-10 LLM call on a green Verify while recording stage 10 as a normal green stage (seal +
status), so every downstream invariant — all-stages-Done status, the 10-seal chain, and the
commit-gate resume fast path — is preserved. The red path is left **completely unchanged**.

## Current state (researched)

> **Freshness contract:** All locations below are described by quoted code snippets and method
> names so they survive line drift. Locate each anchor by searching for the quoted snippet (NOT
> by line number); the parenthetical line numbers are from clean checkout `a574ee8` and are
> illustrative only. If a quoted snippet no longer exists, STOP and re-research — the driver
> has changed underneath this spec.

### Stage 10 is meant ONLY to fix a red Verify — CONFIRMED redundant on green

`RelayStages.All` (`src/VisualRelay.Core/Execution/RelayStages.cs`) defines stage 10 as
`Stage(10, "Fix-verify", "balanced", ...)`, and its system prompt (`SystemPromptFor`, the
`"Fix-verify"` arm) opens with **"Fix failures from the pinned suite."** It is purely a
red-recovery stage — it has no other documented purpose.

The only legitimate way to run stage 10 today is from the **red** branch of stage 9. In
`RunTaskAsync` (`src/VisualRelay.Core/Execution/RelayDriver.cs`), inside the
`if (stage.Number == 9)` block, after the suite runs:

```csharp
check = testResult.ExitCode == 0 ? "green" : "red";
if (bootstrapFailed || guardFailed)
    check = "red";
...
if (check != "green")
{
    ...
    // Genuinely red — record stage 9, then enter fix-verify loop.
    (previousSeal, taskHash) = await RecordStageAsync(... stage, body, check, ...);
    var (loopOutcome, ...) = await RunVerifyFixLoopAsync(...);
    if (loopOutcome is not null)
        return loopOutcome;
    previousSeal = prevSeal; taskHash = tHash; ...
    stage10Handled = true;        // ← set ONLY on the red path
}
```

`stage10Handled` is initialised `var stage10Handled = false;` just above the stage loop and is
assigned `true` **only** inside this red branch (after `RunVerifyFixLoopAsync` returns success).

### Why the do-nothing stage-10 call happens (the bug)

The stage loop guards stage 10 like this:

```csharp
if (stage.Number == 10 && stage10Handled)
    continue;
```

When Verify is **green**, the red branch above never runs, so `stage10Handled` stays `false`.
The loop falls through and treats stage 10 as an ordinary LLM stage: it builds an invocation
and calls `_dependencies.SubagentRunner.RunAsync(...)` — a balanced-tier LLM call with **no**
`LastTestOutput` (nothing to fix). That call, plus the subsequent `RecordStageAsync` at the
bottom of the loop body

```csharp
if (stage.Number != 9 || !stage10Handled)
{
    (previousSeal, taskHash) = await RecordStageAsync(... stage, body, check, ...);
}
```

is the wasted work. (Note `check` for this stage-10 iteration is `null` — stage 10 is not one
of the stages that computes a `check` in the loop, so today the green-path stage-10 seal/status
carries no check value.)

### `RunVerifyFixLoopAsync` sets `stage10Handled` only on the red path — CONFIRMED

`src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`, `RunVerifyFixLoopAsync`, opens with
`var stage = RelayStages.All[9]; // Stage 10 — Fix-verify` and loops `for (var attempt = 1;
attempt <= maxLoops; attempt++)`, calling the subagent each attempt and re-verifying. It returns
`(null, ...)` on green (success) and a flagged outcome otherwise. The caller (the red branch in
`RelayDriver.cs`) is the **only** site that sets `stage10Handled = true`, and it does so only
after this loop returns success. This method is not entered at all when Verify is green. The
red path is therefore entirely contained in `RunVerifyFixLoopAsync` + its single caller and must
not be touched by this change.

### Invariants the fix MUST preserve (these block a naive `continue`)

A naive "skip the whole stage-10 iteration" (e.g. extending the guard to
`if (stage.Number == 10 && (stage10Handled || verifyWasGreen)) continue;`) leaves stage 10 in
its seeded `"Waiting"` status with **no seal**, which breaks three things:

1. **All-stages-Done status.** `SeedStatusEntries` (`RelayDriver.Artifacts.cs`) seeds every
   stage `"Waiting"`. The happy-path test `RunTaskAsync_WritesStatusJson_AllStagesDone`
   (`tests/VisualRelay.Tests/RelayDriverStatusTests.cs`) asserts
   `Assert.All(entries, e => Assert.Equal("Done", e.Status))` over all 11 entries. A skipped
   stage 10 left `"Waiting"` fails this.

2. **Resume fast path.** `LoadResumeState` (`src/VisualRelay.Core/Execution/RelayDriver.Resume.cs`)
   computes `firstStageToRun` from `priorStatus.FirstOrDefault(e => e.Status != "Done")`. If
   stage 10 is `"Waiting"`, a resume sets `firstStageToRun = 10` and re-runs the do-nothing
   stage — and the commit-gate fast path never fires.

3. **Commit-gate resume re-validation.** `ValidateCommitGateResumeAsync`
   (`src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`) gates the skip-to-commit on
   ```csharp
   if (_options.Resume && firstStageToRun == 11
       && statusEntries.Count >= 10
       && statusEntries.Take(10).All(e => e.Status == "Done"))
   ```
   and then reads the recorded tree hash from **`seals[9]`** (the 10th seal):
   ```csharp
   if (seals.Count >= 10)
   {
       using var doc = JsonDocument.Parse(seals[9]);
       if (doc.RootElement.TryGetProperty("treeHash", out var th))
           recordedTreeHash = th.GetString() ?? string.Empty;
   }
   ```
   The resume scenario builder `SetupCommitGateResumeScenario`
   (`tests/VisualRelay.Tests/RelayDriverResumeTests.CommitGate.cs`) constructs a **10-entry seal
   chain** with stage 10 marked `Done`/`green`. So stage 10 must remain Done **and** still emit
   a seal carrying the (green) tree hash, or `RunTaskAsync_Resume_CommitGateWithMatchingHash_SkipsToCommit`
   regresses.

**Conclusion:** the correct minimal fix skips only the **LLM subagent call**, then records stage
10 as a normal **green** stage via the existing `RecordStageAsync` (seal + ledger + status +
`stage_done` event), inheriting stage 9's green outcome. This removes the waste while keeping
status all-Done, the 10-seal chain intact, and the resume fast path working.

### Existing `RecordStageAsync` is the recording primitive to reuse

`RecordStageAsync` (`src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`) already does
exactly what a recorded-but-not-run stage needs: `AppendLedgerSection`, compute tree hash
(`stage.Number >= 4 ? WorkingTreeHash(...) : string.Empty`), `SerializeSeal`, `MarkStatusDone`,
`WriteStatusAsync`, and `PublishStageDoneAsync`. It accepts an explicit `check` argument and a
`cost` (pass `null` — no LLM ran). Calling it for stage 10 with `check: "green"` and a short
synthetic `body` produces a seal indistinguishable in shape from the red-path stage-10 seal,
and (because tree hash is taken from the current worktree, unchanged since the green stage-9
record) carrying the same tree hash stage 9 recorded.

### Tests that currently assert stage 10 RUNS on green (must flip to "skipped")

Only two assertions encode the current (buggy) green-path behavior, both in
`tests/VisualRelay.Tests/RelayDriverFormatBeforeVerifyTests.cs`:

- `FormatCmd_Set_RunsBeforeGuardAtVerifyAndNoFixVerifyEntered` ends with:
  ```csharp
  // Stage 10 runs in the main loop (green stage 9 does not skip it),
  // but it was NOT entered via the fix-verify path — no failure output.
  var stage10 = subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
  Assert.NotNull(stage10);
  Assert.Null(stage10!.LastTestOutput);
  ```
- `FormatCmd_Unset_NeitherFormatterNorBehaviorChanges` ends with the **same** three-line block
  and comment.

These are the weakened assertions the reviewer referenced (`Assert.Null(stage10.LastTestOutput)`
where a real fix expectation should be "stage 10 was skipped"). Both must change to assert the
stage-10 **subagent invocation does NOT happen** on green (see "What to build" §3). Their other
assertions (formatter-before-guard ordering, `Committed` outcome) are unaffected and stay.

### Tests that assert stage 10 runs on the RED path — DO NOT TOUCH

These exercise the red path (stage 10 invoked **with** `LastTestOutput`) and must keep passing
unchanged — they are the regression wall proving the red path is intact:

| Test file | What it asserts (red-path stage 10) |
|---|---|
| `RelayDriverVerifyFixTests.cs` | `RunTaskAsync_FixableVerifyFailure...` (stage 9 red→seal red, stage 10 seal green, stage_start/done for 10); `RunTaskAsync_UnfixableVerifyFailure...`; `RunTaskAsync_MaxVerifyLoopsRespected...`; `RunTaskAsync_FixVerifyLoop_AgentReceivesFailingOutput` (stage 10 gets failing output; **no other stage** does) |
| `RelayDriverFormatBeforeVerifyTests.cs` | `FormatCmd_Set_RunsBeforeGuardInFixVerifyIteration` (stage 9 red → stage 10 runs with `big.cs` in `LastTestOutput`) |
| `RelayDriverBootstrapTests.cs` | stage 10 runs with bootstrap failure in `LastTestOutput` |
| `RelayDriverRepoGuardTests.cs` | stage 10 runs with guard violation in `LastTestOutput` (two cases) |
| `TargetedTestCommandTests.cs` | stage 9 red → stage 10 prompt uses targeted test command |

### What a skipped-but-recorded stage 10 looks like in `status.json` / seals

After this change, on the green path `status.json` (written by `StageStatusRecord.WriteAsync` to
`.relay/<taskId>/status.json`) has the stage-10 entry as
`{ "stage": 10, "name": "Fix-verify", "status": "Done", "check": "green", ... }` with
`costUsd: null`, `turns: null`, `model: null` (no LLM ran — `RecordStageAsync` with `cost: null`
yields these via `MarkStatusDone`). The seals file `<taskId>.seals` contains a 10th line
`{"kind":"stage","n":10,...,"check":"green"}` whose `treeHash` equals stage 9's. The new focused
test asserts on this; existing tests already tolerate it.

## What to build

A minimal, general change (no toolchain specifics) entirely within
`src/VisualRelay.Core/Execution/RelayDriver.cs`. Skip **only** the stage-10 LLM subagent call
when Verify is green; record stage 10 as a green stage via the existing `RecordStageAsync`; do
not enter `RunVerifyFixLoopAsync`. Leave the red path byte-for-byte unchanged.

### 1. Record stage 10 as green and mark it handled when Verify passes green

In `RunTaskAsync` (`RelayDriver.cs`), in the stage-9 block, locate the `if (check != "green")`
branch (the red path). Add an `else` that fires when Verify is **green**. Inside it:

- Record stage 10 as a green stage by reusing `RecordStageAsync`, inheriting the green outcome:
  ```csharp
  else
  {
      // Verify is green — stage 10 (Fix-verify) has nothing to fix. Skip its LLM
      // subagent call entirely, but still record stage 10 as a green stage so the
      // status record stays all-Done, the seal chain reaches 10 entries, and the
      // commit-gate resume fast path keeps working. No LLM ran → cost is null.
      var stage10 = RelayStages.All[9]; // Stage 10 — Fix-verify
      (previousSeal, taskHash) = await RecordStageAsync(
          rootPath, runId, taskId, taskDirectory, stage10,
          body: "_Skipped: Verify passed; nothing to fix._",
          check: "green", cost: null, stopwatch: Stopwatch.StartNew(),
          ledger, seals, statusEntries, manifest,
          previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
      stage10Handled = true;
  }
  ```
  - The body string is a short human-readable note; keep it generic (no toolchain words).
  - `RecordStageAsync` stops the stopwatch it is given; pass a fresh `Stopwatch.StartNew()` so
    the recorded duration for the skipped stage is ~0 (no work was done). Do **not** reuse the
    stage-9 stopwatch — it is still needed to record stage 9 below.
  - This `else` pairs with the existing `if (check != "green")`. The existing
    `else { check = "green"; }` that handles the baseline-excluded case (all failures
    pre-existing) is **nested inside** the red branch (`if (!config.BaselineVerify || ...)`),
    so it is unaffected — verify by reading the brace structure before editing. After that inner
    block reassigns `check = "green"`, control still needs to record stage 10; see §2.

  > Match argument names/order to the real `RecordStageAsync` signature at edit time (search its
  > definition in `RelayDriver.VerifyFix.cs`). Do not hand-roll seal/status writes — reuse the
  > method so the seal shape stays identical to the red path.

### 2. Handle the baseline-excluded green case too (failures all pre-existing)

There is a second way Verify ends up green: the red branch's inner
`else { check = "green"; // baseline-excluded: all failures pre-existing }`. After that block,
`stage10Handled` is still `false`, so today stage 10 also runs a do-nothing LLM call in this
case. The fix must cover it as well.

The cleanest way to cover **both** green routes without duplicating the record call: instead of
(or in addition to) the §1 `else`, gate the stage-10 record on `check == "green"` after the
whole stage-9 `if (check != "green") { ... } else { ... }` construct resolves. Concretely, at
the **end** of the `if (stage.Number == 9)` block (after both branches have run and `check` has
its final value), add:

```csharp
// If Verify ended green by any route (first-try green, or baseline-excluded), the
// Fix-verify LLM call is redundant. Record stage 10 as green here and skip it in the loop.
if (check == "green" && !stage10Handled)
{
    var stage10 = RelayStages.All[9]; // Stage 10 — Fix-verify
    (previousSeal, taskHash) = await RecordStageAsync(
        rootPath, runId, taskId, taskDirectory, stage10,
        "_Skipped: Verify passed; nothing to fix._",
        "green", null, Stopwatch.StartNew(),
        ledger, seals, statusEntries, manifest,
        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);
    stage10Handled = true;
}
```

Choose **one** placement (the consolidated end-of-block guard above is preferred over a separate
`else`, because it covers both green routes with a single code path and is easier to reason
about). The `!stage10Handled` guard ensures it never double-records when the red→success path
already set the flag. Do **not** also keep a §1 `else` if you use this consolidated form — pick
one.

The existing loop guard already does the rest:

```csharp
if (stage.Number == 10 && stage10Handled)
    continue;
```

With `stage10Handled == true`, the stage-10 iteration is skipped — no invocation, no second
`RecordStageAsync` (the bottom-of-loop `RecordStageAsync` is only reached when the iteration
body runs). Stage 9's own record at the bottom of the loop still fires, because that line is
guarded by `if (stage.Number != 9 || !stage10Handled)` — and stage 9 must record. **Verify this
interaction:** after the green change, `stage10Handled` becomes `true` during the stage-9
iteration, so `stage.Number != 9` is false AND `!stage10Handled` is false → the bottom-of-loop
`RecordStageAsync` is **skipped for stage 9**. That means stage 9's record must now also be
emitted explicitly inside the green path (exactly as the red path already records stage 9
explicitly before entering the loop). **Mirror the red path:** record stage 9 first, then record
the synthetic green stage 10. See §2a.

### 2a. Ensure stage 9 itself is still recorded on the green path

The bottom-of-loop guard `if (stage.Number != 9 || !stage10Handled)` was written so that, on the
red path, stage 9 is **not** double-recorded (the red branch already called `RecordStageAsync`
for stage 9 before the loop). Once the green path also sets `stage10Handled = true`, this guard
will likewise skip the bottom-of-loop stage-9 record. Therefore the green path must record stage
9 explicitly too, before recording the synthetic stage 10 — same as the red path does. Final
shape of the consolidated green block:

```csharp
if (check == "green" && !stage10Handled)
{
    // Record stage 9 (green) explicitly — the bottom-of-loop recorder is suppressed
    // once stage10Handled is set, mirroring the red path's explicit stage-9 record.
    (previousSeal, taskHash) = await RecordStageAsync(
        rootPath, runId, taskId, taskDirectory, stage, body, check, cost, stopwatch,
        ledger, seals, statusEntries, manifest,
        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

    // Stage 10 (Fix-verify) has nothing to fix on a green Verify — skip its LLM call,
    // record it as green so status stays all-Done and the seal chain reaches 10.
    var stage10 = RelayStages.All[9];
    (previousSeal, taskHash) = await RecordStageAsync(
        rootPath, runId, taskId, taskDirectory, stage10,
        "_Skipped: Verify passed; nothing to fix._",
        "green", null, Stopwatch.StartNew(),
        ledger, seals, statusEntries, manifest,
        previousSeal, taskHash, sessionCostUsd, unknownCostStageCount, cancellationToken);

    stage10Handled = true;
}
```

Here `stage` is the stage-9 definition currently in scope, `body`/`check`/`cost`/`stopwatch` are
the stage-9 locals already in scope. After this, the bottom-of-loop recorder is correctly
suppressed for stage 9 (guard false) and the loop guard skips the stage-10 iteration. **Keep the
red path's existing explicit stage-9 `RecordStageAsync` + `RunVerifyFixLoopAsync` exactly as-is.**

> Implementation note: this is the minimal change — roughly one `if` block (~15 lines) added at
> the end of the stage-9 handler, plus zero changes to the loop guards (they already do the
> right thing once `stage10Handled` is set). No new fields, no signature changes, no new files.

### 3. Update the two weakened green-path assertions

In `tests/VisualRelay.Tests/RelayDriverFormatBeforeVerifyTests.cs`, in **both**
`FormatCmd_Set_RunsBeforeGuardAtVerifyAndNoFixVerifyEntered` and
`FormatCmd_Unset_NeitherFormatterNorBehaviorChanges`, replace the trailing block:

```csharp
// Stage 10 runs in the main loop (green stage 9 does not skip it),
// but it was NOT entered via the fix-verify path — no failure output.
var stage10 = subagent.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
Assert.NotNull(stage10);
Assert.Null(stage10!.LastTestOutput);
```

with an assertion that stage 10's LLM call was **skipped** on green:

```csharp
// Verify was green on the first try, so the Fix-verify (stage 10) LLM call is
// skipped entirely — there must be no stage-10 subagent invocation.
Assert.DoesNotContain(subagent.Invocations, i => i.Stage.Number == 10);
```

Leave every other assertion in those two tests intact (formatter-before-guard ordering,
`Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status)`, etc.). Confirm the capturing
runner used in each (`CapturingSubagentRunner`) exposes `Invocations` (it does — used elsewhere
in the same file).

### 4. Add a focused regression test (TDD — write FIRST, watch it fail)

Add a new test (e.g. in `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`, placed next to
the red-path fix-verify tests, or a small new `RelayDriverSkipFixVerifyOnGreenTests.cs`). It must
pin **all** of the following on a first-try-green Verify:

```csharp
[Fact]
public async Task RunTaskAsync_VerifyGreen_SkipsFixVerifyLlmCall_ButRecordsStage10Green()
{
    using var repo = TestRepository.Create();
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
    repo.WriteTask("green-skip", "# Verify green, skip fix-verify\n");
    var runner = new CapturingSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),     // stage 5 author gate (must be red)
        new TestRunResult(0, "green"));  // stage 9 verify — green on first try
    var sink = new InMemoryRelayEventSink();
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, sink),
        RelayDriverOptions.NoGitCommit);

    var outcome = await driver.RunTaskAsync(repo.Root, "green-skip");

    Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

    // (1) NO stage-10 LLM subagent invocation happened.
    Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 10);

    // (2) status.json still has all 11 stages Done, and stage 10 is green.
    var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "green-skip"));
    Assert.Equal(11, entries.Count);
    Assert.All(entries, e => Assert.Equal("Done", e.Status));
    var stage10 = entries.Single(e => e.Stage == 10);
    Assert.Equal("green", stage10.Check);
    Assert.Null(stage10.CostUsd);   // no LLM ran
    Assert.Null(stage10.Turns);

    // (3) The seal chain reached 10 stage seals; stage 10 seal is green.
    var seals = await File.ReadAllLinesAsync(
        Path.Combine(repo.Root, ".relay", "green-skip", "green-skip.seals"));
    Assert.Contains(seals, l =>
        l.Contains("\"n\":10", StringComparison.Ordinal) &&
        l.Contains("\"check\":\"green\"", StringComparison.Ordinal));

    // (4) stage_done event for stage 10 was still published (UI parity).
    Assert.Contains(sink.Events, e => e.EventName == "stage_done" && e.StageNumber == 10);
}
```

Optionally add a second `[Fact]` for the **baseline-excluded** green route (failures all
pre-existing) if it can be scripted cheaply, asserting the same "no stage-10 invocation". If not
trivially scriptable, the §3 edits plus the test above are sufficient; the red-path wall (the
DO-NOT-TOUCH table) proves the red path is unchanged.

Confirm `CapturingSubagentRunner` and `ScriptedTestRunner` signatures against
`tests/VisualRelay.Tests/SubagentRunnerTestDoubles.cs` / `TestDoubles.cs` at write time (used by
neighboring tests; `SeedHappyPath` and `Invocations` are already exercised in this suite).

## Done when

- **Write the failing tests first** (the new §4 test asserting no stage-10 invocation on green;
  the §3 edits) and watch them fail against the unpatched driver before changing `RelayDriver.cs`.
- On a green Verify (first-try green AND baseline-excluded), stage 10's **LLM subagent call is
  not made** — `runner.Invocations` contains no entry with `Stage.Number == 10`.
- Stage 10 is still **recorded green**: `status.json` has all 11 stages `"Done"` with stage 10
  `check == "green"`, `costUsd == null`, `turns == null`; the seals file contains a 10th
  `"n":10 ... "check":"green"` seal whose `treeHash` matches stage 9's; a `stage_done` event for
  stage 10 is published.
- The **red path is unchanged**: every test in the DO-NOT-TOUCH table passes without
  modification — `RunVerifyFixLoopAsync` and its single caller are byte-for-byte unchanged, and
  stage 10 still runs (with `LastTestOutput`) whenever Verify is red, bootstrap fails, or a guard
  fails.
- The two weakened assertions in `RelayDriverFormatBeforeVerifyTests.cs` now assert stage 10 is
  **skipped** on green (`Assert.DoesNotContain(... Stage.Number == 10)`), not that it runs with
  null output.
- **Resume still works:** `RunTaskAsync_Resume_CommitGateWithMatchingHash_SkipsToCommit` and
  `RunTaskAsync_Resume_CommitGateWithHashMismatch_RestartsFromStage5`
  (`RelayDriverResumeTests.CommitGate.cs`) pass unmodified — the 10-Done-stages /
  10-seal-chain invariant the commit-gate fast path depends on is preserved.
- The change is confined to `src/VisualRelay.Core/Execution/RelayDriver.cs` (~15 lines added in
  the stage-9 handler) plus test edits/additions. No new fields, no signature changes, no new
  source files; nothing toolchain-specific (no `.NET`/`dotnet`/`nix` strings in the new code).
- **`./visual-relay check` is green** — all pre-existing tests pass; the suite no longer encodes
  "stage 10 runs on green" anywhere.
- **Conventional Commit** subject candidates:
  - `fix(driver): skip Fix-verify LLM call when Verify already green`
  - `perf(driver): record stage 10 green without an LLM call on a green verify`
  - `fix(driver): stop running do-nothing Fix-verify on the happy path`
