# Harness: run Review on `balanced`, escalate to `frontier` only when needed

## Current state (researched)

### Stage 7 "Review" is hardwired to the `frontier` tier

`src/VisualRelay.Core/Execution/RelayStages.cs:15`:
```csharp
Stage(7, "Review", "frontier", "some", "all", """{ "verdict": "pass"|"changes", "issues": [] }"""),
```

The tier string (`"frontier"`) is a field of `RelayStageDefinition`
(`src/VisualRelay.Domain/RelayStageDefinition.cs:6`).  It flows through
`BuildInvocation` (`src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs:244`) into
`StageInvocation.Tier` (`src/VisualRelay.Domain/StageInvocation.cs:6`).  Inside
`SwivalSubagentRunner.BuildArguments`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs:48`) that Tier is mapped to a swival
`--profile` argument via `config.TierProfiles`:

```csharp
var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
// → "--profile", profile
```

So the path is: `RelayStages.All[6].Tier` ("frontier") → `StageInvocation.Tier` →
`TierProfiles["frontier"]` → swival `--profile frontier` → Kimi K2 model
($0.95 input / $4.00 output per 1M tokens,
`src/VisualRelay.Core/Costs/RelayPricing.cs:15`).

The `balanced` tier maps to a cheaper model
(`src/VisualRelay.Core/Costs/RelayPricing.cs:14`, $0.435 input / $0.87 output).

### The cost problem

On 2026-06-13 task 01 Review cost **$0.211, 56% of the entire task budget**, and
returned `{"verdict":"pass","issues":[]}`.  Tasks 04, 05, 06 had frontier Reviews
costing only $0.024–$0.059 because those diffs had more substance to examine.  The
high-cost case is clean diffs: frontier burns large context budgets reasoning about
what turned out to be nothing.

### How the Review verdict is consumed (today: not at all by the driver)

The Review contract is `{ "verdict": "pass"|"changes", "issues": [] }`.  The driver's
main loop in `src/VisualRelay.Core/Execution/RelayDriver.cs:108–113` parses the JSON
with `TryParseContractJson` but does **not** branch on `verdict`.  Stage 8 (Fix) always
runs.  The verdict is only visible to the stage-8 LLM via the ledger.  No existing
code skips stage 8 when verdict is "pass".

### Re-running a stage at a different tier is straightforward

`BuildInvocation` (`RelayDriver.VerifyFix.cs:229–261`) accepts a `RelayStageDefinition`
and passes `stage.Tier` into `StageInvocation`.  Nothing prevents calling it twice with
the same stage but a different tier — the `StageInvocation.Tier` field is the sole
control point; it overrides `stage.Tier` conceptually (the invocation record carries
the effective tier).  The only `Tier`-consuming code is `BuildArguments` in
`ProcessRunners.cs:48` which reads `invocation.Tier`, not `stage.Tier`.

A conditional re-run therefore only requires:
1. Capturing the balanced-run result.
2. Deciding whether to escalate.
3. Building a second `StageInvocation` for the same stage with `Tier = "frontier"`.

The driver already has a precedent for conditional re-runs: the fix-verify loop
(`RelayDriver.VerifyFix.cs:56–206`) runs stage 10 multiple times in a bounded loop.

### Where diff size is available at Review time

At stage 7, the `manifest` list (populated at stage 4,
`RelayDriver.cs:117–136`) is in scope and passed to every `BuildInvocation` call.
The manifest is a list of relative file paths (`manifest.txt` on disk).  File sizes and
line counts are readable from disk.  `WorkingTreeHash` (`RelayDriver.Artifacts.cs:123`)
already iterates `manifest` and reads file content — the same loop can compute a line
count or byte count cheaply.

`git diff --stat HEAD` can be obtained via `GitInvoker.RunAsync`
(`src/VisualRelay.Core/Execution/GitInvoker.cs:61`) which the driver already uses in
other partial files.  A simple `--shortstat` or `--numstat` gives lines-changed and
changed-file count.

However, manifest count alone is a simpler, no-subprocess proxy: many files or large
manifest content → risky.  The manifest is already in memory at stage 7.

### Existing per-stage tier override / config

`RelayConfig.TierProfiles` (`RelayConfig.cs:9`, `RelayConfigLoader.cs:95–100`) is a
dictionary from logical tier name → swival profile name, readable/overridable in
`.relay/config.json` as `"tierProfiles"`.  There is **no** per-stage override
mechanism today.  The tier is baked into `RelayStageDefinition.Tier` at startup
(`RelayStages.cs`) and not runtime-configurable per-stage via config.

### Relevant first-output timeout by tier

`RelayConfig.FirstOutputTimeoutMsByTier` (`RelayConfig.cs:27–28`, defaults at
`RelayConfigLoader.cs:29–33`):
- `cheap`: 90 000 ms
- `balanced`: 120 000 ms
- `frontier`: 660 000 ms (comment: "healthy max ~412 s; Review stage, heaviest
  reasoning")

A balanced Review will have a tighter first-output window automatically, which also
benefits stall-resilience.

## What to build

**Recommended design: (a) balanced-first, escalate-on-signal.**

Rationale: option (b) (risk-gate up-front) is simpler but requires a heuristic that
correctly classifies every risky diff.  The cases that need frontier-grade scrutiny —
cross-cutting changes, security-sensitive paths, large diffs — are exactly the ones
that are hardest to detect by file count alone; a cheap model might plausibly
misclassify a subtle but dangerous diff.  Option (a) never loses frontier quality: it
guarantees frontier runs whenever balanced isn't confident (non-pass verdict or flagged
by a risk heuristic), at the cost of two model calls on those diffs.  On clean diffs —
the expensive case empirically — a single balanced call suffices.  The "double-run"
overhead on flagged diffs is acceptable because: (i) those are the minority; (ii) the
total cost is balanced + frontier, not 2× frontier.

The heuristic for escalation after a balanced Review should be simple and
over-inclusive (false positives are free on quality, just slightly more expensive):
escalate if `verdict != "pass"` OR `issues` is non-empty OR manifest contains more than
N files (suggested default: 10) OR manifest total line count exceeds L (suggested
default: 500 lines across all changed files).  The thresholds are configurable.

### Steps

1. **Add `ReviewEscalation` configuration to `RelayConfig`.**

   Add two optional fields to `RelayConfig` (`src/VisualRelay.Domain/RelayConfig.cs`):

   ```csharp
   // When true, stage 7 (Review) runs on the `balanced` tier first; a second
   // frontier-tier Review runs only if the balanced verdict is non-pass, issues
   // are found, or a diff-complexity heuristic trips.  Default true.
   bool ReviewEscalationEnabled = true,
   // Heuristic thresholds for auto-escalation after a passing balanced Review.
   // ManifestFileCount: escalate if manifest has more than this many files.
   // ManifestLineCount: escalate if total lines across manifest files exceeds this.
   // 0 = disabled for that heuristic.  Defaults: 10 files, 500 lines.
   int ReviewEscalationManifestFileThreshold = 10,
   int ReviewEscalationManifestLineThreshold = 500,
   ```

   Expose them in `RelayConfigLoader` (`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`)
   as optional JSON fields `"reviewEscalationEnabled"` (bool),
   `"reviewEscalationManifestFileThreshold"` (int),
   `"reviewEscalationManifestLineThreshold"` (int) with the above defaults.

2. **Extract a `ReviewEscalationPolicy` helper.**

   Add a small static class (e.g.
   `src/VisualRelay.Core/Execution/ReviewEscalationPolicy.cs`):

   ```csharp
   internal static class ReviewEscalationPolicy
   {
       /// <summary>
       /// Returns true when the balanced Review result warrants a second,
       /// frontier-tier Review.  Escalates on: non-pass verdict, non-empty
       /// issues, or manifest complexity above configured thresholds.
       /// </summary>
       internal static bool ShouldEscalate(
           JsonElement reviewJson,
           IReadOnlyList<string> manifest,
           string rootPath,
           int fileThreshold,
           int lineThreshold)
       {
           // 1. Model signal: non-pass verdict or any issue reported.
           var verdict = reviewJson.TryGetProperty("verdict", out var v) ? v.GetString() : null;
           if (verdict != "pass") return true;
           if (reviewJson.TryGetProperty("issues", out var issues)
               && issues.ValueKind == JsonValueKind.Array
               && issues.GetArrayLength() > 0) return true;

           // 2. Diff complexity heuristic.
           if (fileThreshold > 0 && manifest.Count > fileThreshold) return true;
           if (lineThreshold > 0)
           {
               var totalLines = manifest.Sum(rel =>
               {
                   var path = Path.Combine(rootPath, rel);
                   return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
               });
               if (totalLines > lineThreshold) return true;
           }

           return false;
       }
   }
   ```

   Keep it pure and side-effect-free so it is trivially testable with no I/O mocking.

3. **Add conditional escalation logic for stage 7 in `RelayDriver.cs`.**

   In the main stage loop (`src/VisualRelay.Core/Execution/RelayDriver.cs`), after the
   existing stage 9 block (around line 191) — or preferably as a dedicated `if
   (stage.Number == 7)` block in the same loop — add:

   ```csharp
   if (stage.Number == 7 && config.ReviewEscalationEnabled)
   {
       // The above run used the balanced tier (stage.Tier will be changed; see step 4).
       // Check if escalation is needed.
       if (TryParseContractJson(body, out var reviewJson, out _)
           && ReviewEscalationPolicy.ShouldEscalate(
               reviewJson, manifest, rootPath,
               config.ReviewEscalationManifestFileThreshold,
               config.ReviewEscalationManifestLineThreshold))
       {
           // Re-run on frontier.  Build a new invocation with Tier overridden.
           var escalatedInvocation = BuildInvocation(
               rootPath, runId, taskId, taskDirectory, config, stage, input,
               ledger, manifest, pinnedSwivalProfileContent: pinnedSwivalProfileContent)
               with { Tier = stage.Tier }; // stage.Tier is still "frontier" from RelayStages
           var escalatedResult = await _dependencies.SubagentRunner.RunAsync(escalatedInvocation, cancellationToken);
           var escalatedCost = TryEstimateCost(escalatedInvocation.ReportFile);
           if (escalatedCost is not null) sessionCostUsd += escalatedCost.CostUsd;
           else unknownCostStageCount++;

           if (!escalatedResult.IsValid || string.IsNullOrWhiteSpace(escalatedResult.Json))
               return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number,
                   escalatedResult.Error ?? "invalid frontier review result",
                   escalatedResult.RawText, statusEntries, cancellationToken);

           // Promote the frontier result as the authoritative stage body.
           body = escalatedResult.Json;
           cost = escalatedCost; // update cost for RecordStageAsync / status
       }
   }
   ```

   Note: `BuildInvocation` produces an invocation where `Tier = stage.Tier` (currently
   "frontier" in `RelayStages.cs`).  In step 4 below, stage 7 is changed to "balanced"
   in `RelayStages.cs`, so the first run uses balanced and the `with { Tier = stage.Tier
   }` trick no longer works.  Use an explicit `with { Tier = "frontier" }` for the
   escalated call, or a named constant `RelayTiers.Frontier = "frontier"` to avoid
   stringing.

4. **Change `RelayStages.cs` stage 7 tier from `"frontier"` to `"balanced"`.**

   `src/VisualRelay.Core/Execution/RelayStages.cs:15`:
   ```csharp
   // Before:
   Stage(7, "Review", "frontier", "some", "all", """{ "verdict": "pass"|"changes", "issues": [] }"""),
   // After:
   Stage(7, "Review", "balanced", "some", "all", """{ "verdict": "pass"|"changes", "issues": [] }"""),
   ```

   The escalation logic in step 3 then explicitly uses `"frontier"` for the escalated
   call.  When `ReviewEscalationEnabled = false`, stage 7 runs on `balanced` with no
   escalation — callers who want guaranteed frontier can set this flag false and override
   the tier via `TierProfiles["balanced"] = "frontier"` or by disabling escalation.

   **Note on `FirstOutputTimeoutMsByTier`**: the balanced tier uses 120 000 ms
   first-output timeout vs frontier's 660 000 ms.  No changes needed — the timeout is
   looked up from `invocation.Tier` in the runner, so the first (balanced) run
   automatically uses the balanced window and the escalated (frontier) run uses frontier's
   window.

5. **Wire `RelayConfigWriter` if it generates default configs.**

   `src/VisualRelay.Core/Init/RelayConfigWriter.cs` — if it writes a skeleton
   `config.json`, optionally add the new fields as commented-out examples.  Only if the
   writer already emits optional fields; otherwise skip.

6. **Tests (TDD — write these first).**

   All in `tests/VisualRelay.Tests/`:

   a. **`ReviewEscalationPolicyTests.cs`** — unit tests for the pure helper:
      - `ShouldEscalate_VerdictChanges_ReturnsTrue`
      - `ShouldEscalate_NonEmptyIssues_ReturnsTrue`
      - `ShouldEscalate_VerdictPassEmptyIssuesSmallManifest_ReturnsFalse`
      - `ShouldEscalate_ManifestExceedsFileThreshold_ReturnsTrue`
      - `ShouldEscalate_ManifestExceedsLineThreshold_ReturnsTrue`
      - `ShouldEscalate_ThresholdsDisabled_DoesNotEscalateOnSize`

   b. **`RelayConfigLoaderTests.cs` extension** (or a new
      `RelayConfigLoaderReviewEscalationTests.cs`) — verify:
      - Default values (`ReviewEscalationEnabled = true`, file threshold = 10, line threshold = 500).
      - Parsing `"reviewEscalationEnabled": false` from JSON.
      - Parsing custom thresholds.

   c. **`RelayDriverTests.ReviewEscalation.cs`** — integration tests using
      `CapturingSubagentRunner` to assert on the tier of each stage 7 invocation:
      - `Review_PassVerdict_SmallManifest_RunsOnBalancedOnly` — balanced Review returns
        pass, manifest < threshold → only one stage-7 invocation, `Tier = "balanced"`.
      - `Review_FailVerdict_EscalatesToFrontier` — balanced Review returns
        `{"verdict":"changes","issues":["x"]}` → two stage-7 invocations:
        first `Tier = "balanced"`, second `Tier = "frontier"`.
      - `Review_PassVerdict_LargeManifest_EscalatesToFrontier` — balanced pass but
        manifest count > file threshold → two invocations, second is frontier.
      - `Review_EscalationDisabled_AlwaysRunsOnConfiguredTier` — when
        `ReviewEscalationEnabled = false`, stage 7 runs once on whatever tier
        `RelayStages` specifies (balanced after the change in step 4), no escalation.
      - `Review_FrontierResultIsUsedInLedger` — after escalation, the body recorded to
        the ledger and seals is from the frontier run, not the balanced run.

      Use a `CapturingSubagentRunner` subclass or wrapper that can return different
      results for the first vs second stage-7 call.

## Done when

- **Balanced-first path verified:** a task with a clean diff (balanced Review returns
  `{"verdict":"pass","issues":[]}`, manifest ≤ 10 files, ≤ 500 lines) produces exactly
  one stage-7 subagent call with `Tier = "balanced"` — asserted by the
  `CapturingSubagentRunner` test.  No frontier call is made.
- **Escalation on signal:** a balanced Review returning `verdict = "changes"` or
  non-empty `issues` produces a second stage-7 call with `Tier = "frontier"`.  The
  frontier result is what appears in the ledger and seals.
- **Escalation on size heuristic:** a manifest exceeding the file-count or line-count
  threshold escalates even on a balanced "pass" verdict.
- **Configurable:** `reviewEscalationEnabled`, `reviewEscalationManifestFileThreshold`,
  and `reviewEscalationManifestLineThreshold` are read from `.relay/config.json` and
  default to `true`, `10`, `500` when absent — asserted by config-loader tests.
- **Cost reduction on clean diffs:** balanced tier is $0.435/$0.87 per 1M tokens vs
  frontier's $0.95/$4.00; a clean-diff Review on balanced costs roughly 4–5× less than
  frontier for the same prompt.  No new cost tracking code is required — the existing
  `TryEstimateCost` path captures each invocation's cost from its report file.
- **No quality regression:** when the balanced Review flags issues OR the heuristic
  trips, frontier runs and its result is authoritative — the stage-8 Fix agent sees
  frontier-quality analysis in the ledger.
- **All tests pass:** `ReviewEscalationPolicyTests`, the config loader additions, and
  the `RelayDriverTests.ReviewEscalation` suite are green.  The existing
  `ScriptedSubagentRunner` (stage 7 returns `{"verdict":"pass","issues":[]}`) continues
  to work because a small test manifest stays under the default file threshold.
- **`./visual-relay check` is green** — all pre-existing tests pass unmodified.
- **Files stay under 300 lines each.**  `ReviewEscalationPolicy.cs` should be under 60
  lines; the driver changes are a single `if` block; the policy tests are a single file.
- **Conventional Commit subject candidates:**
  - `feat(driver): escalate Review to frontier only when balanced flags issues`
  - `perf(review): run stage 7 on balanced tier, escalate to frontier on signal`
  - `feat(harness): balanced-first review with frontier escalation on signal or risk`

## Uncertainty / open questions

- **`BuildInvocation` produces the effective tier from `stage.Tier`** after the step-4
  change to "balanced".  The escalated call needs an explicit `with { Tier = "frontier"
  }` on the returned `StageInvocation`; double-check that no other field of the
  invocation (e.g. trace directory path, report file path) needs to differ between the
  balanced and frontier runs.  The attempt counter `RelayAttempt.Next` increments per
  unique trace directory; two calls to `BuildInvocation` for stage 7 will produce
  `stage7-attempt1` and `stage7-attempt2` — that is correct behaviour and costs are
  tracked separately, but the ledger should only record the frontier result to avoid
  confusing the stage-8 Fix agent with a stale balanced body.
- **Only the frontier result goes into the ledger.** The balanced call should be
  recorded only as a cost entry (via `TryEstimateCost`) and an event
  (`stage_start`/`stage_done` pair or a new `review_escalated` event), not as a ledger
  section.  Otherwise stage 8 might conflate the two reviews.  Decide whether to emit a
  lightweight info event (`"review_escalated"`) on escalation so operators can observe
  it in the event log.
- **`ScriptedSubagentRunner` in existing tests always returns pass for stage 7.**
  After step 4 changes the tier to "balanced", the happy-path integration tests will
  have stage 7 invoking with `Tier = "balanced"` and manifest below threshold — no
  escalation, no change in behaviour.  Confirm by reading `ScriptedSubagentRunner`
  (`tests/VisualRelay.Tests/SubagentRunnerTestDoubles.cs:64`): stage 7 returns
  `{"verdict":"pass","issues":[]}`.  Manifest in those tests is two files
  (`src/status.cs`, `tests/status.tests.cs`), well below the 10-file threshold.
  No existing test changes expected.
