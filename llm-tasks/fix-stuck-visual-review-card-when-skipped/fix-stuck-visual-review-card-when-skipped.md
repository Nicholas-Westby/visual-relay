# Publish a Terminal Event When Visual-Review Is Skipped (Stuck "Running" Card)

Observed and diagnosed 2026-07-08 on a live run: the stage **08 Visual-review** card ticked
"Running 9m+" — its timer counting since the review pair started — while stage 09 Fix and
later stages proceeded normally, making it look like Fix had jumped ahead of a still-running
review. The Activity pane filtered to stage 08 showed exactly ONE event: `stage_start`.
On-disk truth was correct the whole time: triage had decided `{"visualReview":"skip"}`
("no UI markup, styles, layout… nothing rendered to visually inspect"), `status.json` had
stage 8 `"Skipped"`, and the ledger had the `_Skipped: …_` section. Only the live GUI was
wrong, and it stays wrong until the stage board is next rehydrated from status.json
(task reselect or the next run).

## Root cause (verified in source)

Review (7) and Visual-review (8) run as a concurrent pair in `RunReviewPairAsync`
(`src/VisualRelay.Core/Execution/RelayDriver.ReviewPair.cs`). The pair publishes
`stage_start` for BOTH stages up front — before the cheap stage-0 triage agent decides
whether a visual pass is worthwhile:

```csharp
// Publish stage_start for both.
await PublishAsync("info", "stage_start", rootPath, runId, taskId, reviewStage, cancellationToken);
await PublishAsync("info", "stage_start", rootPath, runId, taskId, visualStage, cancellationToken);
```

When triage answers `skip` (or no `"vision"` tier profile is configured), no visual agent is
ever launched. After Review completes, the skip branch records everything on disk by
hand-rolling what `RecordStageAsync` does — ledger section, status write, artifact hash,
seal, artifacts — but publishes **no event at all**:

```csharp
// Record Visual-review as skipped.
var skipReason = triageResult is { VisualReview: "skip" }
    ? $"_Skipped: {triageResult.Reason}_"
    : "_Skipped: vision tier unconfigured_";
AppendLedgerSection(ledger, visualStage, skipReason);
MarkStatusSkipped(statusEntries, visualStage);
await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
var h = Hashing.Sha256Hex("8", visualStage.Name, skipReason);
var seal = Hashing.Sha256Hex(previousSeal, "8", DateTimeOffset.UtcNow.ToString("O"), h, string.Empty, string.Empty);
seals.Add(SerializeSeal(8, h, string.Empty, seal, null));
previousSeal = seal; taskHash = seal;
await WriteArtifactsAsync(taskDirectory, taskId, ledger.ToString(), seals, cancellationToken);
```

The GUI's live stage board is event-driven — `ApplyStageEventToBoard` in
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs`: `stage_start` calls
`stage.MarkRunning(...)` (opens the ticking timer), and only
`stage_done`/`stage_report`/`flagged` ever change the status afterwards. The `"Skipped"`
mapping ALREADY exists on the GUI side — `stage_done` carries an optional `status` value:

```csharp
stage.Status = relayEvent.EventName switch
{
    "stage_done" or "stage_report" =>
        relayEvent.Data is not null && relayEvent.Data.TryGetValue("status", out var s) && !string.IsNullOrEmpty(s) ? s : "Done",
    "flagged" => "Flagged",
    _ => stage.Status
};
```

Proof the contract works end-to-end: the stage-5 "skip automated testing" path (in
`RelayDriver.cs`) records its skip through `RecordStageAsync`
(`src/VisualRelay.Core/Execution/RelayDriver.Invocation.cs`), which preserves an
already-Skipped status entry and publishes it on the `stage_done`:

```csharp
var alreadySkipped = idx >= 0 && idx < statusEntries.Count
    && "Skipped".Equals(statusEntries[idx].Status, StringComparison.OrdinalIgnoreCase);
if (!alreadySkipped)
    MarkStatusDone(statusEntries, stage, stopwatch.Elapsed, cost, check, testDurationSeconds);
await WriteStatusAsync(taskDirectory, statusEntries, cancellationToken);
var status = idx >= 0 && idx < statusEntries.Count ? statusEntries[idx].Status : null;
await PublishStageDoneAsync(rootPath, runId, taskId, stage, stopwatch.Elapsed, cost, sessionCostUsd, unknownCostStageCount, cancellationToken, testDurationSeconds, status: status);
```

That makes the pair's skip branch the only terminal-event-less completion path in the
driver — and since triage skips the visual pass for every non-visual change, most tasks
hit it.

## What to build (TDD-first)

1. **Record the skip through `RecordStageAsync`** — replace the entire hand-rolled block
   quoted above (everything from `AppendLedgerSection` through `WriteArtifactsAsync`) with
   the same shape the stage-5 skip uses:

   ```csharp
   MarkStatusSkipped(statusEntries, visualStage);
   (previousSeal, taskHash) = await RecordStageAsync(rootPath, runId, taskId, taskDirectory,
       visualStage, skipReason, "green", null, Stopwatch.StartNew(), ledger, seals,
       statusEntries, manifest, previousSeal, taskHash, sessionCostUsd,
       unknownCostStageCount, cancellationToken);
   ```

   Why each piece: `MarkStatusSkipped` first, so `RecordStageAsync`'s `alreadySkipped` guard
   preserves `"Skipped"` and publishes it as the `stage_done`'s `Data["status"]`; body =
   `skipReason` (byte-identical ledger section and artifact hash to today — `RecordStageAsync`
   calls the same `AppendLedgerSection` and computes the same
   `Sha256Hex(stage.Number, stage.Name, body)`); cost = `null` (no invented cost; renders "?"
   exactly like the stage-5 skip); a fresh `Stopwatch.StartNew()` so the event's duration is
   ~0s — it must NOT reflect Review's elapsed time (no stage-8 stopwatch exists in this
   branch today; do not reuse anything started at pair start).

2. **Intended seal-line change, do not "preserve" the old shape.** Routing through
   `RecordStageAsync` makes the stage-8 skip seal carry a working-tree hash (stage ≥ 4) and
   `"check":"green"`, where the hand-rolled seal wrote empty treeHash and no check. This is
   deliberate and verified safe: resume never re-verifies seal composition — it only chains
   from the last line's `seal` property (`RelayDriver.Resume.cs`); the commit gate only
   stages the seals file by path (`RelayDriver.CommitGate.cs`); and no existing test asserts
   the stage-8 skip seal's fields. One seal line per stage, chain still well-formed.

3. **Both skip variants emit the event.** Triage `"skip"` and vision-tier-unconfigured take
   the same `else` branch; the single `RecordStageAsync` call covers both. Keep the two
   `skipReason` strings exactly as they are.

4. **Tests** (all red-first, then implement):
   - In `tests/VisualRelay.Tests/RelayDriverReviewPairTests.Orchestration.cs`, extend
     `RunTaskAsync_TriageSkip_NoVisualReviewInvocation` (already uses
     `TriageSkipSubagentRunner` and an `InMemoryRelayEventSink` named `sink`) to also assert:
     `sink.Events` contains a `stage_done` with `StageNumber` 8 and
     `Data["status"] == "Skipped"`.
   - Add `RunTaskAsync_VisionUnconfigured_PublishesSkippedStageDone` in the same file: seed
     the repo with `WriteConfigWithDownshift` (`tests/VisualRelay.Tests/TestDoubles.cs` —
     it writes a config with NO `tierProfiles`, which is what makes `visionConfigured`
     false; do not add a vision tier), plain `ScriptedSubagentRunner.SeedHappyPath`, and
     assert the same stage-8 `stage_done` with `Data["status"] == "Skipped"` plus the
     `_Skipped: vision tier unconfigured_` ledger line.
   - Add a VM-side pin in the `StageCardCumulativeMetricsTests.cs`/`LiveStateViewModelTests.cs`
     style, dispatched through `RelayEventTestDispatch`
     (`tests/VisualRelay.Tests/RelayEventTestDispatch.cs` — the reflection helper that feeds
     the private `HandleRelayEvent`, the same entry point the live sink uses): a stage-8
     `stage_start` followed by a stage-8 `stage_done` with `Data["status"] = "Skipped"`
     leaves the stage row's `Status == "Skipped"`.
   - Existing regression pins that must stay green unmodified:
     `RunTaskAsync_ReviewAndVisualReviewEventsPublished` (happy path where the visual agent
     runs — its stage-8 `stage_done` keeps status `Done`), the `_Skipped:` ledger assertions,
     and `RelayDriverSkipTestsTests` (stage-5 skip semantics untouched).

## Done when

- A run whose triage skips Visual-review ends with the stage 08 card reading **Skipped** (no
  ticking timer) while Fix and later stages proceed; the stage-08 event stream reads
  `stage_start` → `stage_done` (status Skipped, ~0s duration).
- Ledger content and status.json are byte-for-byte what they are today; the only artifact
  delta is the stage-8 skip seal line now carrying treeHash + `"check":"green"` (per item 2).
- All tests above pass; `./visual-relay check` passes.

## Guardrails

- Driver (`VisualRelay.Core`) production changes only; tests may touch `tests/`. No
  production GUI changes — `ApplyStageEventToBoard` already maps `Data["status"]`.
- Do NOT redesign the pair's up-front `stage_start` publication for both stages, and do NOT
  move the skip recording earlier than Review's completion (artifact ordering: Review's
  ledger section and seal come first).
- Out of scope: the flagged-mid-pair paths (`FlagAsync` on a red Review/Visual-review) can
  also strand stage 8's status as "Running" in status.json; that is a separate defect —
  do not touch `FlagAsync` here.
- `RelayDriver.ReviewPair.cs` sits at exactly the 300-line file guard limit
  (`tools/VisualRelay.Guards/FileSizeGuard.cs` fails files with more than 300 lines). The
  prescribed replacement deletes more lines than it adds, so the guard passes without any
  other reshuffling — if you find yourself needing to grow the file, you've drifted from
  item 1.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs.
