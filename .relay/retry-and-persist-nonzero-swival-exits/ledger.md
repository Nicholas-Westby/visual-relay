## Stage 1 - Ideate

{
  "summary": "The swival runner's nonzero-exit branch (spawned process exits 1 without a valid report) is a fail-fast path that truncates captured output to the first 600 chars via TrimForError, neither persisting the full output nor retrying the attempt. This loses diagnostic evidence (the real error is usually at the tail) and misses recovery opportunities on transient failures. The stall path already handles both persistence (stageN-attemptM.killed-output.txt, since commit 227b937) and bounded retries (MaxStallRetries). The fix routes the nonzero-exit branch into the existing stall-retry infrastructure: persist full output on nonzero exit, retry within the shared MaxStallRetries budget, and on exhaustion build the flag reason from the output tail + persisted path — keeping only genuine fail-fast cases (backend-not-ready, empty whitelist) unchanged.",
  "options": [
    "Option A — Direct branch routing (minimal change): In the runner loop's subprocess-wait block, when exit code != 0 and no report was produced, call the existing persist-helper to write killed-output.txt, then jump into the stall-retry decrement/continue logic (shared budget) instead of returning immediately. On retry exhaustion, build the reason from the last N hundred chars of captured output + file path. This reuses all stall-path machinery as-is with minimal new code — the only new branch is the goto-like early-exit redirect into the existing retry loop. Tests: a fake swival that exits 1 on first attempt and succeeds on second (asserts retry + persisted file + success), plus an always-exit-1 fake (asserts bounded retries, tail-not-head in reason).",
    "Option B — Unified failed-attempt handler: Extract a shared handle_failed_attempt(reason, output, attempt, ...) from both the stall path and the nonzero-exit path. Both call it, it persists output, decrements the shared budget, and either retries (by signalling the outer loop) or builds the tail-based reason + persisted-path for the flag. Cleaner separation but touches more lines — the stall path is refactored to also go through the handler, risking regression. Tests same as Option A plus a stall test routed through the new handler to ensure no behavioral change.",
    "Option C — Wrapped subprocess launcher: Wrap subprocess creation/wait in a helper that catches nonzero exits (no report), persists output internally, and raises a typed retryable exception. The outer retry loop already catches stall timeouts; this adds the new exception to the same catch set. Most decoupled from runner internals and easiest to unit-test in isolation, but adds indirection and may need to thread through the persist-helper reference differently than the stall path which does it inline."
  ]
}

## Stage 2 - Research

{
  "findings": "The stall path (ProcessRunners.RunAsync.cs:99–159) already persists full output via TryPersistKilledOutput and retries with stallRetriesLeft. The nonzero-exit branch (lines 179–183) does not — it returns immediately with only TrimForError (first 600 chars). Two pre-flight guards (backend readiness line 20, empty whitelist line 27) return before the loop and are unaffected. TryPersistKilledOutput (Helpers.cs:223–244) writes stageN-attemptM.killed-output.txt with a header. No tail-of-output helper exists today. Test infrastructure (SwivalTestHelpers.cs) creates fake bash scripts; the stall-persistence test in CpuPulse.cs:82–109 shows the pattern.",
  "constraints": [
    "Nonzero-exit retries must share the same stallRetriesLeft / MaxStallRetries budget as stall retries (combined bounded attempts).",
    "A TrimForTail (last N hundred chars) helper is needed — no such method exists today.",
    "TryPersistKilledOutput expects an ActivityWatchdog.Result parameter; the nonzero-exit path needs to either pass a synthetic result or factor out a simpler persist overload.",
    "Tests must use the same fake-bash-script pattern as existing watchdog tests (SwivalTestHelpers.WriteExecutableAsync, TestRepository.Create).",
    "Pre-flight fail-fast cases (backend readiness probe at line 20, empty whitelist at line 27) must remain unchanged.",
    "Solution must compile against existing RelayConfig, SubagentResult, StageInvocation, and SubagentResult types."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The diagnostic confirms a two-part defect in SwivalSubagentRunner.RunAsync (ProcessRunners.RunAsync.cs).\n\n**Root cause — nonzero-exit branch (lines 179–183):**\nWhen the spawned swival process exits nonzero without producing a valid report, the runner immediately returns with `TrimForError(result.Output)` — the FIRST 600 characters — as the flag reason. It never persists the full captured output (unlike the stall path at lines 108–111 which calls TryPersistKilledOutput) and never retries (unlike the stall path at lines 142–147 which decrements stallRetriesLeft and continues). The real error — usually at the TAIL after the sandbox startup banner — is lost.\n\n**TrimForError (Helpers.cs:206–209):**\nTakes the HEAD: `text.Length <= 600 ? text : string.Concat(text.AsSpan(0, 600), \"...\")`. For nono-wrapped runs the first 600 chars are always the nono startup banner, obscuring the actual error.\n\n**TryPersistKilledOutput (Helpers.cs:223–244):**\nOnly called from the stall path (line 110); expects an ActivityWatchdog.Result parameter the nonzero-exit path doesn't have. Writes stageN-attemptM.killed-output.txt with full captured stdout/stderr.\n\n**Incident 1 — fix-timing-estimates s8 (drive-jf2.log:26090):**\nRan ~3.5 min. Model emitted a `todo` tool call with malformed JSON args; swival injected corrective message \"Your previous tool call (todo) had malformed JSON arguments...\", then exited 1 five seconds later. Flag reason reads `swival exit 1: nono v0.62.0` — only the sandbox banner. Model-stochastic + swival recovery failure — a retry would very likely have succeeded. The real traceback was lost with the unpersisted output tail.\n\n**Incident 2 — drop-vestigial-kimi-suffix s8 (drive-v22.log:5346):**\nDied in ~10s. Flag reason reads `swival exit 1: nono v0.62.0` — only the sandbox banner. The actual error — a litellm 400 (invalid model `balanced` after the task's own profile edit renamed tiers) — is only recoverable from the swival report JSON preserved separately (stage8-attempt1.report.json:30). A retry would have refailed identically and flagging is correct, but the evidence loss is not.\n\n**The stall path already has the infrastructure (lines 99–159):**\nTryPersistKilledOutput persists full output, stallRetriesLeft is decremented, and when exhausted it builds a reason from phase/threshold/silence details (not just the head). The fix routes the nonzero-exit branch into this same machinery.",
  "excerpts": [
    "ProcessRunners.RunAsync.cs:179-183 — nonzero-exit branch: `if (result.ExitCode != 0) { var reason = $\"swival exit {result.ExitCode}: {TrimForError(result.Output)}\"; return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason)); }` — no persist, no retry, HEAD-only reason.",
    "ProcessRunners.Helpers.cs:206-209 — TrimForError: `var text = value.Trim(); return text.Length <= 600 ? text : string.Concat(text.AsSpan(0, 600), \"...\");` — takes first 600 chars (non-null prefix), not tail where errors sit.",
    "ProcessRunners.Helpers.cs:223-244 — TryPersistKilledOutput: writes stageN-attemptM.killed-output.txt with full captured output. Only called from stall path (line 110 of RunAsync.cs). Expects ActivityWatchdog.Result — nonzero-exit path would need a synthetic result or overload.",
    "ProcessRunners.RunAsync.cs:108-111 — stall path persist: `var killedOutputPath = TryPersistKilledOutput(traceDirParent, stageNum, attempt, wdResult, currentFirstOutputMs, currentInactivityMs, killedProcess.Output);`",
    "ProcessRunners.RunAsync.cs:142-147 — stall path retry: `else if (stallRetriesLeft > 0) { stallRetriesLeft--; attempt++; continue; }`",
    "drive-jf2.log:26089-26090 — fix-timing-estimates s8: last trace shows `UserText: 'Your previous tool call (todo) had malformed JSON arguments...'` then 5s later `flagged reason: swival exit 1: nono v0.62.0` — real error at tail, lost.",
    "drive-v22.log:5346,5352 — drop-vestigial-kimi-suffix s8: `flagged reason: swival exit 1: nono v0.62.0` — only banner, actual litellm 400 lost; real error only recoverable from separate swival report JSON.",
    "drop-vestigial-kimi-suffix stage8-attempt1.report.json:30 — swival internal report preserves: `error_message: 'LLM call failed: litellm.BadRequestError: OpenAIException - /chat/completions: Invalid model name passed in model=balanced'` — the evidence the runner's TrimForError discarded."
  ],
  "repro": "1. Observe ProcessRunners.RunAsync.cs:179-183 — nonzero exit returns immediately with TrimForError (first 600 chars). 2. Observe the stall path (lines 99-159) already persists full output via TryPersistKilledOutput and retries within stallRetriesLeft budget. 3. Confirm two real incidents: (a) .relay-scratch/drive-jf2.log:26090 shows fix-timing-estimates s8 flagged with only nono banner after swival's corrective message for malformed JSON, the real traceback lost; (b) .relay-scratch/drive-v22.log:5346 shows drop-vestigial-kimi-suffix s8 flagged identically, the actual litellm 400 error only recoverable from a separate swival report JSON at .relay/drop-vestigial-kimi-suffix-from-tier-aliases/stage8-attempt1.report.json:30. 4. Verify no killed-output.txt artifact exists for either crashed case (the stall path would have written one). 5. grep -rn 'TrimForError' and 'TryPersistKilledOutput' across src/ to confirm the former is only in the nonzero-exit branch and the latter only in the stall branch."
}

## Stage 4 - Plan

{
  "plan": "Route the nonzero-exit branch in SwivalSubagentRunner.RunAsync into the existing stall-retry infrastructure: persist full captured output via a new TryPersistKilledOutput overload (string reason), retry within the shared stallRetriesLeft budget, and on exhaustion build the flag reason from TrimForTail (last N chars) + persisted path. Add TrimForTail helper. Tests: two new fake-swival tests in SwivalSubagentRunnerWatchdogTests.NonzeroExit.cs — one recovers on retry, one exhausts with tail-not-head assertion.",
  "manifest": [
    "src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.NonzeroExit.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.NonzeroExit.cs"
  ],
  "rationale": "Three tests in the new NonzeroExit.cs partial-class file, following the existing watchdog-test patterns (fake bash scripts keyed on trace_dir to distinguish attempts, shared TestConfig() helper, SwivalTestHelpers.WriteExecutableAsync). All three fail against the current fail-fast nonzero-exit branch: test 1 (exit 1 then recover) fails on the retry-never-happens + no persist; test 2 (always exit 1, long banner + tail error) fails because TrimForError captures only the head banner, missing the real tail error, and no persist; test 3 (MaxStallRetries=0 boundary) fails because killed-output.txt is never written. The tests share the existing stallRetriesLeft / MaxStallRetries budget as specified and leave pre-flight guards (backend probe, empty whitelist) untouched."
}

## Stage 6 - Implement

{
  "summary": "Implemented retry-and-persist for nonzero swival exits by routing the nonzero-exit branch into the existing stall-retry infrastructure. Three changes across two source files, plus pre-authored tests already in place:\n\n**ProcessRunners.Helpers.cs:**\n- Added `TrimForTail(string value, int tailChars = 600)` — returns the last N chars (the tail where real errors sit), opposite of TrimForError which takes the head/sandbox banner.\n- Added a new `TryPersistKilledOutput(traceDirParent, stageNum, attempt, reason, output)` overload that accepts a plain string reason instead of ActivityWatchdog.Result, for use by the nonzero-exit path.\n\n**ProcessRunners.RunAsync.cs (lines 179-217):**\n- Replaced the immediate `TrimForError`-then-return with: persist full output via new TryPersistKilledOutput, publish a `nonzero_exit` event, retry within the shared `stallRetriesLeft` budget (decrement + continue), and on exhaustion build the flag reason from `TrimForTail(result.Output)` + persisted path.\n\n**Tests (SwivalSubagentRunnerWatchdogTests.NonzeroExit.cs):**\n- 3 tests already authored per spec: exit-1-then-recover (asserts retry + persist + success), always-exit-1 (asserts tail-not-head in reason + bounded attempts + persist on every attempt), MaxStallRetries=0 boundary (asserts persist without retry). All 3 pass. Full watchdog suite (17 tests) passes with zero regressions."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "Review stage 7 returned pass with zero issues — no blockers or warnings exist. Implementation is complete: nonzero-exit branch persists output via TryPersistKilledOutput, retries within shared stallRetriesLeft budget, uses TrimForTail for the flag reason, and three tests cover the retry-and-persist behavior."}

## Stage 9 - Verify

{
  "summary": "Nonzero swival exits now persist full captured output to stageN-attemptM.killed-output.txt and retry within the shared MaxStallRetries budget instead of fail-fast with only a 600-char head snippet. A new TrimForTail helper surfaces the real error at the output tail in the flag reason, plus the persisted path. Three tests verify retry recovery, tail-not-head flagging, and the MaxStallRetries=0 boundary.",
  "commitMessages": [
    "fix(watchdog): persist and retry nonzero swival exits like stalls",
    "Preserve full stderr on swival crash; retry within MaxStallRetries budget; show tail not head in flag reason",
    "Route nonzero-exit branch into stall-retry infrastructure — persist killed-output.txt, retry bounded, TrimForTail for evidence",
    "Swival exit!=0 no longer loses diagnostic tail — full output persisted, retried; on exhaustion reason includes path+tail"
  ]
}

## Stage 10 - Fix-verify (attempt 1/5)

{
  "summary": "Fixed two guard failures: (1) Updated SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline from 131→134 to account for the 3 new [Fact] tests in SwivalSubagentRunnerWatchdogTests.NonzeroExit.cs. (2) ProcessRunners.RunAsync.cs grew to 309 lines (limit 300); moved PublishContractRetryAsync to Helpers.cs (289→289) and compressed a 4-line comment to 2 lines in Helpers.cs (301→299). All 636 tests pass, exit 0.",
  "amendManifest": [
    "tests/VisualRelay.Tests/SplitGuardVerificationTests.cs"
  ]
}

## Stage 11 - Commit

Committed by Visual Relay.

