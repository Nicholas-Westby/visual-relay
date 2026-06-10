## Stage 1 - Ideate

{
  "summary": "Add corrective contract retry to SwivalSubagentRunner by extending the existing stall-retry loop to cover contract failures (null extractor or shape mismatch), with a maxContractRetries config (default 1) and contract_retry events. Add shape validation to the extractor's output so array-root and missing-key failures route through the same null path. Mirror the maxStallRetries loading pattern in RelayConfigLoader.",
  "options": [
    "Option A — Inline retry in SwivalSubagentRunner: extend the existing stall-retry loop to also handle contract failures, re-spawning the subagent on the same tier with corrective instruction; simplest diff, single failure path.",
    "Option B — Corrective retry via RelayDriver: return a distinct ContractFailure status from the runner, let the driver handle retry logic (like the verify-fix loop); cleaner separation but more plumbing.",
    "Option C — Composite: runner does one immediate same-tier retry inline, driver validates shape and routes shape mismatches back for retry with precise instruction; best layering but shared mutable counter across components."
  ]
}

## Stage 2 - Research

{ "findings": "Extractor robustness (marker walk last-to-first, array-root rejection) is DONE in 1f87e9d. The runner returns straight out of the retry loop on contract failure (ProcessRunners.cs:152-154) with no corrective retry. The driver assumes JSON root is Object, risking unhandled InvalidOperationException on array roots (observed). Config loading for MaxStallRetries provides a direct pattern to mirror for MaxContractRetries. RelayEvent already carries Attempt/StageNumber/Tier for contract_retry events. Test doubles (ScriptedSubagentRunner, CapturingSubagentRunner) can be extended to simulate contract failures.", "constraints": ["Do NOT modify FencedJsonExtractor.cs or its tests (already fixed in 1f87e9d).", "Add MaxContractRetries to RelayConfig (default 1), RelayConfigLoader.Defaults(), and load via OptionalInt.", "Extend the existing stall-retry loop in SwivalSubagentRunner.RunAsync so contract failures (null JSON after zero-exit) get corrective retries on same tier, iterating the loop (continue) instead of returning.", "Corrective prompt must include prior output + instruction to reply with ONLY the fenced JSON block (derive from prior work).", "Emit contract_retry event (info level) per corrective attempt using RelayEvent's Attempt/StageNumber/Tier fields.", "After exhausting same-tier corrections, try once on next-higher tier, then flag with existing hint.", "Add shape validation in RelayDriver.cs after JSON extraction: verify Object root and required keys; route mismatches through same null-JSON path (no unhandled exceptions).", "maxContractRetries: 0 must preserve today's fail-fast behavior.", "Tests required: (a) corrective retry succeeds with contract_retry event, (b) corrective prompt contains prior output + only-block instruction, (c) exhaustion still flags cleanly, (d) maxContractRetries:0 preserves fail-fast, (e) array-root gets corrective retry then flags cleanly."] }

## Stage 3 - Diagnose

{ "evidence": "The runner's retry loop (ProcessRunners.cs:81-155) handles three failure modes after swival exits zero: (a) watchdog-fired first-output stall → `continue` at line 117-118 for up to `MaxStallRetries` retries, (b) timeout → return with hint, (c) contract failure (null extractor output) → immediate return at lines 152-154 with `ErrorHintClassifier.WithHint(\"no valid fenced json block\")`. Mode (c) bypasses the retry mechanism entirely — the loop is never re-entered, the subagent is never re-invoked with corrective instructions, no tier escalation occurs. This is in contrast to mode (a), which gets `maxStallRetries` attempts via `continue` within the same loop. The driver (RelayDriver.cs:216-219) then receives `!result.IsValid` and calls `FlagAsync` immediately, killing the task. There is no `maxContractRetries` configuration anywhere in RelayConfig or RelayConfigLoader to bound or tune this behavior. The extractor (FencedJsonExtractor.cs) was already hardened in 1f87e9d to walk markers last-to-first and reject array-root JSON (line 62: `parsed.RootElement.ValueKind == JsonValueKind.Object ? json : null`), but the driver at line 222 still calls `JsonDocument.Parse(result.Json).RootElement.Clone()` and passes it directly to `ReadStringArray`/`ReadOptionalString` without shape validation — any malformed object (wrong keys, missing required fields) silently returns empty data instead of triggering the corrective-retry path. RelayEvent already carries `Attempt`, `StageNumber`, and `Tier` fields, ready for `contract_retry` event emission. The test double `ScriptedSubagentRunner` returns canned fenced-JSON results; `CapturingSubagentRunner` records invocations for assertion — both can be extended for contract-failure simulation.",
  "excerpts": [
    "// ProcessRunners.cs:79 — loop bounded by stall retries only; no contract-retry counter\nvar maxAttempts = _config.MaxStallRetries + 1;\n\nfor (var attempt = startAttempt; attempt < startAttempt + maxAttempts; attempt++)",

    "// ProcessRunners.cs:115-118 — watchdog stall → `continue` (retries within loop)\nif (watchdogFired)\n{\n    await processTask;\n    if (attempt < startAttempt + maxAttempts - 1)\n        continue;",

    "// ProcessRunners.cs:152-154 — contract failure → immediate `return` (no retry, no tier escalation)\nvar json = FencedJsonExtractor.Extract(result.Output);\nvar error = json is null ? ErrorHintClassifier.WithHint(\"no valid fenced json block\") : null;\nreturn new SubagentResult(result.Output, json, json is not null, error);",

    "// RelayDriver.cs:216-219 — driver flags instantly on invalid/missing JSON, no corrective path\nif (!result.IsValid || string.IsNullOrWhiteSpace(result.Json))\n{\n    return await FlagAsync(rootPath, runId, taskId, taskDirectory, stage.Number, result.Error ?? \"invalid subagent result\", result.RawText, statusEntries, cancellationToken);\n}",

    "// RelayDriver.cs:222 — assumes Object root; no shape validation before consuming properties\nvar json = JsonDocument.Parse(result.Json).RootElement.Clone();\nif (stage.Number == 4)\n{\n    manifest.Clear();\n    var raw = ReadStringArray(json, \"manifest\")...",

    "// RelayConfig.cs:27 — MaxStallRetries exists; no MaxContractRetries\nint MaxStallRetries,",

    "// RelayConfigLoader.cs:36/123 — MaxStallRetries loading pattern (mirror for MaxContractRetries)\nMaxStallRetries: 2,\n...\nMaxStallRetries = OptionalInt(root, \"maxStallRetries\", defaults.MaxStallRetries),",

    "// RelayEvent.cs:3-13 — Attempt/StageNumber/Tier fields ready for contract_retry events\npublic sealed record RelayEvent(\n    DateTimeOffset Timestamp,\n    string Level,\n    string EventName,\n    string RunId,\n    string RootPath,\n    string? TaskId = null,\n    int? StageNumber = null,\n    string? Tier = null,\n    int? Attempt = null,\n    IReadOnlyDictionary<string, string>? Data = null)",

    "// FencedJsonExtractor.cs:62 — already rejects array-root (1f87e9d), but driver still lacks shape defense\nusing var parsed = JsonDocument.Parse(json);\nreturn parsed.RootElement.ValueKind == JsonValueKind.Object ? json : null;",

    "// SubagentRunnerTestDoubles.cs:7-76 — ScriptedSubagentRunner returns canned fenced JSON; test infra\n// SubagentRunnerTestDoubles.cs:85-99 — CapturingSubagentRunner records invocations for prompt assertions"
  ],
  "repro": "1. Configure a relay with a cheap-tier model. 2. Run any task where the subagent finishes with exit 0 but its output either (a) lacks a ```json fence entirely, or (b) has a fenced block whose root is a JSON array rather than an object. 3. Observe: the runner at ProcessRunners.cs:152-154 returns SubagentResult with IsValid=false and error hint \"no valid fenced json block\". The driver at RelayDriver.cs:216-218 flags the task instantly — no corrective retry, no tier escalation. The task dies after a single failed stage despite the model having done useful work. 4. Contrast with a first-output stall: the watchdog at ProcessRunners.cs:117-118 fires `continue`, the loop re-spawns swival on the same tier up to MaxStallRetries times, and the task only flags after exhaustion. The contract-failure path has no equivalent resilience. 5. For array-root specifically (before 1f87e9d fix): the extractor returned the array, the driver hit line 222 with JsonDocument.Parse, and the subsequent property access on a non-object threw InvalidOperationException caught by the top-level catch at RelayDriver.cs:465-468, producing an `exception:` flag at stage 0 — even less informative than the contract hint." }

## Stage 4 - Plan

{
  "plan": "## Changes\n\n### 1. `src/VisualRelay.Domain/RelayConfig.cs` — Add MaxContractRetries\nAdd `int MaxContractRetries = 1` parameter after `int MaxStallRetries`.\n\n### 2. `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` — Load MaxContractRetries\n- In `Defaults()`: add `MaxContractRetries: 1` after `MaxStallRetries: 2`\n- In `TryLoadAsync()`: add `MaxContractRetries = OptionalInt(root, \"maxContractRetries\", defaults.MaxContractRetries)` after the `MaxStallRetries` line\n\n### 3. `src/VisualRelay.Core/Execution/ProcessRunners.cs` — Corrective contract retry\nRestructure the stall-retry `for` loop (lines 79–155) into a `while` loop that tracks two independent counters: `stallRetriesLeft` and `contractRetriesLeft`. On contract failure (null JSON after exit 0):\n- If `contractRetriesLeft > 0`: decrement, emit `contract_retry` event via `_eventSink`, set corrective prompt, increment attempt number, `continue`.\n- After exhausting same-tier corrections: try ONE escalation to next-higher tier (cheap→balanced→frontier) via a `NextTier()` helper, then `continue`.\n- If `MaxContractRetries == 0`: fail fast (preserve today's behavior).\n- Exhausted: return with `ErrorHintClassifier.WithHint(\"no valid fenced json block\")`.\n\nAdd `BuildCorrectivePrompt(StageInvocation, string priorOutput)` method: embeds prior output and instructs the model to reply with ONLY the fenced JSON block derived from that output. Uses the stage's `OutputContract` as the expected schema.\n\nAdd `NextTier(string tier)` helper returning the escalation target or null.\n\nAdd shape validation inside the extraction path: after `FencedJsonExtractor.Extract`, also verify the parsed JSON has all required keys from the stage contract (parse quoted keys from `OutputContract` with a regex `\"(\\w+)\"\\s*:`, excluding optional `?`-suffixed keys). Missing keys → treat as null JSON → corrective retry with message \"root must be a JSON object with keys: …\".\n\n### 4. `src/VisualRelay.Core/Execution/RelayDriver.cs` — Defensive JSON parsing\nAt line 222, replace the direct `JsonDocument.Parse(result.Json).RootElement.Clone()` with a `TryParseContractJson` helper that:\n- Parses the JSON inside try/catch\n- Verifies root is `JsonValueKind.Object`\n- Returns false with an error message on any failure\n- On failure, flags cleanly via `FlagAsync` (no unhandled `InvalidOperationException`)\n\n### 5. `tests/VisualRelay.Tests/SwivalSubagentRunnerTests.cs` — Contract retry tests\nAdd tests using the fake-shell-script pattern (like the watchdog tests):\n- **(a)** First run outputs text without a fenced JSON block → corrective second run outputs valid block → `IsValid=true`, one `contract_retry` event in sink\n- **(b)** Corrective prompt contains the prior output and the \"reply with ONLY that block\" instruction (fake script writes last arg to file, test inspects it)\n- **(c)** All contract retries exhausted → `IsValid=false` with \"no valid fenced json block\"\n- **(d)** `MaxContractRetries: 0` → first contract failure returns immediately (fail-fast)\n\n### 6. `tests/VisualRelay.Tests/RelayDriverTests.cs` — Shape/array-root flagging\nAdd test: a custom `ISubagentRunner` returns `SubagentResult` with `Json=\"[1,2,3]\"` and `IsValid=true` (simulating a bug where non-object JSON reaches the driver). The driver must flag cleanly with a shape-descriptive message — no `exception:` in NEEDS-REVIEW.",
  "manifest": [
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerTests.cs",
    "tests/VisualRelay.Tests/RelayDriverTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/SwivalSubagentRunnerContractRetryTests.cs",
    "tests/VisualRelay.Tests/RelayDriverTests.cs",
    "tests/VisualRelay.Tests/SubagentRunnerTestDoubles.cs"
  ],
  "rationale": "Contract retry tests use the fake-shell-script pattern from the watchdog tests. Four tests cover: (a) corrective retry recovers with contract_retry event, (b) corrective prompt embeds prior output and 'reply with ONLY' instruction, (c) exhaustion flags with 'no valid fenced json block', (d) MaxContractRetries:0 preserves fail-fast. All four fail to compile because MaxContractRetries doesn't exist on RelayConfig yet. The array-root driver test adds a defensive shape-validation case: ArrayRootSubagentRunner returns IsValid=true with Json='[1,2,3]' (bypassing the extractor's object-root guard), and the test asserts the driver flags cleanly without an 'exception:' NEEDS-REVIEW. This test compiles but fails at runtime — the current driver throws InvalidOperationException which produces exactly the 'exception:' message the test rejects."
}

## Stage 6 - Implement

{"summary": "Added corrective contract retry to SwivalSubagentRunner: independent contract retry counter (MaxContractRetries, default 1), contract_retry events, tier escalation after exhaustion, shape validation via regex key extraction, defensive JSON parsing in RelayDriver, and fail-fast preservation at MaxContractRetries=0. All 503 tests pass."}

## Stage 7 - Review

{ "verdict": "changes", "issues": ["The corrective prompt in BuildCorrectivePrompt always says the block is missing, even when ValidateContractShape detected a specific shape mismatch; the spec requires the prompt to state the actual shape problem (e.g., missing required keys) so the model can fix it.","TryParseContractJson in RelayDriver.Artifacts.cs creates a JsonDocument but never disposes it, leaking pooled memory; it should use 'using var doc = JsonDocument.Parse(json)' since RootElement.Clone() copies the data."] }

## Stage 8 - Fix

{ "summary": "Resolved both review issues: (1) BuildCorrectivePrompt now accepts an optional shapeError so corrective retries tell the model the exact shape problem (missing keys, wrong root type) instead of always claiming the block is missing; (2) TryParseContractJson uses 'using var doc' to dispose the JsonDocument after RootElement.Clone(). All 503 tests pass." }

## Stage 9 - Verify

{
  "summary": "Stage 9 verify passes: all 503 tests green. The implementation adds corrective contract retry to SwivalSubagentRunner (independent contractRetriesLeft counter, shape validation via ValidateContractShape, corrective prompt with optional shapeError, tier escalation after exhaustion, MaxContractRetries:0 fail-fast), loads MaxContractRetries from config (default 1, mirroring MaxStallRetries pattern), emits contract_retry events, and adds defensive TryParseContractJson in RelayDriver so array-root or malformed JSON flags cleanly instead of throwing. The extractor half was already completed in 1f87e9d (marker walk last-to-first).",
  "commitMessages": [
    "feat(relay): add corrective contract retry with shape validation and tier escalation",
    "fix: harden RelayDriver against non-object contract JSON with defensive TryParseContractJson",
    "Add MaxContractRetries config (default 1) with contract_retry events and fail-fast at zero"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Stage 10 fix-verify passes: all 503 tests green. The verify stage (stage 9) was already green — no pinned failures exist to fix. The implementation is complete and correct: MaxContractRetries config (default 1, fail-fast at 0), corrective contract retry loop with independent contractRetriesLeft counter, shape validation via ValidateContractShape regex key extraction, corrective prompt with optional shapeError for precise retry guidance, tier escalation (cheap→balanced→frontier) after exhaustion, contract_retry events via RelayEvent, and defensive TryParseContractJson in RelayDriver ensuring array-root or malformed JSON flags cleanly with a descriptive message instead of throwing InvalidOperationException. The extractor half (FencedJsonExtractor marker walk last-to-first, object-root guard) was completed in 1f87e9d. The exact verify command exits 0 with 503 passed, 0 failed, 0 skipped. Note: the bare config command requires -p:NuGetAudit=false in this offline sandbox because the .NET 10 SDK NuGet vulnerability audit cannot reach api.nuget.org; this is an environment constraint, not a code defect."
}

## Stage 11 - Commit

Committed by Visual Relay.

