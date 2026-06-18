# Fix-Verify Loop: Converge or Bail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Fix-verify gate converge or bail — detect non-convergence on attempt 2 instead of burning all `MaxVerifyLoops` attempts; escalate the agent's verify command to the full gate and teach it to read the exit code (no reward hacking); extend the distiller and feed the real failure to the agent; isolate BOTH authoritative gates (stage 9 + stage 10) in a full-fidelity snapshot so suite writes don't pollute the repo; and make every authoritative verdict observable (`verify_result` event + persisted output artifact) — the R5 prerequisite, built first.

**Architecture:** Most behavior change is the loop's gate verdict, kept in `RelayDriver.VerifyFix.cs` (loop + minimal call-site edits) with new logic isolated into NEW partials to respect the 300-line file-size guard: `RelayDriver.VerifyObservability.cs` (Task 1), `RelayDriver.ConvergenceGuard.cs` (Task 2), `RelayDriver.VerifyWorktree.cs` (Task 8). The single authoritative-gate function `RunTestCommandWithRetryAsync` (`RelayDriver.Bootstrap.cs`) is called at BOTH stage 9 (`RelayDriver.cs:193`) and stage 10 (`VerifyFix.cs:155`); Tasks 1 and 8 touch BOTH call sites. The distiller `ExtractFailureReason` (`ProcessRunners.Diagnostics.cs`) is extended in Task 4. Prompts live in `RelayStages.cs`. New events (`verify_result`, `verify_mutated_tree`, `verify_retry`/`verify_retry_pass` relabels) flow through the existing `IRelayEventSink` / `InMemoryRelayEventSink` test seam. Tests live in `RelayDriverVerifyFixTests.cs`, `RelayDriverRetryFlakyVerifyTests.cs`, `CodingStageSystemPromptTests.cs`, `TargetedTestCommandTests.cs`, `SwivalSubagentRunnerToolPreflightTests.cs`, and `SandboxedTestRunnerArgumentTests.cs`. Several EXISTING tests are updated in place (called out per task): the max-loops test (Task 2), the stage-10 targeted-command assertion (Task 3), and the retry-label/flaky assertions (Task 5).

**Tech Stack:** C# / net10.0, xUnit v3, `InMemoryRelayEventSink`, `ScriptedTestRunner` (queue-based `ITestRunner` fake), `CapturingSubagentRunner` / `ScriptedSubagentRunner` (`ISubagentRunner` fakes), `TestRepository.Create()` (temp dir fixture), `RelayDriver` + `RelayDriverDependencies.ForTests(runner, tests, sink)`.

## Global Constraints

- Target framework: `net10.0`. Never downgrade.
- No polling in tests — `ScriptedTestRunner` is queue-based and deterministic; no `Task.Delay`, no `WaitUntilAsync`.
- Non-UI tests use plain `[Fact]` / `[Theory]` — no `[AvaloniaFact]` and no `[Collection("Headless")]` for driver/runner tests.
- Build command: `dotnet build tests/VisualRelay.Tests/VisualRelay.Tests.csproj`
- Test command (full suite): `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj`
- Test command (single class): `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build --filter "FullyQualifiedName~<ClassName>"`
- `ScriptedTestRunner` call ordering is critical: slot 0 = stage-5 author gate (always red in these tests); slots 1–2 = stage-9 first-run + retry (both fail to trigger the loop); subsequent slots = per-attempt pairs for the fix-verify loop. Add a comment next to every seeded `TestRunResult` explaining which call it covers.
- `RelayDriverDependencies.ForTests(runner, tests, sink)` injects a real `GitInvoker` — tests must use `baselineVerify: false` in config unless they set up a real git repo via `TestGit`. Prefer `baselineVerify: false`.
- `WorkingTreeHash(string rootPath, IReadOnlyList<string> manifest)` (`RelayDriver.Artifacts.cs:170`, `private static`) hashes ONLY the *contents of the manifest files* (SHA-256 over `relative` + file text, missing file = empty). It does NOT see writes to non-manifest files. To simulate "tree unchanged," do NOT change any manifest file in `repo.Root` between loop attempts (the `ScriptedSubagentRunner` default writes nothing to the filesystem, so its manifest files don't exist → identical hash each attempt). To simulate "tree changed," have the subagent write a manifest file to `repo.Root` between attempts (see Task 2 for the `WriteOnAttemptSubagentRunner` pattern).
- **File-size guard is active at 300 lines** (`tools/guards/check-file-size.sh`, `${VISUAL_RELAY_FILE_LINE_LIMIT:-300}`). `RelayDriver.VerifyFix.cs` is ALREADY 299 lines — ANY non-trivial addition overflows it. Therefore **all new production logic in Tasks 1, 2, and 8 goes into NEW partial files**, never inline into `VerifyFix.cs`: `RelayDriver.VerifyObservability.cs` (Task 1 `verify_result` emit + per-attempt artifact), `RelayDriver.ConvergenceGuard.cs` (Task 2 guard helper), `RelayDriver.VerifyWorktree.cs` (Task 8). `VerifyFix.cs` itself receives only minimal call-site edits (changing one argument, calling one new helper) and any such edit must keep the file ≤ 300 lines. After each task, run `bash tools/guards/check-file-size.sh` (or `dotnet build`, which runs the guard) and split further if needed.
- Test-double APIs are FIXED — use exactly these (verified in `tests/VisualRelay.Tests/`): `TestRepository.Create()` / `.Root` / `.WriteConfig(string testCommand, string[] logSources, bool baselineVerify=true, int maxVerifyLoops=0, bool archiveOnDone=true, string? formatCmd=null)` (NO `testFileCmd`/`retryFlakyVerify` params — for those, write raw `.relay/config.json`); `.WriteTask(id, markdown)`. `ScriptedTestRunner(params TestRunResult[])` is FIFO, default green, **ignores `rootPath`**. `RecordingTestRunner(params TestRunResult[])` exposes `.Calls` (List<(string RootPath, string Command)>). `ScriptedSubagentRunner.SeedHappyPath(code, test)` (writes nothing to disk). `CapturingSubagentRunner.Invocations` (IReadOnlyList<StageInvocation>) + `.SeedHappyPath(...)`. `InMemoryRelayEventSink.Events` (List<RelayEvent>). `TestGit` exposes ONLY a synchronous `TestGit.Run(string rootPath, params string[] arguments)` — there is NO `InitAsync`/`AddAllAsync`/`CommitAsync`; init a repo via `TestGit.Run(root, "init")`, `TestGit.Run(root, "config", "user.email", "...")`, `TestGit.Run(root, "config", "user.name", "...")`, `TestGit.Run(root, "add", ".")`, `TestGit.Run(root, "commit", "-m", "...")`. `TestFileSystem.DeleteDirectoryResilient(path)` exists but is TEST-ONLY — never reference it from production code.
- Event assertions use C# record patterns: `e is { EventName: "verify_result", StageNumber: 10 }`.
- `RelayEvent.Data` is `IReadOnlyDictionary<string, string>?` — access with `e.Data?["key"]`. The full `RelayEvent` ctor is `(DateTimeOffset Timestamp, string Level, string EventName, string RunId, string RootPath, string? TaskId=null, int? StageNumber=null, string? Tier=null, int? Attempt=null, IReadOnlyDictionary<string,string>? Data=null)`.

---

## Task 1: Emit `verify_result` structured event + persist full output artifact at every authoritative verify (observability prerequisite, R5)

**Files:**
- Create: `src/VisualRelay.Core/Execution/RelayDriver.VerifyObservability.cs` — new partial holding `PublishVerifyResultAsync(...)` and `TryPersistVerifyOutput(...)` (keeps `VerifyFix.cs` under the 300-line guard).
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs` — one call to `PublishVerifyResultAsync(...)` after the stage-10 `RunTestCommandWithRetryAsync` (~line 155).
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.cs` — one call to `PublishVerifyResultAsync(...)` after the stage-9 `RunTestCommandWithRetryAsync` (line 193), so the FIRST authoritative red is observable too (not only the loop).
- Test: `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`

**Interfaces:**
- Consumes: `IRelayEventSink.PublishAsync(RelayEvent, CancellationToken)` (existing); `_dependencies.EventSink` (existing field on `RelayDriver`).
- Consumes: `SwivalSubagentRunner.ExtractFailureReason(string output, int tailChars = 600)` — same namespace `VisualRelay.Core.Execution`, no new `using` needed. (Task 4 extends this method; Task 1 just calls it.)
- Mirrors: `TryPersistKilledOutput(string traceDirParent, int stageNum, int attempt, string reason, string output)` (`ProcessRunners.Helpers.cs:249-267`) — the precedent for writing a `stageN-attemptM.*.txt` artifact with a header then returning the path (or null on exception).
- Produces: New `verify_result` events with `Data` keys: `"command"` (the test command string = `config.TestCommand`), `"exitCode"` (int as string), `"check"` (`"green"` / `"red"`), `"reason"` (distilled via `ExtractFailureReason` on nonzero output; empty on green), `"treeHash"` (the `WorkingTreeHash` over the manifest — a coarse fingerprint; see caveat), and `"outputFile"` (the absolute path to the persisted full-output artifact, or empty if persistence failed). The FULL untrimmed output is written to the artifact file ONLY — it is never inlined into the event (inlining is what bloated `run.log` and buried the real failure per R5). Later tasks assert on these events.

- [ ] **Step 1: Write the failing test**

In `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`, add inside the `RelayDriverVerifyFixTests` class:

```csharp
[Fact]
public async Task RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage9AndStage10_WithOutputFilePointer()
{
    using var repo = TestRepository.Create();
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
    repo.WriteTask("verify-event", "# Verify event test\n");
    var runner = new ScriptedSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
        new TestRunResult(0, "green"),             // fix-verify attempt 1 gate — green
        new TestRunResult(0, "green"));            // pad (not reached)
    var sink = new InMemoryRelayEventSink();
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, sink),
        RelayDriverOptions.NoGitCommit);

    await driver.RunTaskAsync(repo.Root, "verify-event");

    // (1) The first authoritative red (stage 9) is observable.
    var stage9 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 9 });
    Assert.NotNull(stage9);
    Assert.Equal("dotnet test", stage9!.Data?["command"]);
    Assert.Equal("1", stage9.Data?["exitCode"]);
    Assert.Equal("red", stage9.Data?["check"]);
    // Distilled reason carries the real failing line, not the full blob.
    Assert.Contains("Failed TestX", stage9.Data?["reason"] ?? "", StringComparison.Ordinal);

    // (2) The stage-10 gate verdict is observable.
    var stage10 = sink.Events.SingleOrDefault(e => e is { EventName: "verify_result", StageNumber: 10 });
    Assert.NotNull(stage10);
    Assert.Equal("dotnet test", stage10!.Data?["command"]);
    Assert.Equal("0", stage10.Data?["exitCode"]);
    Assert.Equal("green", stage10.Data?["check"]);

    // (3) Every verify_result carries a treeHash and an outputFile POINTER (path),
    //     and the full output is in the file — never inlined in the event.
    Assert.True(stage9.Data!.ContainsKey("treeHash"));
    Assert.True(stage9.Data.ContainsKey("outputFile"));
    var outputFile = stage9.Data["outputFile"];
    Assert.False(string.IsNullOrEmpty(outputFile));
    Assert.True(File.Exists(outputFile));
    var persisted = await File.ReadAllTextAsync(outputFile);
    Assert.Contains("Failed TestX", persisted, StringComparison.Ordinal);
    // The event itself must NOT contain the full output blob under any key.
    Assert.DoesNotContain(stage9.Data.Values, v => v.Contains("Failed TestX") && v.Length > 200);
}
```

Note on the artifact filename: persist to `Path.Combine(taskDirectory, $"stage{stage.Number}-attempt{attempt}.verify-output.txt")`. For the **stage-9** call (which is not inside the attempt loop), use `attempt = 1`. `taskDirectory` is `.relay/{taskId}` and is in scope at both call sites; `attempt` for stage 10 is the loop variable. The header line mirrors `TryPersistKilledOutput` (a `# verify output …` comment with `capturedUtc` and `bytes`).

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage9AndStage10_WithOutputFilePointer"
```

Expected: FAIL — `verify_result` events are not yet emitted.

- [ ] **Step 3: Create `RelayDriver.VerifyObservability.cs` with the emit + persist helpers**

Do NOT add this inline to `VerifyFix.cs` (already 299 lines — would blow the 300-line guard). Create `src/VisualRelay.Core/Execution/RelayDriver.VerifyObservability.cs`:

```csharp
using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Persists the FULL untrimmed verify output to a per-attempt artifact and emits a
    /// structured <c>verify_result</c> event carrying the command, exit code, verdict,
    /// distilled reason, working-tree hash, and a POINTER to that artifact — never the
    /// full output inline. Mirrors <c>TryPersistKilledOutput</c>'s file convention so the
    /// autopsy trail is uniform. Called at BOTH authoritative gate runs (stage 9 and the
    /// stage-10 loop) so every red is observable after the fact (R5).
    /// </summary>
    private async Task PublishVerifyResultAsync(
        string rootPath, string runId, string taskId, string taskDirectory,
        RelayStageDefinition stage, int attempt, RelayConfig config,
        TestRunResult testResult, IReadOnlyList<string> manifest,
        CancellationToken cancellationToken)
    {
        var check = testResult.ExitCode == 0 ? "green" : "red";
        var reason = testResult.ExitCode != 0
            ? SwivalSubagentRunner.ExtractFailureReason(testResult.Output)
            : string.Empty;
        // NOTE: WorkingTreeHash fingerprints only the manifest files' contents — a coarse
        // signal, acceptable for observability (and for the Task 2 convergence guard).
        var treeHash = WorkingTreeHash(rootPath, manifest);
        var outputFile = TryPersistVerifyOutput(taskDirectory, stage.Number, attempt, check, testResult.Output) ?? string.Empty;

        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "info", "verify_result", runId, rootPath, taskId,
            stage.Number, stage.Tier, Attempt: attempt,
            Data: new Dictionary<string, string>
            {
                ["command"] = config.TestCommand,
                ["exitCode"] = testResult.ExitCode.ToString(),
                ["check"] = check,
                ["reason"] = reason,
                ["treeHash"] = treeHash,
                ["outputFile"] = outputFile
            }), cancellationToken);
    }

    /// <summary>
    /// Writes the verify run's full output to
    /// <c>stage{N}-attempt{M}.verify-output.txt</c> under the task directory, returning
    /// the path (or null on failure). Mirrors <c>TryPersistKilledOutput</c>.
    /// </summary>
    private static string? TryPersistVerifyOutput(
        string taskDirectory, int stageNum, int attempt, string check, string output)
    {
        try
        {
            var path = Path.Combine(taskDirectory, $"stage{stageNum}-attempt{attempt}.verify-output.txt");
            var header =
                $"# verify output (autopsy artifact){Environment.NewLine}" +
                $"# check: {check}{Environment.NewLine}" +
                $"# capturedUtc: {DateTimeOffset.UtcNow:O}  bytes: {output.Length}{Environment.NewLine}{Environment.NewLine}";
            File.WriteAllText(path, header + output);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
```

Verify the real signatures before wiring: `WorkingTreeHash` is `RelayDriver.Artifacts.cs:170`; `TryPersistKilledOutput` (the precedent) is `ProcessRunners.Helpers.cs:249-267`; `ExtractFailureReason` is `ProcessRunners.Diagnostics.cs:72`.

- [ ] **Step 4: Wire `PublishVerifyResultAsync` into BOTH authoritative gate call sites**

(a) `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs` — immediately after the stage-10 `var testResult = await RunTestCommandWithRetryAsync(...)` (currently ~line 155, BEFORE the `testResult.TimedOut` guard so even a green attempt is recorded; emit after the timeout guard if you prefer not to record timeouts — pick one and keep it consistent). Pass the loop's `attempt` variable:

```csharp
await PublishVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage, attempt, config, testResult, manifest, cancellationToken);
```

(b) `src/VisualRelay.Core/Execution/RelayDriver.cs` — immediately after the stage-9 `var testResult = await RunTestCommandWithRetryAsync(rootPath, config, cancellationToken, 9, runId, taskId);` (line 193). Stage 9 is not in the attempt loop, so pass `attempt: 1` and `stage` = `RelayStages.All[8]` (the Verify stage; confirm the in-scope stage variable's `.Number == 9` before using it, otherwise reference `RelayStages.All[8]`):

```csharp
await PublishVerifyResultAsync(rootPath, runId, taskId, taskDirectory, stage, attempt: 1, config, testResult, manifest, cancellationToken);
```

After Task 8 lands, the `testResult`/`rootPath` passed here will be the isolated-tree result — that is intentional (the event reports exactly what the gate judged); see Task 8 Defect-F note.

- [ ] **Step 5: Run the test to verify it passes, then confirm the size guard**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage9AndStage10_WithOutputFilePointer"
bash tools/guards/check-file-size.sh   # VerifyFix.cs must stay <= 300 lines
```

Expected: PASS; guard clean.

- [ ] **Step 6: Run the full test suite to confirm no regressions**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All prior tests pass plus the new test.

> **Scope decision (recorded, not an omission):** the spec's Approach-0 sub-bullet "capture stdout and stderr separately / tag each line by stream" is deliberately NOT implemented. `ProcessCapture` merges the streams today, and we close the noise problem via regex distillation (`ExtractFailureReason`, extended in Task 4) instead — the spec explicitly sanctions distillation as the alternative ("a cleaner fix … than after-the-fact regex stripping" describes the road not taken; the chosen road is the distiller). Stream-separation would be a larger `ProcessCapture`/`IProcessCapture` change with no additional acceptance criterion riding on it, so it is out of scope for this plan.

- [ ] **Step 7: Commit**

```bash
git add src/VisualRelay.Core/Execution/RelayDriver.VerifyObservability.cs \
        src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs \
        src/VisualRelay.Core/Execution/RelayDriver.cs \
        tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs
git commit -m "feat(verify): emit verify_result event + persist full-output artifact at stage 9 and stage 10 (R5 observability)"
```

---

## Task 2: Add convergence guard — bail on attempt 2 when tree unchanged and same failure (R3)

**Files:**
- Create: `src/VisualRelay.Core/Execution/RelayDriver.ConvergenceGuard.cs` — new partial holding the small `IsNonConvergent(...)` helper + the per-attempt tracking record (keeps `VerifyFix.cs` under the 300-line guard).
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs` — a few lines inside the loop: track the prior attempt's `(treeHash, distilledReason)`, and AFTER the existing recording block flag-and-return when the guard fires. Reuses the `treeHash` already computed at line 175 — does NOT duplicate the ledger/seal/status recording.
- **Update existing tests:** `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs` — the existing `RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops` (L42-71) and the new convergence tests (see below).

**Why this placement (Defect-I correctness):** The loop already records the attempt's ledger/seal/status and publishes `stage_done` at lines 166-188 of `VerifyFix.cs`. The convergence check must run AFTER that recording (so the failing attempt is honestly sealed) and only when the attempt did not go green (the early green return is at line 190-191). Placing the guard right before the `failingTestOutput = BuildFailureOutput(...)` line (194) lets it reuse the already-computed `treeHash` (line 175) and the already-incremented `previousSeal`/`taskHash`/`sessionCostUsd`/`unknownCostStageCount` — so it just calls `FlagAsync` and returns, with NO duplicated recording logic.

**Interfaces (verified against `VerifyFix.cs`):**
- Consumes: `treeHash` (the `WorkingTreeHash(rootPath, manifest)` value computed at line 175). `WorkingTreeHash` hashes only manifest-file CONTENTS (`RelayDriver.Artifacts.cs:170`) — a coarse fingerprint; an agent edit outside the manifest won't move it, so the guard can occasionally be conservative (bail when the agent did change a non-manifest file). That is acceptable: the alternative (burning 5 attempts) is the bug we're removing, and Task 8's full-tree isolation makes the gate verify the right code regardless. Add a code comment stating this limitation.
- Consumes: `SwivalSubagentRunner.ExtractFailureReason(string output)` (`ProcessRunners.Diagnostics.cs:72`) — distilled failure string; same value the agent is shown after Task 4.
- Consumes: `FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, reason, details, statusEntries, cancellationToken)` — already the method the loop calls at lines 199-200.
- In-scope identifiers at the guard site (confirmed): `attempt`, `check`, `treeHash`, `testResult`, `previousSeal`, `taskHash`, `sessionCostUsd`, `unknownCostStageCount`, `manifest`, `rootPath`, `runId`, `taskId`, `taskDirectory`, `stage`, `statusEntries`, `cancellationToken`.
- Produces: On attempt ≥ 2 with `check == "red"`, `treeHash == prior.TreeHash`, and `distilledReason == prior.DistilledReason`, returns a Flagged outcome whose reason is `$"verify non-convergent: working tree unchanged, same failure persists ({distilledReason})"`. The flag is recorded via `FlagAsync` (which emits the `flagged` event with `Data["reason"]`).

- [ ] **Step 1: Write the failing test (no-change → non-convergent bail)**

Add to `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`:

```csharp
[Fact]
public async Task RunVerifyFixLoop_NonConvergent_BailsOnAttempt2()
{
    // Verify red + agent makes no tree changes = non-convergence bail on attempt 2,
    // not after burning all maxVerifyLoops attempts.
    using var repo = TestRepository.Create();
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 5);
    repo.WriteTask("non-convergent", "# Non-convergent\n");
    var runner = new ScriptedSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    // ScriptedSubagentRunner writes no files — manifest files never exist, so
    // WorkingTreeHash is identical every attempt (tree "unchanged").
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),                // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),        // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"),        // stage 9 verify — retry also fails
        new TestRunResult(1, "Failed TestX"),        // fix-verify attempt 1 gate — red
        new TestRunResult(1, "Failed TestX"),        // fix-verify attempt 1 retry — red
        new TestRunResult(1, "Failed TestX"),        // fix-verify attempt 2 gate — red (bail here)
        new TestRunResult(1, "Failed TestX"),        // fix-verify attempt 2 retry — red
        new TestRunResult(1, "Failed TestX"),        // attempt 3+ — should NOT be reached
        new TestRunResult(1, "Failed TestX"));       // pad — should NOT be reached
    var sink = new InMemoryRelayEventSink();
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, sink),
        RelayDriverOptions.NoGitCommit);

    var outcome = await driver.RunTaskAsync(repo.Root, "non-convergent");

    // Must flag — non-convergence bails.
    Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
    // Must bail on attempt 2 with the convergence reason, not the max-loops reason.
    Assert.NotNull(outcome.Reason);
    Assert.Contains("non-convergent", outcome.Reason!, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("after 5 fix-verify", outcome.Reason!, StringComparison.OrdinalIgnoreCase);
    // Exactly 2 fix-verify stage_start events (attempt 1 + attempt 2).
    var fixVerifyStarts = sink.Events.Where(e => e is { EventName: "stage_start", StageNumber: 10 }).ToList();
    Assert.Equal(2, fixVerifyStarts.Count);
    // The flagged event must carry the non-convergence reason.
    var flagged = sink.Events.Single(e => e.EventName == "flagged");
    Assert.Contains("non-convergent", flagged.Data?["reason"] ?? "", StringComparison.OrdinalIgnoreCase);
}
```

> Note the seeded results now include BOTH the gate run AND the retry per attempt (the `RetryFlakyVerify` default is `true`, and `WriteConfig` does not change it). Each fix-verify attempt consumes two `TestRunResult`s when both are nonzero. Comment every slot.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_NonConvergent_BailsOnAttempt2"
```

Expected: FAIL — currently burns all 5 attempts and flags `verify failed after 5 fix-verify attempts`.

- [ ] **Step 3: Add the convergence guard (new partial + minimal `VerifyFix.cs` edit)**

Create `src/VisualRelay.Core/Execution/RelayDriver.ConvergenceGuard.cs`:

```csharp
namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>One fix-verify attempt's convergence fingerprint.</summary>
    private readonly record struct VerifyAttemptFingerprint(string TreeHash, string DistilledReason);

    /// <summary>
    /// True when this red attempt is provably non-convergent: the working-tree
    /// fingerprint and the distilled failure are BOTH identical to the prior
    /// attempt, so looping cannot improve the verdict. LIMITATION: the tree hash
    /// (<see cref="WorkingTreeHash"/>) covers only the MANIFEST files' contents, so
    /// an agent edit OUTSIDE the manifest is invisible here — the guard may then
    /// bail conservatively. Accepted: the alternative is burning every attempt, and
    /// Task 8's full-tree isolation makes the authoritative gate verify the right
    /// code regardless.
    /// </summary>
    private static bool IsNonConvergent(int attempt, string check, VerifyAttemptFingerprint current, VerifyAttemptFingerprint? prior) =>
        attempt >= 2 && check == "red" && prior is { } p
        && current.TreeHash == p.TreeHash
        && current.DistilledReason == p.DistilledReason;
}
```

In `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`, declare the tracker before the `for` loop (just after line 80 `var maxLoops = config.MaxVerifyLoops;`):

```csharp
VerifyAttemptFingerprint? previousAttempt = null;
```

Then, inside the loop, AFTER the existing recording/`PublishStageDoneAsync` block and the early `if (check == "green") return ...` (i.e. immediately before the existing `failingTestOutput = BuildFailureOutput(...)` line ~194), insert:

```csharp
// Convergence guard (R3): if this red attempt left the manifest tree unchanged
// AND the distilled failure is identical to the prior attempt, the loop cannot
// converge — flag now instead of burning the remaining attempts. The attempt is
// already recorded above (honest history), so we only flag and return here.
var distilledReason = SwivalSubagentRunner.ExtractFailureReason(testResult.Output);
var thisAttempt = new VerifyAttemptFingerprint(treeHash, distilledReason);
if (IsNonConvergent(attempt, check, thisAttempt, previousAttempt))
{
    var reason = $"verify non-convergent: working tree unchanged, same failure persists ({distilledReason})";
    var ncOutcome = await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
        reason, testResult.Output, statusEntries, cancellationToken);
    return (ncOutcome, previousSeal, taskHash, sessionCostUsd, unknownCostStageCount);
}
previousAttempt = thisAttempt;
```

This reuses `treeHash` from line 175 (no second filesystem read) and adds NO duplicate recording. Confirm `treeHash`, `previousSeal`, `taskHash`, `sessionCostUsd`, `unknownCostStageCount` are the real in-scope names by re-reading `VerifyFix.cs:155-201` before editing.

- [ ] **Step 4: Run the convergence test + size guard**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_NonConvergent_BailsOnAttempt2"
bash tools/guards/check-file-size.sh
```

Expected: PASS; `VerifyFix.cs` still ≤ 300 lines (the inserted block is ~9 lines; if it overflows, move `BuildFailureOutput`-adjacent helpers or this block into the new partial).

- [ ] **Step 5: Write a complementary test — convergent loop (tree DID change) must NOT bail early**

Add to `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`:

```csharp
[Fact]
public async Task RunVerifyFixLoop_TreeChangedBetweenAttempts_DoesNotBailEarly()
{
    // When the agent actually changes a manifest file, the convergence guard must
    // NOT fire — the loop continues until green (or maxLoops).
    using var repo = TestRepository.Create();
    var srcPath = Path.Combine(repo.Root, "src");
    Directory.CreateDirectory(srcPath);
    File.WriteAllText(Path.Combine(srcPath, "app.cs"), "// v0");
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 3);
    repo.WriteTask("convergent", "# Convergent\n");

    // Writes a manifest file on the first stage-10 invocation so the tree hash
    // changes between attempt 1 and attempt 2.
    var writeOnce = new WriteOnAttemptSubagentRunner(repo.Root, "src/app.cs", "// v1");
    writeOnce.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
        new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red (tree will change)
        new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — red
        new TestRunResult(0, "green"));            // fix-verify attempt 2 gate — green
    var sink = new InMemoryRelayEventSink();
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(writeOnce, tests, sink),
        RelayDriverOptions.NoGitCommit);

    var outcome = await driver.RunTaskAsync(repo.Root, "convergent");

    Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    var fixVerifyStarts = sink.Events.Where(e => e is { EventName: "stage_start", StageNumber: 10 }).ToList();
    Assert.Equal(2, fixVerifyStarts.Count);
    Assert.DoesNotContain(sink.Events, e => e.EventName == "flagged");
}
```

Add the helper runner at the bottom of `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs` (or in a split file `RelayDriverVerifyFixTests.ConvergenceGuard.cs`):

```csharp
/// <summary>
/// Wraps <see cref="ScriptedSubagentRunner"/> and writes a manifest file to
/// rootPath exactly once (on the first stage-10 invocation) so the working-tree
/// hash changes between fix-verify attempt 1 and attempt 2.
/// </summary>
internal sealed class WriteOnAttemptSubagentRunner(string rootPath, string relativePath, string content) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private bool _written;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 10 && !_written)
        {
            _written = true;
            var fullPath = Path.Combine(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
```

- [ ] **Step 6: Update the existing max-loops test so it stays a genuine max-loops scenario (Defect A)**

The convergence guard changes what `RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops` (L42-71) exercises. As written, its `ScriptedSubagentRunner` writes nothing → the tree is unchanged across attempts → under the guard it now bails on attempt 2 with `verify non-convergent` instead of looping to `maxLoops` and flagging `verify failed after 2 fix-verify attempts`. That genuine "loops to maxLoops then flags" path is exactly the new `RunVerifyFixLoop_TreeChangedBetweenAttempts_DoesNotBailEarly` shape (the agent CHANGES the tree each attempt) — except verify stays red.

Rewrite `RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops` so the subagent changes a manifest file EVERY attempt (so the guard never fires) while verify stays red, proving the max-loops bound still works:

```csharp
[Fact]
public async Task RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops()
{
    using var repo = TestRepository.Create();
    var srcPath = Path.Combine(repo.Root, "src");
    Directory.CreateDirectory(srcPath);
    File.WriteAllText(Path.Combine(srcPath, "app.cs"), "// v0");
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
    repo.WriteTask("unfixable", "# Unfixable\n");
    // Changes a manifest file on EVERY stage-10 invocation so the convergence
    // guard never fires — this is the genuine "agent keeps trying, verify stays
    // red, loop exhausts maxLoops" path.
    var runner = new WriteEachAttemptSubagentRunner(repo.Root, "src/app.cs");
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
        new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red
        new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 retry — red
        new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 2 gate — red
        new TestRunResult(1, "Failed TestX"));     // fix-verify attempt 2 retry — red
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
        RelayDriverOptions.NoGitCommit);

    var outcome = await driver.RunTaskAsync(repo.Root, "unfixable");

    Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
    Assert.Contains("verify failed after 2 fix-verify attempts", outcome.Reason, StringComparison.Ordinal);
    var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "unfixable", "NEEDS-REVIEW"));
    Assert.Contains("verify failed after 2 fix-verify attempts", review, StringComparison.Ordinal);
    var seals = await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".relay", "unfixable", "unfixable.seals"));
    Assert.Contains(seals, line => line.Contains("\"n\":9", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
    Assert.Contains(seals, line => line.Contains("\"n\":10", StringComparison.Ordinal) && line.Contains("\"check\":\"red\"", StringComparison.Ordinal));
}
```

Add the every-attempt writer beside `WriteOnAttemptSubagentRunner`:

```csharp
/// <summary>
/// Writes DISTINCT content to a manifest file on EVERY stage-10 invocation so the
/// working-tree hash changes each attempt — the convergence guard never fires and
/// the loop runs to maxLoops.
/// </summary>
internal sealed class WriteEachAttemptSubagentRunner(string rootPath, string relativePath) : ISubagentRunner
{
    private readonly ScriptedSubagentRunner _inner = new();
    private int _n;

    public void SeedHappyPath(string codeFile, string testFile) =>
        _inner.SeedHappyPath(codeFile, testFile);

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 10)
        {
            var fullPath = Path.Combine(rootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, $"// attempt {++_n}", cancellationToken);
        }
        return await _inner.RunAsync(invocation, cancellationToken);
    }
}
```

> Also CONFIRM `RunTaskAsync_MaxVerifyLoopsRespected_ExactAttemptCount` (L73-101) still passes UNCHANGED: its attempt 2 retry flips green, so `check == "green"` on attempt 2 → the guard (which only fires on `check == "red"`) never triggers, and it commits as before. Likewise `RunTaskAsync_FixableVerifyFailure_CommitsAfterFixVerifyLoop` (attempt 1 retry goes green) is unaffected. Run them to verify.

- [ ] **Step 7: Run all fix-verify tests, then the full suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RelayDriverVerifyFixTests"
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All pass (new convergence tests + the rewritten max-loops test + the unchanged survivors).

- [ ] **Step 8: Commit**

```bash
git add src/VisualRelay.Core/Execution/RelayDriver.ConvergenceGuard.cs \
        src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs \
        tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs
git commit -m "feat(verify): convergence guard — bail on attempt 2 when manifest tree unchanged and same failure"
```

---

## Task 3: Escalate Fix-verify agent to full gate command (close R0a)

**Files:**
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs:94` — change `testCommand: targetedTestCommand` → `testCommand: config.TestCommand`
- Modify: `src/VisualRelay.Core/Execution/RelayStages.cs:78-86` — update the Fix-verify system prompt
- Test: `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs` (new test asserting stage-10 `TestCommand`)
- **Update existing test:** `tests/VisualRelay.Tests/TargetedTestCommandTests.cs:213-247` — `RunTaskAsync_FixVerifyLoop_Stage10ReceivesTargetedTestCommandInPrompt` currently asserts stage 10 gets the TARGETED command (`"bun test config.json"`, L246). This is the assertion the whole task inverts: it must now assert the FULL command (`"dotnet test"`). Stages 6/8 stay targeted (`RunTaskAsync_StagesImplementAndFix_ReceiveTargetedTestCommand` at L98-136 is unchanged).
- Test: `tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs` (add new prompt assertions; the existing Fix-verify assertions — `"do NOT run"`, `"## Verify command"`, `"harness"`, `"diff-scoped"` — all stay green under the new prompt)

**Interfaces:**
- Consumes: `BuildInvocation(..., testCommand: ...)` — `testCommand` is the `TestCommand` property of `StageInvocation`; already passed through `BuildPrompt` to emit `## Verify command`.
- Consumes: `BuildTargetedTestCommand(config, manifest)` — for stages 6 and 8 only (unchanged); stage 10 now uses `config.TestCommand` directly.
- Produces: `StageInvocation.TestCommand == config.TestCommand` for stage-10 invocations; unchanged for stages 6 and 8.

- [ ] **Step 1: Write the failing test asserting stage-10 uses the full gate command**

Add to `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`:

```csharp
[Fact]
public async Task RunVerifyFixLoop_Stage10Invocation_UsesFullGateCommand_NotTargetedSubset()
{
    // Config has a testFileCmd with {files} so targetedTestCommand differs from
    // config.TestCommand. Stage 10 must use the full gate (config.TestCommand).
    using var repo = TestRepository.Create();
    Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
    await File.WriteAllTextAsync(
        Path.Combine(repo.Root, ".relay", "config.json"),
        """
        {
          "testCmd": "dotnet test --project VisualRelay.sln",
          "testFileCmd": "dotnet test --filter {files}",
          "logSources": [],
          "baselineVerify": false,
          "maxVerifyLoops": 1
        }
        """);
    repo.WriteTask("full-gate", "# Full gate\n");
    var runner = new CapturingSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
        new TestRunResult(0, "green"));            // fix-verify attempt 1 gate — green
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
        RelayDriverOptions.NoGitCommit);

    var outcome = await driver.RunTaskAsync(repo.Root, "full-gate");

    Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    var stage10Invocation = runner.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
    Assert.NotNull(stage10Invocation);
    // Stage 10 must use the FULL gate, not the targeted subset.
    Assert.Equal("dotnet test --project VisualRelay.sln", stage10Invocation!.TestCommand);
    // Stages 6 and 8 must still use the targeted subset.
    var stage6 = runner.Invocations.SingleOrDefault(i => i.Stage.Number == 6);
    Assert.NotNull(stage6);
    Assert.Contains("{files}", stage6!.TestCommand ?? "", StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_Stage10Invocation_UsesFullGateCommand_NotTargetedSubset"
```

Expected: FAIL — currently passes `targetedTestCommand` to stage 10.

- [ ] **Step 3: Change `BuildInvocation` call in `RunVerifyFixLoopAsync` to use `config.TestCommand`**

In `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`, at line 94 (the `BuildInvocation` call), change:

```csharp
// OLD:
testCommand: targetedTestCommand,
// NEW:
testCommand: config.TestCommand,
```

The `targetedTestCommand` parameter is still accepted by `RunVerifyFixLoopAsync` (it is passed in from stage 9 for use in earlier iterations if needed, and it stays on the method signature — no callers need updating). Only the `BuildInvocation` call changes.

- [ ] **Step 4: Run the failing test**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_Stage10Invocation_UsesFullGateCommand_NotTargetedSubset"
```

Expected: PASS

- [ ] **Step 5: Update the Fix-verify system prompt in `RelayStages.cs`**

In `src/VisualRelay.Core/Execution/RelayStages.cs`, in the `SystemPromptFor` method, replace the `"Fix-verify"` case:

```csharp
// OLD:
"Fix-verify" =>
    "Fix failures from the pinned suite. Verify by running ONLY the command shown " +
    "in the ## Verify command section of the prompt — run exactly that one command " +
    "and nothing else — and confirm it passes (exit 0) before returning success. " +
    "Do NOT run the project's full check, lint, format, build, or screenshot gate " +
    "(e.g. `./visual-relay check`), and do NOT broaden the command to a fuller " +
    "gate — the harness runs the full gate at its own stage. " +
    "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
    "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",

// NEW:
"Fix-verify" =>
    "Fix all failures from the full test suite gate shown in ## Verify command. " +
    "The command in ## Verify command IS the full gate — run exactly that command " +
    "and confirm it exits 0 before returning success. " +
    "Treat a nonzero exit as a real, unfinished failure even when the summary " +
    "says '0 failed': inspect the output tail for a non-test gate (perf/wall-clock " +
    "ceiling, lint/coverage ratchet, a throwing setup/teardown hook) and resolve it " +
    "legitimately — do NOT delete tests, weaken assertions, or skip hooks to beat " +
    "the gate. If a non-test gate is not safely fixable within this task's scope, " +
    "report it explicitly as a non-test gate failure instead of hacking around it. " +
    "Do NOT run the project's broader orchestration gate (e.g. `./visual-relay check`). " +
    "The harness runs the full gate mechanically; your job is to make it pass cleanly. " +
    "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
    "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",
```

- [ ] **Step 6: Update `CodingStageSystemPromptTests.cs` to cover the new Fix-verify prompt**

The existing tests in `CodingStageSystemPromptTests.cs` check that:
- Fix-verify prompt contains `"do NOT run"` — still true in the new prompt.
- Fix-verify prompt references `"## Verify command"` — still true.
- Fix-verify prompt contains `"harness"` — still true.
- Fix-verify prompt contains `"diff-scoped"` — still true.

Add two new tests to check the new behavior:

```csharp
[Fact]
public void FixVerify_SystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure()
{
    var stage = RelayStages.All.Single(s => s.Name == "Fix-verify");
    // Must tell the agent that a nonzero exit = real failure even when summary says 0 failed.
    Assert.Contains("nonzero exit", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("0 failed", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void FixVerify_SystemPrompt_ForbidsRewardHacking_AndHasNonTestGateFallback()
{
    var stage = RelayStages.All.Single(s => s.Name == "Fix-verify");
    // Must instruct the agent NOT to delete tests or weaken assertions to beat the gate.
    Assert.Contains("do NOT delete tests", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    // Must carry the "report as a non-test gate if not safely fixable" fallback.
    Assert.Contains("non-test gate", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 7: Invert the existing stage-10 targeted-command assertion (Defect E)**

In `tests/VisualRelay.Tests/TargetedTestCommandTests.cs`, the existing `RunTaskAsync_FixVerifyLoop_Stage10ReceivesTargetedTestCommandInPrompt` (L213-247) asserts stage 10 receives the TARGETED command. After this task, stage 10 receives the FULL gate. Rename it and invert the assertion (keep the same config + scripted results — `testCmd: "dotnet test"`, `testFileCmd: "bun test {files}"`, manifest `src/app.cs` + `config.json`):

```csharp
// OLD (L245-246):
var stage10 = runner.Invocations.Single(i => i.Stage.Number == 10);
Assert.Equal("bun test config.json", stage10.TestCommand);

// NEW (rename the method to ..._Stage10ReceivesFullGateCommandInPrompt):
var stage10 = runner.Invocations.Single(i => i.Stage.Number == 10);
Assert.Equal("dotnet test", stage10.TestCommand);  // full gate, not the targeted subset
```

Leave `RunTaskAsync_StagesImplementAndFix_ReceiveTargetedTestCommand` (L98-136, asserts stages 6 & 8 == `"bun test config.json"`) UNCHANGED — those stages keep the fast targeted command.

- [ ] **Step 8: Run all affected tests**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~CodingStageSystemPromptTests|FullyQualifiedName~TargetedTestInvocationTests|FullyQualifiedName~RelayDriverVerifyFixTests"
```

Expected: All pass (including the inverted stage-10 assertion and the unchanged stage-6/8 assertion).

- [ ] **Step 9: Run the full suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All prior tests pass plus new tests.

- [ ] **Step 10: Commit**

```bash
git add src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs \
        src/VisualRelay.Core/Execution/RelayStages.cs \
        tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs \
        tests/VisualRelay.Tests/TargetedTestCommandTests.cs \
        tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs
git commit -m "feat(verify): escalate Fix-verify agent to full gate command; update stage-10 prompt + tests"
```

---

## Task 4: Extend the distiller + feed it the verify failure shown to the fix agent (close R1)

`ExtractFailureReason` as it exists TODAY (`ProcessRunners.Diagnostics.cs:72`) only strips lines containing BOTH `is blocked by '` AND `use --bypass-protection`, plus pure banner rows. It does NOT strip `Verified N pack(s)` lines, does NOT strip *bare* `deny_*` advisory lines, and does NOT treat test-failure lines (`Failed …`) as a failure anchor. So reusing it as-is would leave `Verified 1 pack(s)` in the agent's view and would fail to anchor on the real failing test — NOT meeting the spec acceptance (`deny_*` / `Verified N pack(s)` gone; the real reason present). This task EXTENDS the distiller, then feeds it into `BuildFailureOutput`.

**Files:**
- Modify: `src/VisualRelay.Core/Execution/ProcessRunners.Diagnostics.cs` — extend `ExtractFailureReason`: (1) strip `Verified N pack(s)` lines, (2) strip *bare* nono advisory `deny_*` lines, (3) add a test-failure anchor tier.
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs:271-293` — `BuildFailureOutput` distills `testResult.Output` via the extended `ExtractFailureReason` before appending.
- Test (direct unit): `tests/VisualRelay.Tests/SwivalSubagentRunnerToolPreflightTests.cs` — the existing home of the `ExtractFailureReason_*` unit tests.
- Test (end-to-end): `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs` — assert the distilled output reaches the agent's `LastTestOutput`.

**Safety constraint (verified):** the ONLY other caller of `ExtractFailureReason` is the subagent-crash path (`ProcessRunners.RunAsync.cs:229`: `swival exit N: {ExtractFailureReason(result.Output)}`). The existing unit tests pin that behavior — `ExtractFailureReason_RealArtifactShape_SurfacesBinaryPathNotEnvrcNoise` (anchors `cannot find binary path`), `ExtractFailureReason_NoFailureSignal_FallsBackToLastNonEmptyLine` (exact `"swival completed with some final summary"`), `ExtractFailureReason_BenignErrorCountLineBeforeFatal_AnchorsOnTheFatalLine` (a benign "0 errors" line must NOT anchor), `ExtractFailureReason_AllAdvisoryNoise_FallsBackToNoDiagnosticPlaceholder` (returns `(no diagnostic output captured)`). The extension must keep ALL of these green. Design choices that guarantee that: the new test-failure anchors join the EXISTING **strong** tier (so strong precedence and the "benign 0-errors never anchors" rule are unchanged); the new strips target only `Verified N pack(s)` and *bare* `deny_*` advisories (the WARN lines those tests use already carry the full `is blocked by '` + `use --bypass-protection` phrase, so they were already stripped — the new bare-`deny_*` rule does not change them); and "N fail" is deliberately NOT a trigger (a benign `0 failed` / `0 fail` summary must not be mistaken for a failure — only `Failed …` at line start and the uppercase `\bFAIL\b` token are anchored).

**Interfaces:**
- Consumes/extends: `SwivalSubagentRunner.ExtractFailureReason(string output, int tailChars = 600)` — `internal static`, same namespace `VisualRelay.Core.Execution`.
- Consumes: `BuildFailureOutput(TestRunResult testResult, string? guardOutput, bool bootstrapFailed, string? bootstrapFailureOutput, string? newGuardOutput = null)` — `private static` in `RelayDriver.RepoGuards.cs:271`.
- Produces: `BuildFailureOutput` returns `ExtractFailureReason(testResult.Output)` instead of `testResult.Output` verbatim (test portion only — guard and bootstrap output unchanged).

- [ ] **Step 1: Write the failing DIRECT unit test on the extended distiller**

Add to `tests/VisualRelay.Tests/SwivalSubagentRunnerToolPreflightTests.cs`, beside the other `ExtractFailureReason_*` tests:

```csharp
[Fact]
public void ExtractFailureReason_VerifyOutput_StripsPackAndDenyNoise_KeepsFailedTest()
{
    // Spec acceptance: deny_* and "Verified N pack(s)" gone; the real "Failed X" present.
    var output = string.Join('\n', new[]
    {
        "deny_read_user_home",                                  // bare advisory (no bypass phrase)
        "'/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access",
        "Verified 1 pack(s)",
        "bun test v1.x",
        "Failed JobFinder > parses Ashby — expected 3 but got 0",
    });

    var reason = SwivalSubagentRunner.ExtractFailureReason(output);

    Assert.DoesNotContain("deny_read_user_home", reason, StringComparison.Ordinal);
    Assert.DoesNotContain("deny_credentials", reason, StringComparison.Ordinal);
    Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
    Assert.DoesNotContain("Verified 1 pack(s)", reason, StringComparison.Ordinal);
    // The real failing test must be retained AND lead the reason (anchored).
    Assert.Contains("Failed JobFinder > parses Ashby", reason, StringComparison.Ordinal);
    Assert.StartsWith("Failed JobFinder", reason, StringComparison.Ordinal);
}

[Fact]
public void ExtractFailureReason_BenignZeroFailedSummary_DoesNotAnchorOnIt()
{
    // A "0 failed" summary line is benign; it must NOT become the anchor. With no
    // real failure line present, the extractor falls back to the surviving tail.
    var output = string.Join('\n', new[]
    {
        "Verified 1 pack(s)",
        "Test Files  12 passed (12)",
        "Tests  340 passed | 0 failed",
        "wall-clock ceiling exceeded: 61s > 60s budget",
    });

    var reason = SwivalSubagentRunner.ExtractFailureReason(output);

    Assert.DoesNotContain("Verified 1 pack(s)", reason, StringComparison.Ordinal);
    // The non-test gate line (the real cause) survives as the tail.
    Assert.Contains("wall-clock ceiling exceeded", reason, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the direct unit test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~ExtractFailureReason_VerifyOutput_StripsPackAndDenyNoise_KeepsFailedTest"
```

Expected: FAIL — `Verified 1 pack(s)` and the bare `deny_*` line currently survive; the `Failed …` line is not anchored.

- [ ] **Step 3: Extend `ExtractFailureReason` in `ProcessRunners.Diagnostics.cs`**

Re-read the method first (currently lines 72-128). Make three surgical changes inside the existing structure:

(a) In the per-line filter loop (after the existing banner-row drop, ~line 89), drop the two new noise classes BEFORE the anchor checks:

```csharp
// Drop "Verified N pack(s)" — nono prints it every run regardless of outcome.
if (VerifiedPacksLine.IsMatch(line))
    continue;
// Drop a BARE nono advisory token line (e.g. "deny_read_user_home") that printed
// without the full "is blocked by … use --bypass-protection" phrase (already
// handled above). Match only a line that is ONLY such a token, so a real error
// that merely contains the substring is never dropped.
if (BareDenyAdvisoryLine.IsMatch(line))
    continue;
```

(b) Promote test-failure lines into the existing STRONG tier. Update `HasStrongFailureSignal`:

```csharp
private static bool HasStrongFailureSignal(string line) =>
    line.Contains("cannot find binary path", StringComparison.OrdinalIgnoreCase) ||
    line.Contains("command execution failed", StringComparison.OrdinalIgnoreCase) ||
    line.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
    // A real test failure is exactly what we want to surface. "Failed " at line
    // start matches this codebase's failing-test format (see ExtractFailureIds);
    // \bFAIL\b (uppercase) matches bun/jest "FAIL path/to/test". NOT "N fail" —
    // a benign "0 failed" summary must never anchor.
    line.StartsWith("Failed ", StringComparison.Ordinal) ||
    FailToken.IsMatch(line);
```

(c) Add the three regexes beside the existing `WeakFailureKeywords` (`RegexOptions.Compiled`):

```csharp
private static readonly Regex VerifiedPacksLine = new(
    @"^Verified\s+\d+\s+pack\(s\)\s*$", RegexOptions.Compiled);
// A line that is ONLY a bare nono advisory token like "deny_read_user_home".
private static readonly Regex BareDenyAdvisoryLine = new(
    @"^deny_[a-z0-9_]+\s*$", RegexOptions.Compiled);
// Uppercase FAIL as a whole word (bun/jest/vitest failure rows). Case-SENSITIVE
// so "failed" inside prose / "Command execution failed" is not matched here.
private static readonly Regex FailToken = new(
    @"\bFAIL\b", RegexOptions.Compiled);
```

> Re-confirm before editing: the filter loop, `HasStrongFailureSignal`, and the regex declarations are all in `ProcessRunners.Diagnostics.cs` (lines ~74-128). Keep the existing two-pass anchor logic (strong wins, weak fallback) intact — you are only widening the strong set and the noise filter.

- [ ] **Step 4: Run the direct unit tests + the existing distiller tests**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~ExtractFailureReason"
```

Expected: the two new tests PASS and ALL existing `ExtractFailureReason_*` tests STILL pass.

- [ ] **Step 5: Write the end-to-end test (distilled output reaches the agent)**

Add to `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`:

```csharp
[Fact]
public async Task RunVerifyFixLoop_FailureOutputShownToAgent_HasNonoNoiseStripped()
{
    // The agent's LastTestOutput must have nono advisory noise (blocked-by lines,
    // bare deny_*, "Verified N pack(s)") stripped — only the real failure survives.
    using var repo = TestRepository.Create();
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 2);
    repo.WriteTask("noise-strip", "# Noise strip test\n");
    var runner = new CapturingSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    const string nonoNoise =
        "deny_read_user_home\n" +
        "'/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access\n" +
        "Verified 1 pack(s)\n";
    const string realFailure = "Failed ImportantTest — expected 42 but got 0";
    var rawOutput = nonoNoise + realFailure;
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),                       // stage 5 author gate
        new TestRunResult(1, rawOutput),                   // stage 9 verify — first run fails with noise
        new TestRunResult(1, rawOutput),                   // stage 9 verify — retry also fails
        new TestRunResult(1, rawOutput),                   // fix-verify attempt 1 gate — red
        new TestRunResult(0, "green"));                    // fix-verify attempt 1 retry — green
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
        RelayDriverOptions.NoGitCommit);

    await driver.RunTaskAsync(repo.Root, "noise-strip");

    var stage10Invocation = runner.Invocations.SingleOrDefault(i => i.Stage.Number == 10);
    Assert.NotNull(stage10Invocation);
    var lastOutput = stage10Invocation!.LastTestOutput ?? "";
    Assert.DoesNotContain("is blocked by", lastOutput, StringComparison.Ordinal);
    Assert.DoesNotContain("--bypass-protection", lastOutput, StringComparison.Ordinal);
    Assert.DoesNotContain("deny_read_user_home", lastOutput, StringComparison.Ordinal);
    Assert.DoesNotContain("Verified 1 pack(s)", lastOutput, StringComparison.Ordinal);
    Assert.Contains("Failed ImportantTest", lastOutput, StringComparison.Ordinal);
}
```

- [ ] **Step 6: Distill test output in `BuildFailureOutput`**

In `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs`, in `BuildFailureOutput` (line 271), change the test-output part:

```csharp
// OLD (lines 279-280):
if (testResult.ExitCode != 0)
    parts.Add(testResult.Output);

// NEW:
if (testResult.ExitCode != 0)
    parts.Add(SwivalSubagentRunner.ExtractFailureReason(testResult.Output));
```

No new `using` needed — same namespace.

- [ ] **Step 7: Run the end-to-end test, then the full suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_FailureOutputShownToAgent_HasNonoNoiseStripped"
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All pass. In particular the existing `RunTaskAsync_FixVerifyLoop_AgentReceivesFailingOutput` (asserts `Contains("Failed DeepCheck")`) still passes — `Failed DeepCheck` now anchors the strong tier and is retained verbatim.

- [ ] **Step 8: Commit**

```bash
git add src/VisualRelay.Core/Execution/ProcessRunners.Diagnostics.cs \
        src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs \
        tests/VisualRelay.Tests/SwivalSubagentRunnerToolPreflightTests.cs \
        tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs
git commit -m "feat(verify): extend distiller (strip Verified-pack/bare-deny, anchor Failed/FAIL) and feed it to the fix-verify agent"
```

---

## Task 5: Honest retry label + explicit flaky signal (close R2, Approach 3)

Two linked changes to `RunTestCommandWithRetryAsync` (`RelayDriver.Bootstrap.cs:69-94`):

1. **Relabel the retry reason (R2).** The pre-emptive `verify_retry reason=transient-fault` (line 80) is misleading — it pre-labels the result before the retry decides. Change it to `"first-run-nonzero"`, which is always accurate (we retry *because* the first run was nonzero).
2. **Label the flip as flaky (Approach 3).** When the retry flips red→green (line 84-90), the run was non-deterministic — that IS the "flaky" signal the spec's acceptance asks for ("a failing test that passes on re-run is labeled flaky and does not by itself hard-fail the gate"). Today the flip emits `verify_retry_pass` with `Data["result"]="pass-on-retry"` but never says *flaky* explicitly and has no dedicated test. Add `Data["classification"]="flaky"` to that event so the signal is explicit, and add a dedicated test asserting BOTH the flaky label AND that the flip does not hard-fail (commits green).

**Rationale (recorded — why whole-suite retry, not "re-run just the failing test"):** the spec's Approach 3 sketches "re-run just the failing test(s)" but that requires per-runner output parsing to identify which test failed — which R0c forbids ("per-runner output parsing is not general"). We therefore KEEP the existing whole-suite retry (`config.TestCommand` re-run once, gated by `config.RetryFlakyVerify`) and treat its red→green flip as the flaky signal. A flip is sufficient evidence of non-determinism without any per-runner knowledge. (A chronically-flaky suite remains visible via the count of `verify_retry` vs `verify_retry_pass`/`classification=flaky` events in the log — no extra state needed.)

**Files:**
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.Bootstrap.cs:77-90` — relabel `verify_retry` reason; add `classification=flaky` to `verify_retry_pass`.
- Test: `tests/VisualRelay.Tests/RelayDriverRetryFlakyVerifyTests.cs` — new label test + new explicit-flaky test.

**Interfaces:**
- `verify_retry` event: `Data["reason"]` changes from `"transient-fault"` → `"first-run-nonzero"`.
- `verify_retry_pass` event: gains `Data["classification"] = "flaky"` (keeps existing `Data["result"] = "pass-on-retry"`). Level stays `"info"`. No verdict change — the green retry already commits today (the gate is NOT hard-failed by the first red).

- [ ] **Step 1: Write the failing test**

In `tests/VisualRelay.Tests/RelayDriverRetryFlakyVerifyTests.cs`, the existing `RetryFlakyVerify_TransientFailThenPass_CommitsGreen` test asserts:

```csharp
Assert.Contains(sink.Events, e => e is { EventName: "verify_retry", Level: "warn" });
```

Add a new test that checks the new reason label:

```csharp
[Fact]
public async Task RetryFlakyVerify_ReasonLabel_IsFirstRunNonzero_NotTransientFault()
{
    using var repo = TestRepository.Create();
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 0);
    repo.WriteTask("retry-label", "# Retry label\n");
    var runner = new ScriptedSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"));     // stage 9 verify — retry also fails
    var sink = new InMemoryRelayEventSink();
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, sink),
        RelayDriverOptions.NoGitCommit);

    await driver.RunTaskAsync(repo.Root, "retry-label");

    var retryEvent = sink.Events.Single(e => e.EventName == "verify_retry");
    Assert.Equal("first-run-nonzero", retryEvent.Data?["reason"]);
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RetryFlakyVerify_ReasonLabel_IsFirstRunNonzero_NotTransientFault"
```

Expected: FAIL — currently emits `"transient-fault"`.

- [ ] **Step 3: Write the failing explicit-flaky test**

Add to `tests/VisualRelay.Tests/RelayDriverRetryFlakyVerifyTests.cs`:

```csharp
[Fact]
public async Task RetryFlakyVerify_FailThenPass_LabeledFlaky_AndDoesNotHardFailGate()
{
    // Acceptance (Approach 3): a failing test that passes on re-run is labeled
    // flaky and does NOT by itself hard-fail the gate.
    using var repo = TestRepository.Create();
    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 0);
    repo.WriteTask("flaky", "# Flaky\n");
    var runner = new ScriptedSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var tests = new ScriptedTestRunner(
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(0, "green"));            // stage 9 verify — retry flips green
    var sink = new InMemoryRelayEventSink();
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, tests, sink),
        RelayDriverOptions.NoGitCommit);

    var outcome = await driver.RunTaskAsync(repo.Root, "flaky");

    // Did NOT hard-fail: the flaky red flipped green and the task committed.
    Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    // The flip is explicitly labeled flaky.
    var flip = sink.Events.Single(e => e.EventName == "verify_retry_pass");
    Assert.Equal("flaky", flip.Data?["classification"]);
}
```

- [ ] **Step 4: Run the test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RetryFlakyVerify_FailThenPass_LabeledFlaky_AndDoesNotHardFailGate"
```

Expected: FAIL — `verify_retry_pass` has no `classification` key yet.

- [ ] **Step 5: Relabel the retry reason AND label the flip flaky in `RunTestCommandWithRetryAsync`**

In `src/VisualRelay.Core/Execution/RelayDriver.Bootstrap.cs`:

```csharp
// (a) line 80 — honest retry reason:
// OLD:
Data: new Dictionary<string, string> { ["reason"] = "transient-fault" }), ct);
// NEW:
Data: new Dictionary<string, string> { ["reason"] = "first-run-nonzero" }), ct);

// (b) lines 87-89 — explicit flaky classification on the red→green flip:
// OLD:
Data: new Dictionary<string, string> { ["result"] = "pass-on-retry" }), ct);
// NEW:
Data: new Dictionary<string, string>
{
    ["result"] = "pass-on-retry",
    ["classification"] = "flaky"   // first-run red flipped green on re-run = non-deterministic
}), ct);
```

- [ ] **Step 6: Confirm no existing assertion referenced the old label**

The existing tests in `RelayDriverRetryFlakyVerifyTests.cs` do NOT assert `Data["reason"]` (only `e is { EventName: "verify_retry", Level: "warn" }`) and assert `verify_retry_pass` only by name/level — so adding a key and changing the reason break nothing. Confirm:

```bash
grep -n "transient-fault\|transient_fault\|pass-on-retry\|\"reason\"\|\"result\"" \
  tests/VisualRelay.Tests/RelayDriverRetryFlakyVerifyTests.cs
```

If any `Assert` references `"transient-fault"` against `Data`, update it to `"first-run-nonzero"`.

- [ ] **Step 7: Run all retry tests, then the full suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RelayDriverRetryFlakyVerifyTests"
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All pass (the new label test, the new explicit-flaky test, and the unchanged existing retry tests).

- [ ] **Step 8: Commit**

```bash
git add src/VisualRelay.Core/Execution/RelayDriver.Bootstrap.cs \
        tests/VisualRelay.Tests/RelayDriverRetryFlakyVerifyTests.cs
git commit -m "feat(verify): honest verify_retry reason (first-run-nonzero) + explicit flaky classification on red-to-green flip"
```

---

## Task 6: Add Implement/Fix prompt instruction to treat nonzero-with-zero-failures as real failure (shift-left, close R0c partial)

**Files:**
- Modify: `src/VisualRelay.Core/Execution/RelayStages.cs` — update `"Implement"` and `"Fix"` system prompts
- Test: `tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs` — add assertions

**Interfaces:**
- Produces: Updated `RelayStages.All` entries for stages 6 and 8 with enhanced system prompts. No runtime behavior changes (prompt only).

- [ ] **Step 1: Write the failing tests**

Add to `tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs`:

```csharp
[Theory]
[InlineData("Implement")]
[InlineData("Fix")]
[InlineData("Fix-verify")]
public void CodingStageSystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure(string stageName)
{
    var stage = RelayStages.All.Single(s => s.Name == stageName);
    // The prompt must tell the agent that a nonzero exit = failure even when
    // the summary reports 0 failed tests.
    Assert.Contains("nonzero", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("exit", stage.SystemPrompt, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run to confirm they fail for Implement and Fix (Fix-verify was handled in Task 3)**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~CodingStageSystemPromptTests.CodingStageSystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure"
```

Expected: Implement and Fix fail (Fix-verify passes from Task 3).

- [ ] **Step 3: Update Implement and Fix system prompts in `RelayStages.cs`**

In `src/VisualRelay.Core/Execution/RelayStages.cs`, update the `"Implement"` case. Append to the existing instruction (after the `"do NOT reformat"` sentence), keeping the existing text intact:

```csharp
"Implement" =>
    "Implement the change within the manifest files. " +
    "Verify your changes using ONLY the targeted test command shown in the " +
    "## Verify command section of the prompt. Do NOT run the project's full " +
    "check, lint, or format gate (e.g. `./visual-relay check`) during " +
    "implementation — the harness runs the full gate at the Verify stage. " +
    "Treat a nonzero exit as a real, unfinished failure even when the summary " +
    "says '0 failed': inspect the output tail for a non-test gate and resolve " +
    "it legitimately. " +
    "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
    "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",
```

Update the `"Fix"` case similarly:

```csharp
"Fix" =>
    "Resolve every blocker and warning from review. " +
    "Verify your changes using ONLY the targeted test command shown in the " +
    "## Verify command section of the prompt. Do NOT run the project's full " +
    "check, lint, or format gate during implementation — the harness runs the " +
    "full gate at the Verify stage. " +
    "Treat a nonzero exit as a real, unfinished failure even when the summary " +
    "says '0 failed': inspect the output tail for a non-test gate and resolve " +
    "it legitimately. " +
    "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
    "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~CodingStageSystemPromptTests"
```

Expected: All pass.

- [ ] **Step 5: Run the full suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/VisualRelay.Core/Execution/RelayStages.cs \
        tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs
git commit -m "feat(stages): instruct Implement/Fix agents to treat nonzero-exit as real failure (shift-left)"
```

---

## Task 7: Add launch drift-guard unit test (Approach 5 — keep sandbox parity provable)

**Files:**
- Test: `tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs` (add to existing file)

**Interfaces:**
- Consumes: `SwivalSubagentRunner.BuildNonoPrefix(RelayConfig config, bool rollback)` — `internal static`, returns `IReadOnlyList<string>`. This is the shared builder for both the swival agent launch (`rollback: true`) and the verify launch (`rollback: false`). Asserting they differ only in the `--rollback --no-rollback-prompt` pair locks the invariant.
- Produces: Test that fails if either prefix diverges (different profile, missing `--allow-cwd`, etc.).

The spec (Approach 5) asks for a test asserting the agent and verify launches resolve to the same profile, capability set, cwd, and env overlay — allowing only the documented `--rollback` difference. `BuildNonoPrefix` is the single method that produces both prefixes; testing it directly (pure, no I/O) covers this exactly.

- [ ] **Step 1: Write the failing test (pre-verify it passes first since this is a guard not a feature)**

Add to `tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs`:

```csharp
[Fact]
public void BuildNonoPrefix_AgentAndVerifyLaunches_DifferOnlyInRollbackFlag()
{
    // The agent launch uses rollback:true, verify launch uses rollback:false.
    // Everything else (profile, --allow-cwd, extra allow paths) must be identical.
    var config = TestConfig() with { BypassSandbox = false };

    var agentPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true).ToList();
    var verifyPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false).ToList();

    // Both must start with: run -p vr-guard --allow-cwd
    Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd" }, agentPrefix.Take(4));
    Assert.Equal(new[] { "run", "-p", "vr-guard", "--allow-cwd" }, verifyPrefix.Take(4));

    // Agent must have --rollback and --no-rollback-prompt; verify must NOT.
    Assert.Contains("--rollback", agentPrefix);
    Assert.Contains("--no-rollback-prompt", agentPrefix);
    Assert.DoesNotContain("--rollback", verifyPrefix);
    Assert.DoesNotContain("--no-rollback-prompt", verifyPrefix);

    // The non-rollback portions must be identical (drop --rollback/--no-rollback-prompt from agent).
    var agentCore = agentPrefix.Except(["--rollback", "--no-rollback-prompt"]).ToList();
    Assert.Equal(agentCore, verifyPrefix);
}

[Fact]
public void BuildNonoPrefix_WithExtraAllowPaths_BothLaunchesCarryTheSamePaths()
{
    var config = TestConfig() with
    {
        BypassSandbox = false,
        SandboxExtraAllowPaths = ["/tmp/extra-cache", "/tmp/extra-build"]
    };

    var agentPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true).ToList();
    var verifyPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false).ToList();

    // Both must contain the extra paths as -a <path> pairs.
    foreach (var extraPath in config.SandboxExtraAllowPaths!)
    {
        var agentIdx = agentPrefix.IndexOf("-a");
        while (agentIdx >= 0 && agentPrefix[agentIdx + 1] != extraPath)
            agentIdx = agentPrefix.IndexOf("-a", agentIdx + 1);
        Assert.True(agentIdx >= 0, $"agent prefix missing -a {extraPath}");

        var verifyIdx = verifyPrefix.IndexOf("-a");
        while (verifyIdx >= 0 && verifyPrefix[verifyIdx + 1] != extraPath)
            verifyIdx = verifyPrefix.IndexOf("-a", verifyIdx + 1);
        Assert.True(verifyIdx >= 0, $"verify prefix missing -a {extraPath}");
    }
}
```

- [ ] **Step 2: Run the tests to verify they pass (guard tests, not feature tests)**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~BuildNonoPrefix_AgentAndVerifyLaunches_DifferOnlyInRollbackFlag|FullyQualifiedName~BuildNonoPrefix_WithExtraAllowPaths_BothLaunchesCarryTheSamePaths"
```

Expected: PASS (these guard existing behavior; if they fail, the sandbox parity was already broken and must be fixed before merging).

- [ ] **Step 3: Run the full suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All pass.

- [ ] **Step 4: Commit**

```bash
git add tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs
git commit -m "test(sandbox): drift-guard asserting agent and verify launches share identical nono prefix except rollback"
```

---

## Task 8: Isolated verify tree — run the authoritative gate against a full-fidelity snapshot (Approach 6)

This is the most invasive change. It must satisfy four corrected requirements:

- **Snapshot fidelity (Defect C).** The snapshot MUST reflect the FULL working-tree state of `rootPath` = committed `HEAD` + ALL uncommitted changes (tracked modifications AND untracked-not-ignored files). Copying ONLY the manifest files would reintroduce the exact R0a divergence this task exists to kill: an agent edit OUTSIDE the manifest (the real fix) would be missing from the snapshot, so the gate would verify the wrong code. The agent edits the live `rootPath`, so the snapshot must mirror it exactly.
- **Mutation detection by DELTA (Defect D).** The advisory must name only files the TEST RUN wrote — not the agent's own edits. Capture the worktree's dirty set IMMEDIATELY AFTER the snapshot overlay / BEFORE running the test, then re-diff AFTER; the advisory is the delta (newly dirty / newly untracked). The "exclude manifest files" heuristic is wrong (the agent may legitimately edit non-manifest files) and is removed.
- **Isolation scope = BOTH authoritative gates (Defect F).** `RunTestCommandWithRetryAsync` is the single authoritative-gate function, called at BOTH stage 9 (`RelayDriver.cs:193`) and stage 10 (`RelayDriver.VerifyFix.cs:155`). We isolate BOTH by routing both through one new `RunIsolatedVerifyAsync` helper, so the live `rootPath` is never polluted by either gate's suite writes. (The stage-9 baseline-diff `GetNewFailuresAsync` at `RelayDriver.cs:219` is NOT the gate — it is a stash/restore baseline comparison that is net-neutral on the tree — and stays in the live repo; note this explicitly so a reviewer doesn't expect it isolated.)
- **No test-only types in production (Defect E).** `TestGit.InitAsync/AddAllAsync/CommitAsync` and `TestFileSystem.DeleteDirectoryResilient` do NOT exist for production use: `TestGit` exposes only the sync `Run(root, params args)`; `TestFileSystem` is a test-only type. The production fallback uses `Directory.Delete(path, recursive: true)` in try/catch — never `TestFileSystem`. The test initializes git via `TestGit.Run`.

**Files:**
- Create: `src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs` — new partial: `CreateVerifyWorktreeAsync`, `CaptureDirtySetAsync`, `RunIsolatedVerifyAsync`, `CleanupVerifyWorktreeAsync`.
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs` — replace the stage-10 `RunTestCommandWithRetryAsync(rootPath, ...)` call with `RunIsolatedVerifyAsync(...)`; emit `verify_mutated_tree` from the returned delta.
- Modify: `src/VisualRelay.Core/Execution/RelayDriver.cs` — replace the stage-9 `RunTestCommandWithRetryAsync(rootPath, ...)` call (line 193) with `RunIsolatedVerifyAsync(...)`; emit `verify_mutated_tree` from the returned delta.
- Test: `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs` — assert the real `rootPath` is unmodified after a verify that writes a tracked file, and a `verify_mutated_tree` advisory names that file.

**Interfaces (all verified against real signatures):**
- `IGitInvoker.RunAsync(string rootPath, IEnumerable<string> arguments, CancellationToken ct, TimeSpan? timeout = null, IReadOnlyDictionary<string,string>? environment = null, CancellationToken killToken = default, Action<string>? onActivity = null)` returns `Task<(int ExitCode, string Output, bool TimedOut)>` (`IGitInvoker.cs:14`). Call positionally: `await _dependencies.GitInvoker.RunAsync(path, new[] { ... }, cancellationToken)`; destructure as `var (exit, output, timedOut) = ...`.
- Reuse `PlanningWorktree.CreateAsync(string repoRoot, string taskId, string runId, CancellationToken, IGitInvoker? = null)` (`PlanningWorktree.cs:35`) for worktree creation — it does `git worktree add --detach --quiet <tempPath> HEAD` under a temp namespace, retries transient git failures, and throws on hard failure. Reuse `PlanningWorktree.RemoveAsync(repoRoot, worktreePath, ct, gitInvoker)` for teardown (best-effort, never throws). Pass a per-run unique id (e.g. `$"{taskId}-verify-s{stageNumber}-a{attempt}"`) so concurrent/repeated verifies never collide.
- Enumerate uncommitted state with the same NUL-safe git the codebase already uses (`WorktreeFilter.cs:71-119`): `git diff --name-only -z` (tracked mods) and `git ls-files --others --exclude-standard -z` (untracked-not-ignored). Apply the tracked diff to the worktree via `git -C <rootPath> diff HEAD` piped to `git -C <worktree> apply`, OR (simpler and robust for binary/edge cases) copy each enumerated dirty/untracked file from `rootPath` into the worktree with `File.Copy(..., overwrite: true)`. Prefer the copy approach (no patch-context failures); the patch approach is acceptable if `git apply --whitespace=nowarn` is used and tested.
- Produces: `verify_mutated_tree` `RelayEvent` (level `"warn"`) with `Data["files"]` = space-joined DELTA paths the TEST RUN wrote, plus `Data["advice"]`. Emitted only when the delta is non-empty. The real `rootPath` tree is always unaffected by the gate.

**Note on `RunTestCommandWithRetryAsync`:** it already takes `rootPath` as its first parameter and runs `config.TestCommand` against it (`RelayDriver.Bootstrap.cs:73`). `RunIsolatedVerifyAsync` simply passes the SNAPSHOT path as that `rootPath`, so the retry logic, `verify_retry`, and `verify_retry_pass` events (Task 5) all run unchanged against the snapshot. No change to `RunTestCommandWithRetryAsync`'s body is required.

- [ ] **Step 1: Write the failing test**

Add to `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs`:

```csharp
[Fact]
public async Task RunVerifyFixLoop_VerifyWritesTrackedFile_RealRepoUnmodifiedAndAdvisoryEmitted()
{
    // When the test command writes a tracked file during verify, the real rootPath
    // must be unmodified (the write lands in the isolated snapshot), and a
    // verify_mutated_tree advisory naming that file must be emitted.
    using var repo = TestRepository.Create();
    // Real git repo (full-fidelity snapshot needs HEAD). TestGit.Run is SYNC.
    TestGit.Run(repo.Root, "init");
    TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
    TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
    var trackedRel = Path.Combine("src", "app.cs");
    var trackedFilePath = Path.Combine(repo.Root, trackedRel);
    Directory.CreateDirectory(Path.GetDirectoryName(trackedFilePath)!);
    File.WriteAllText(trackedFilePath, "// original");
    TestGit.Run(repo.Root, "add", ".");
    TestGit.Run(repo.Root, "commit", "-m", "seed");

    repo.WriteConfig("dotnet test", [], baselineVerify: false, maxVerifyLoops: 1);
    repo.WriteTask("mutating-verify", "# Mutating verify\n");

    // A test runner that writes a TRACKED file relative to the rootPath it RECEIVES
    // (= the snapshot when isolation works), simulating a non-idempotent suite that
    // rewrites tracked files (e.g. TEST-TIMING.md, ratchet-status.json).
    var mutatingTests = new MutatingTestRunner(
        trackedRel, "// written by test run",
        new TestRunResult(1, "red"),              // stage 5 author gate
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — first run fails
        new TestRunResult(1, "Failed TestX"),      // stage 9 verify — retry also fails
        new TestRunResult(1, "Failed TestX"),      // fix-verify attempt 1 gate — red
        new TestRunResult(0, "green"));            // fix-verify attempt 1 retry — green

    var sink = new InMemoryRelayEventSink();
    var runner = new ScriptedSubagentRunner();
    runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
    var driver = new RelayDriver(
        RelayDriverDependencies.ForTests(runner, mutatingTests, sink),
        RelayDriverOptions.NoGitCommit);

    await driver.RunTaskAsync(repo.Root, "mutating-verify");

    // Real repo must still have the original committed content — the suite's write
    // went into the throwaway snapshot, not here.
    Assert.Equal("// original", await File.ReadAllTextAsync(trackedFilePath));
    // Advisory event must name the file the TEST RUN wrote (forward-slash path).
    var advisory = sink.Events.FirstOrDefault(e => e.EventName == "verify_mutated_tree");
    Assert.NotNull(advisory);
    Assert.Contains("src/app.cs", advisory!.Data?["files"] ?? "", StringComparison.Ordinal);
}
```

Add `MutatingTestRunner` to `tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs` (or a split file). It writes relative to the RECEIVED `rootPath` so the write lands in the snapshot, proving both isolation and delta-detection:

```csharp
/// <summary>
/// On every gate call, writes <paramref name="content"/> to
/// <paramref name="relativePath"/> UNDER THE ROOTPATH IT RECEIVES (the snapshot
/// when isolation is active), simulating a non-idempotent suite, then returns the
/// next scripted result.
/// </summary>
internal sealed class MutatingTestRunner(
    string relativePath,
    string content,
    params TestRunResult[] results) : ITestRunner
{
    private readonly Queue<TestRunResult> _results = new(results);

    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, content, cancellationToken);
        return _results.Count > 0 ? _results.Dequeue() : new TestRunResult(0, "green");
    }
}
```

> NOTE the scripted slots now cover stage 9 (run + retry) AND a stage-10 attempt (gate + retry). Because `RunIsolatedVerifyAsync` runs the suite in the snapshot, the `MutatingTestRunner` writes into the snapshot at each gate — the live `repo.Root` is never written by the gate, so the assertion `// original` holds.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_VerifyWritesTrackedFile_RealRepoUnmodifiedAndAdvisoryEmitted"
```

Expected: FAIL — currently the verify runs in-place and mutates `rootPath`.

- [ ] **Step 3: Create `RelayDriver.VerifyWorktree.cs` (full-fidelity snapshot + delta detection + safe fallback)**

Create `src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs`. RE-CONFIRM `PlanningWorktree.CreateAsync`/`RemoveAsync` signatures (`PlanningWorktree.cs:35,73`) and `IGitInvoker.RunAsync` (`IGitInvoker.cs:14`) before editing.

```csharp
using System.Text;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Runs the authoritative gate (via <see cref="RunTestCommandWithRetryAsync"/>)
    /// against an ISOLATED, FULL-FIDELITY snapshot of <paramref name="rootPath"/> =
    /// committed HEAD + ALL uncommitted changes (tracked mods AND untracked-not-
    /// ignored). The suite may write freely in the snapshot; the real repo is never
    /// polluted. Returns the gate result and the DELTA — the files the TEST RUN wrote
    /// (captured before vs after the suite ran), NOT the agent's own edits.
    /// If <paramref name="rootPath"/> is not a git repo (or worktree creation fails),
    /// falls back to running against <paramref name="rootPath"/> directly with an
    /// empty delta (preserves today's behavior for non-git test fixtures).
    /// </summary>
    private async Task<(TestRunResult Result, IReadOnlyList<string> Mutations)> RunIsolatedVerifyAsync(
        string rootPath, RelayConfig config, int stageNumber, int attempt,
        string runId, string taskId, CancellationToken cancellationToken)
    {
        string? worktreePath = null;
        var worktreeId = $"{taskId}-verify-s{stageNumber}-a{attempt}";
        try
        {
            worktreePath = await CreateVerifyWorktreeAsync(rootPath, worktreeId, runId, cancellationToken);
        }
        catch
        {
            worktreePath = null; // non-git fixture or transient git failure → no isolation
        }

        if (worktreePath is null)
        {
            var inPlace = await RunTestCommandWithRetryAsync(rootPath, config, cancellationToken, stageNumber, runId, taskId);
            return (inPlace, Array.Empty<string>());
        }

        try
        {
            // Dirty set IMMEDIATELY AFTER the overlay / BEFORE the suite runs.
            var before = await CaptureDirtySetAsync(worktreePath, cancellationToken);
            var result = await RunTestCommandWithRetryAsync(worktreePath, config, cancellationToken, stageNumber, runId, taskId);
            // Dirty set AFTER the suite ran — the DELTA is the suite's writes.
            var after = await CaptureDirtySetAsync(worktreePath, cancellationToken);
            var mutations = after.Where(p => !before.Contains(p))
                                 .OrderBy(p => p, StringComparer.Ordinal)
                                 .ToList();
            return (result, mutations);
        }
        finally
        {
            await CleanupVerifyWorktreeAsync(rootPath, worktreePath, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a detached HEAD worktree (reusing <see cref="PlanningWorktree.CreateAsync"/>)
    /// then OVERLAYS the full uncommitted state of <paramref name="sourcePath"/> onto it:
    /// every tracked-modified and untracked-not-ignored file is copied across, so the
    /// snapshot mirrors exactly what the agent produced (Defect C). Throws if
    /// <paramref name="sourcePath"/> is not a git repo (caller catches → fallback).
    /// </summary>
    private async Task<string> CreateVerifyWorktreeAsync(
        string sourcePath, string worktreeId, string runId, CancellationToken cancellationToken)
    {
        var worktreePath = await PlanningWorktree.CreateAsync(
            sourcePath, worktreeId, runId, cancellationToken, _dependencies.GitInvoker);

        foreach (var relative in await EnumerateUncommittedAsync(sourcePath, cancellationToken))
        {
            var src = Path.Combine(sourcePath, relative);
            if (!File.Exists(src)) continue; // deleted file: leave the HEAD copy (or handle deletes if needed)
            var dst = Path.Combine(worktreePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        return worktreePath;
    }

    /// <summary>Tracked-modified + untracked-not-ignored repo-relative paths (NUL-safe).</summary>
    private async Task<IReadOnlyList<string>> EnumerateUncommittedAsync(string rootPath, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var diff = await _dependencies.GitInvoker.RunAsync(rootPath, new[] { "diff", "--name-only", "-z" }, cancellationToken);
        foreach (var p in SplitNul(diff.Output)) set.Add(p);
        var untracked = await _dependencies.GitInvoker.RunAsync(rootPath, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, cancellationToken);
        foreach (var p in SplitNul(untracked.Output)) set.Add(p);
        return set.ToList();
    }

    /// <summary>The worktree's current dirty set (tracked mods + untracked), NUL-safe.</summary>
    private async Task<HashSet<string>> CaptureDirtySetAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var diff = await _dependencies.GitInvoker.RunAsync(worktreePath, new[] { "diff", "--name-only", "-z" }, cancellationToken);
        foreach (var p in SplitNul(diff.Output)) set.Add(p);
        var untracked = await _dependencies.GitInvoker.RunAsync(worktreePath, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, cancellationToken);
        foreach (var p in SplitNul(untracked.Output)) set.Add(p);
        return set;
    }

    private static IEnumerable<string> SplitNul(string? gitOutput) =>
        (gitOutput ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Best-effort teardown: git worktree remove, then a resilient dir delete.</summary>
    private async Task CleanupVerifyWorktreeAsync(string sourcePath, string worktreePath, CancellationToken cancellationToken)
    {
        await PlanningWorktree.RemoveAsync(sourcePath, worktreePath, cancellationToken, _dependencies.GitInvoker);
        try { if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true); }
        catch { /* PRODUCTION fallback — never reference TestFileSystem here (Defect E). */ }
    }
}
```

Notes verified against source: `PlanningWorktree.CreateAsync` already does `git worktree add --detach --quiet <temp> HEAD` and throws on hard failure (so a non-git fixture throws → caught → fallback). `git diff --name-only -z` and `git ls-files --others --exclude-standard -z` are the same NUL-safe enumerations used in `WorktreeFilter.cs:71-119`. The delta (`after \ before`) names ONLY what the suite wrote, because the agent's edits are ALREADY in `before` (they were overlaid before the suite ran) — this is Defect D done with git, not the manifest heuristic. `IGitInvoker.RunAsync` returns `(int ExitCode, string Output, bool TimedOut)`; we only read `.Output`.

- [ ] **Step 4: Build to verify the new partial compiles**

```bash
dotnet build tests/VisualRelay.Tests/VisualRelay.Tests.csproj
```

Expected: Build succeeded, 0 errors. The partial references only production types (`PlanningWorktree`, `IGitInvoker`, `RunTestCommandWithRetryAsync`, `RelayConfig`, `TestRunResult`) — NO `TestFileSystem`.

- [ ] **Step 5: Route BOTH authoritative gates through `RunIsolatedVerifyAsync` (Defect F)**

(a) Stage 10 — `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`, replace the line-155 `var testResult = await RunTestCommandWithRetryAsync(rootPath, config, cancellationToken, 10, runId, taskId);`:

```csharp
var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(
    rootPath, config, stageNumber: 10, attempt: attempt, runId, taskId, cancellationToken);
await EmitMutatedTreeAdvisoryAsync(rootPath, runId, taskId, stage, verifyMutations, cancellationToken);
```

(b) Stage 9 — `src/VisualRelay.Core/Execution/RelayDriver.cs`, replace the line-193 `var testResult = await RunTestCommandWithRetryAsync(rootPath, config, cancellationToken, 9, runId, taskId);`:

```csharp
var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(
    rootPath, config, stageNumber: 9, attempt: 1, runId, taskId, cancellationToken);
await EmitMutatedTreeAdvisoryAsync(rootPath, runId, taskId, stage, verifyMutations, cancellationToken);
```

Add the shared advisory emitter to `RelayDriver.VerifyWorktree.cs` (keeps both call sites identical and small):

```csharp
private async Task EmitMutatedTreeAdvisoryAsync(
    string rootPath, string runId, string taskId, RelayStageDefinition stage,
    IReadOnlyList<string> mutations, CancellationToken cancellationToken)
{
    if (mutations.Count == 0) return;
    await _dependencies.EventSink.PublishAsync(new RelayEvent(
        DateTimeOffset.UtcNow, "warn", "verify_mutated_tree", runId, rootPath, taskId,
        stage.Number, stage.Tier,
        Data: new Dictionary<string, string>
        {
            ["files"] = string.Join(' ', mutations),
            ["advice"] = "the test command wrote these files during verify; VR ran the gate in an "
                       + "isolated tree so the repo is unaffected — gitignore them or use a non-writing "
                       + "test command for idempotent verification"
        }), cancellationToken);
}
```

Add `using VisualRelay.Domain;` to `RelayDriver.VerifyWorktree.cs` for `RelayEvent`/`RelayStageDefinition`/`TestRunResult`/`RelayConfig` (re-confirm which namespace each lives in; mirror the `using`s at the top of `RelayDriver.VerifyFix.cs`). Keep `Task 1`'s `PublishVerifyResultAsync` call AFTER this — it reports the (now isolated) `testResult`.

> Re-confirm the stage-9 in-scope `stage` variable is the Verify stage (`.Number == 9`); if not, pass `RelayStages.All[8]`. The `testResult` variable name is reused at both sites, so downstream code (timeout guard, `check` computation, `BuildFailureOutput`) is unchanged.

- [ ] **Step 6: Run the isolated-tree test + size guard**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RunVerifyFixLoop_VerifyWritesTrackedFile_RealRepoUnmodifiedAndAdvisoryEmitted"
bash tools/guards/check-file-size.sh
```

Expected: PASS; guard clean (the bulk of new code is in `RelayDriver.VerifyWorktree.cs`; the call-site edits in `VerifyFix.cs`/`RelayDriver.cs` are 2-3 lines each).

- [ ] **Step 7: Run the full suite — confirm the non-git fixtures still work via fallback**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build
```

Expected: All pass. The many existing tests that use `TestRepository` WITHOUT `git init` (e.g. `RunTaskAsync_FixableVerifyFailure_CommitsAfterFixVerifyLoop`, the rewritten `RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops`, all of `TargetedTestInvocationTests`, `RelayDriverRetryFlakyVerifyTests`, etc.) hit the fallback: `PlanningWorktree.CreateAsync` throws on a non-git dir → caught → the gate runs in-place against `rootPath` exactly as before, with an empty mutation delta (no `verify_mutated_tree`). Those tests therefore see NO behavior change. Also confirm the Task-1 `verify_result`, Task-2 convergence, and Task-5 retry/flaky tests still pass (they all use non-git fixtures → fallback path).

> **Interaction with the Task-2 convergence guard (verify):** with isolation, the agent still edits the live `rootPath`, and `WorkingTreeHash` reads the live `rootPath` manifest — so the convergence guard's tree-change detection is unaffected by isolation (the snapshot is only where the GATE runs). The `RunVerifyFixLoop_TreeChangedBetweenAttempts_DoesNotBailEarly` test uses a non-git fixture (fallback), and `WriteOnAttemptSubagentRunner` writes to the live `rootPath`, so both the hash change AND the gate behavior are as designed.

- [ ] **Step 8: Commit**

```bash
git add src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs \
        src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs \
        src/VisualRelay.Core/Execution/RelayDriver.cs \
        tests/VisualRelay.Tests/RelayDriverVerifyFixTests.cs
git commit -m "feat(verify): isolate BOTH authoritative gates in a full-fidelity snapshot; advise on suite file-writes (delta-detected)"
```

---

## Task 9: Final verification and regression sweep

**Files:** (no changes — verification only)

- [ ] **Step 1: Build**

```bash
dotnet build tests/VisualRelay.Tests/VisualRelay.Tests.csproj
```

Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Run the full test suite**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj
```

Expected: All pass. Previously 1114 passed, 28 skipped, 0 failed. New total ≥ 1125 passed.

- [ ] **Step 3: Run all fix-verify and related tests to confirm all acceptance criteria are met**

```bash
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build \
  --filter "FullyQualifiedName~RelayDriverVerifyFixTests|FullyQualifiedName~RelayDriverRetryFlakyVerifyTests|FullyQualifiedName~CodingStageSystemPromptTests|FullyQualifiedName~SandboxedTestRunnerArgumentTests|FullyQualifiedName~TargetedTestInvocationTests|FullyQualifiedName~SwivalSubagentRunnerToolPreflightTests"
```

**Acceptance criteria checklist (spec "Acceptance" + Approach-0 prerequisite):**
- [ ] Non-convergent loop bails on attempt 2 (`RunVerifyFixLoop_NonConvergent_BailsOnAttempt2`)
- [ ] Convergent loop (manifest tree changes) does NOT bail early (`RunVerifyFixLoop_TreeChangedBetweenAttempts_DoesNotBailEarly`)
- [ ] A flaky test (fails then passes on re-run) is labeled flaky AND does not hard-fail the gate (`RetryFlakyVerify_FailThenPass_LabeledFlaky_AndDoesNotHardFailGate`; the pre-existing `RetryFlakyVerify_TransientFailThenPass_CommitsGreen` still passes too)
- [ ] Stage-10 invocation uses `config.TestCommand`, not the targeted subset, while stages 6/8 keep targeted (`RunVerifyFixLoop_Stage10Invocation_UsesFullGateCommand_NotTargetedSubset` + the INVERTED `TargetedTestInvocationTests` stage-10 assertion + the UNCHANGED stage-6/8 assertion)
- [ ] Fix-verify, Implement, and Fix prompts instruct agent to treat nonzero-with-zero-failures as real (`CodingStageSystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure`, `FixVerify_SystemPrompt_InstructsAgentToTreatNonzeroAsRealFailure`)
- [ ] Fix-verify prompt forbids reward hacking + has the "report as a non-test gate" fallback (`FixVerify_SystemPrompt_ForbidsRewardHacking_AndHasNonTestGateFallback`)
- [ ] Distiller acceptance: `deny_*` / `Verified N pack(s)` gone, the real `Failed X` present (direct unit `ExtractFailureReason_VerifyOutput_StripsPackAndDenyNoise_KeepsFailedTest`; end-to-end `RunVerifyFixLoop_FailureOutputShownToAgent_HasNonoNoiseStripped`)
- [ ] Launch drift-guard: agent and verify prefixes differ only in `--rollback` pair (`BuildNonoPrefix_AgentAndVerifyLaunches_DifferOnlyInRollbackFlag`)
- [ ] Isolated verify tree: real rootPath unmodified when verify writes a tracked file; `verify_mutated_tree` advisory names it (`RunVerifyFixLoop_VerifyWritesTrackedFile_RealRepoUnmodifiedAndAdvisoryEmitted`)
- [ ] (R5 prerequisite) `verify_result` event at BOTH stage 9 and stage 10 with command + exitCode + check + treeHash + outputFile pointer; full output persisted to artifact (`RunVerifyFixLoop_EmitsVerifyResultEvent_AtStage9AndStage10_WithOutputFilePointer`)
- [ ] `verify_retry` reason is `"first-run-nonzero"` not `"transient-fault"` (`RetryFlakyVerify_ReasonLabel_IsFirstRunNonzero_NotTransientFault`)
- [ ] Normal path unbroken: a genuinely fixable failure still loops and commits (`RunTaskAsync_FixableVerifyFailure_CommitsAfterFixVerifyLoop`); a genuinely green suite passes on attempt 1 (`RunTaskAsync_VerifyGreen_SkipsFixVerifyLlmCall_ButRecordsStage10Green`)
- [ ] **(UPDATED, not preserved verbatim)** Genuinely unfixable failure (agent CHANGES the tree each attempt, verify stays red) still flags after maxLoops — `RunTaskAsync_UnfixableVerifyFailure_FlagsAfterMaxLoops` is REWRITTEN in Task 2 Step 6 to use `WriteEachAttemptSubagentRunner` so the convergence guard does not fire and the max-loops bound is what's exercised. The old no-op-subagent version of this test would now (correctly) bail as non-convergent and is replaced.

> **Spec coverage note — every "Acceptance" bullet maps to a test above.** The spec's Approach-0 sub-bullet "capture stdout/stderr separately" is intentionally NOT implemented (recorded scope decision in Task 1, Step 6) — we use regex distillation (Task 4) as the spec-sanctioned alternative. No acceptance criterion depends on stream separation.

- [ ] **Step 4: Confirm no uncommitted changes**

```bash
git status
# Expected: clean working tree.
```
