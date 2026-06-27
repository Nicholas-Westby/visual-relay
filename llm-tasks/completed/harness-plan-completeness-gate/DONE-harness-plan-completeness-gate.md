# Harness: Plan-completeness gate — cross-check manifest against task deliverables after stage 4

On task `10-adopt-inspectcode-standards-repo-wide` (2026-06-13), stage 4 (Plan) produced
a manifest and narrative that covered only a narrow sub-scope of the task's stated deliverables.
Stages 5–8 faithfully executed that sub-scope. Stage 7 (Review) later caught the gap and stage
8 (Fix) rebuilt the missing deliverables — but that rescue cost approximately 55% of the entire
run budget. The driver had no hook to reject a Plan that silently dropped major deliverables
before proceeding to Author-tests.

## Current state (researched)

### Where stage-4 output is accepted today

`src/VisualRelay.Core/Execution/RelayDriver.cs:110–134`: after stage 4's `TryParseContractJson`
succeeds, the driver reads `manifest`, filters task-dir entries, writes `manifest.txt` and
advances to stage 5. There is no step that cross-checks the Plan's `plan` narrative or `manifest`
against the task's "Done when" / deliverables section.

The stage-4 contract is:
```
{ "plan": string, "manifest": string[] }
```
(`RelayStages.cs:12`). The full task text (`input.Markdown`) is already passed into every
`BuildInvocation` call (`RelayDriver.VerifyFix.cs:243`, `StageInvocation.TaskInput`).

### Corrective-retry mechanism that already exists

`SwivalSubagentRunner` runs up to `config.MaxContractRetries` corrective retries when a
stage's JSON block is missing or shape-invalid (`ProcessRunners.cs`, contract retry loop).
The same corrective-retry path (re-invoking the stage with a specific error message) is
available to the driver for semantic rejections — the precedent is `CheckManifestAgainstGitignoreAsync`
(`ProcessRunners.ManifestValidation.cs:14`) which returns an error string that feeds back into
the contract retry.

### Analogous gate: AuthorTestGate

`src/VisualRelay.Core/Execution/AuthorTestGate.cs`: a small static class with a single
`RunAsync` method, called from the stage-5 block in `RelayDriver.cs:144–184`. This is the
natural template for `PlanCompletenessGate`.

### No overlap with existing DONE tasks

`DONE-validate-manifest-against-gitignore-at-acceptance.md` validates that manifest paths are
not gitignored; it does not check content coverage against task deliverables.
`DONE-verify-enforce-repo-guards.md` concerns stage-9 guard checks. Neither task touches
Plan-level semantic coverage.

## What to build

Write the failing test first. All logic is in the VR harness and is language-agnostic —
the coverage check is purely textual.

### 1. Add `PlanCompletenessGate` class

Create `src/VisualRelay.Core/Execution/PlanCompletenessGate.cs` (target: <80 lines):

```csharp
internal static class PlanCompletenessGate
{
    /// <summary>
    /// Checks whether the stage-4 Plan's narrative and manifest appear to cover
    /// the task's declared deliverables / "Done when" checklist.  Returns a
    /// non-null corrective error message when coverage is insufficient; null when
    /// the plan looks complete or no checklist is detectable.
    /// </summary>
    internal static string? CheckCoverage(
        string planNarrative,
        IReadOnlyList<string> manifest,
        string taskMarkdown)
    {
        // Extract the checklist: lines that start with "- " or "* " under a
        // "## Done when" or "## Deliverables" heading (case-insensitive).
        // If no such heading exists, return null (degrade gracefully — don't block).
        var checklist = ExtractChecklist(taskMarkdown);
        if (checklist.Count == 0)
            return null;

        // Build a combined coverage corpus from the plan text and manifest paths.
        var corpus = (planNarrative + "\n" + string.Join("\n", manifest)).ToUpperInvariant();

        // Identify checklist items whose key noun phrase does not appear in the
        // corpus.  Heuristic: extract the first 5+ character token per item.
        var uncovered = checklist
            .Where(item => !KeyTokensOf(item).Any(tok => corpus.Contains(tok, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (uncovered.Count == 0)
            return null;

        var bullets = string.Join("\n", uncovered.Select(u => $"- {u}"));
        return
            $"Plan completeness check: the following deliverables from the task's checklist " +
            $"do not appear to be covered by the plan narrative or manifest:\n{bullets}\n\n" +
            "Please revise the plan to address all stated deliverables, then re-emit the " +
            "complete JSON contract.";
    }

    private static IReadOnlyList<string> ExtractChecklist(string markdown)
    {
        var items = new List<string>();
        var inSection = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.OrdinalIgnoreCase))
            {
                var heading = line[3..].Trim().ToUpperInvariant();
                inSection = heading.StartsWith("DONE WHEN", StringComparison.Ordinal)
                         || heading.StartsWith("DELIVERABLE", StringComparison.Ordinal);
                continue;
            }
            if (inSection && (line.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                           || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)))
            {
                var text = line.TrimStart()[2..].Trim();
                if (text.Length > 0)
                    items.Add(text);
            }
            else if (inSection && line.StartsWith("#", StringComparison.Ordinal))
            {
                inSection = false; // next heading ends the section
            }
        }
        return items;
    }

    private static IEnumerable<string> KeyTokensOf(string item)
    {
        // Split on whitespace/punctuation; return tokens of 5+ chars.
        return item
            .Split([' ', '\t', '(', ')', ',', '.', ':', ';', '"', '\''],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 5);
    }
}
```

Keep `ExtractChecklist` and `KeyTokensOf` internal and pure (no I/O) so they are trivially
unit-testable.

### 2. Wire the gate into the stage-4 block in `RelayDriver.cs`

In `src/VisualRelay.Core/Execution/RelayDriver.cs`, after `await WriteManifestAsync(...)` (line
133) and before the stage loop continues, add a completeness check with one corrective Plan
retry:

```csharp
if (stage.Number == 4)
{
    // ... existing manifest parsing and WriteManifestAsync ...

    // Plan-completeness gate: cross-check Plan narrative + manifest against task
    // deliverables.  One corrective retry is attempted on coverage failure.
    if (ReadOptionalString(json, "plan") is { } planNarrative)
    {
        var coverageError = PlanCompletenessGate.CheckCoverage(planNarrative, manifest, input.Markdown);
        if (coverageError is not null)
        {
            // One corrective retry using the existing invocation (new attempt slot).
            var retryInvocation = BuildInvocation(rootPath, runId, taskId, taskDirectory,
                config, stage, input with { Markdown = input.Markdown },
                ledger, manifest, pinnedSwivalProfileContent: pinnedSwivalProfileContent);
            // Inject the coverage error as a "last test output" equivalent via
            // BuildCorrectivePrompt (reuse the contract-retry flow).
            var retryResult = await _dependencies.SubagentRunner.RunAsync(
                retryInvocation with { LastTestOutput = coverageError }, cancellationToken);
            // ... cost accounting and validity check as in the main stage loop ...
            if (retryResult.IsValid && !string.IsNullOrWhiteSpace(retryResult.Json)
                && TryParseContractJson(retryResult.Json, out var retryJson, out _))
            {
                // Accept the retry's manifest.
                manifest.Clear();
                var retryRaw = ReadStringArray(retryJson, "manifest")
                    .Distinct(StringComparer.Ordinal)
                    .Where(e => !IsPathUnderDirectory(rootPath, e, config.TasksDir))
                    .ToList();
                manifest.AddRange(retryRaw);
                targetedTestCommand = BuildTargetedTestCommand(config, manifest);
                await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
                body = retryResult.Json;
            }
            // If the retry also fails validation or coverage, proceed anyway —
            // the gate is advisory; one retry is the limit.
        }
    }
}
```

The exact integration point depends on the current driver's local variable layout; the
implementer should read `RelayDriver.cs:110–134` and place the call immediately after
`WriteManifestAsync` but before advancing the loop. Reuse existing helpers (`ReadOptionalString`,
`TryParseContractJson`, `IsPathUnderDirectory`, `BuildTargetedTestCommand`,
`WriteManifestAsync`) without introducing new infrastructure.

**Graceful degradation**: if `input.Markdown` has no "Done when" or "Deliverables" heading,
`ExtractChecklist` returns an empty list and `CheckCoverage` returns null. The gate is a
no-op for tasks that lack an explicit checklist. Tasks that use the standard spec format
(all new harness tasks do) are checked.

### 3. Tests (TDD — write first)

All in `tests/VisualRelay.Tests/`.

**`PlanCompletenessGateTests.cs`** (pure unit tests, no I/O):

- `CheckCoverage_NoDeliverableSection_ReturnsNull` — task markdown with no `## Done when` heading
  → returns null (gate is skipped).
- `CheckCoverage_AllDeliverablesCovered_ReturnsNull` — checklist items whose key tokens all
  appear in planNarrative → null.
- `CheckCoverage_PartialCoverage_ReturnsCorrectionMessage` — one checklist item absent from
  plan + manifest → non-null message naming the uncovered item.
- `CheckCoverage_EmptyManifest_UncoveredDeliverable_ReturnsCorrectionMessage` — empty manifest
  + uncovered item → correction message.
- `CheckCoverage_CaseInsensitiveMatch_Passes` — token match is case-insensitive.
- `CheckCoverage_ShortTokensIgnored_DoesNotFalseNegative` — checklist items whose only tokens
  are <5 chars (e.g. "red") do not generate uncovered errors.

**`RelayDriverPlanCompletenessTests.cs`** (driver integration via `ScriptedSubagentRunner`):

- `Stage4_CompletePlan_ProceedsToStage5WithoutRetry` — stage 4 returns a plan that covers all
  task deliverables → one stage-4 invocation, stage 5 runs.
- `Stage4_IncompletePlan_TriggersOneRetry` — stage 4 returns a plan missing deliverables → two
  stage-4 invocations (original + retry), stage 5 runs after.
- `Stage4_RetryAlsoIncompletePlan_ProceedsAfterOneRetry` — even if the retry's plan is also
  incomplete, the driver proceeds (advisory, not blocking).
- `Stage4_NoDeliverableHeading_NeverRetries` — task with no `## Done when` section → one
  stage-4 invocation only.

## Done when

- **Gate is pure and testable:** `PlanCompletenessGateTests` covers all cases above and passes;
  the class has no I/O dependencies.
- **Corrective retry fires on coverage gap:** `RelayDriverPlanCompletenessTests` confirms that
  a plan missing deliverables triggers exactly one additional stage-4 invocation with the
  corrective error message in `LastTestOutput`.
- **Graceful degradation:** tasks without `## Done when` or `## Deliverables` sections never
  trigger a retry; existing `ScriptedSubagentRunner`-based driver tests are unaffected (they use
  task stubs with no such heading).
- **One retry max:** even if the retry's plan is also incomplete, the driver proceeds — the gate
  is advisory.
- **`./visual-relay check` green** after the change.
- **Files under 300 lines each:**
  - `src/VisualRelay.Core/Execution/PlanCompletenessGate.cs` (new, <80 lines)
  - `RelayDriver.cs` stage-4 block extended by ~20 lines
  - `tests/VisualRelay.Tests/PlanCompletenessGateTests.cs` (new, <100 lines)
  - `tests/VisualRelay.Tests/RelayDriverPlanCompletenessTests.cs` (new, <120 lines)
- **Conventional Commit subject candidates:**
  - `feat(driver): plan-completeness gate retries stage 4 when deliverables appear uncovered`
  - `feat(harness): cross-check Plan manifest against task checklist before Author-tests`
