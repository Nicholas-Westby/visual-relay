## Stage 1 - Ideate

{ "summary": "The CommitMessageSanitizer silently truncates overlong commit subjects at word boundaries, producing gibberish like 'facts into' in sealed git history. The fix: reject subjects >72 chars (return null from TrySanitizeMessage) so BuildCommitChain falls through to shorter intact candidates, with an ellipsis-truncated safety-net candidate before the generic chore(relay) fallback for the all-overlong case. Add run-log advisories on rejection/truncation, and update the stage prompt to document the 72-char subject limit upstream.", "options": ["Reject-overlong-with-ellipsis-safety-net: TrySanitizeMessage returns null for >72-char subjects; chain falls through; inject visibly-ellipsized candidate before generic fallback; log advisories; update stage prompt. Covers best-case (next candidate fits), worst-case (visible … marker), and eliminates silent corruption.", "Pure-reject-rely-on-generic-fallback: Same rejection logic but no ellipsis candidate; all-overlong case yields chore(relay): <taskId>. Simpler but loses subject info in worst case.", "Visible-truncation-only: Keep truncation but append … (like BuildFallbackSubject) and log advisories. Minimal change but still discards info past 72 chars; reader sees 'facts into…' and still wonders 'into what?'"] }

## Stage 2 - Research

{
  "findings": "CommitMessageSanitizer.SanitizeSubject() (lines 69-79, 157-167) silently word-boundary truncates subjects >72 chars, producing corrupted git history like 'replacement tests 17 %' and 'facts into'. The truncation happens inside TrySanitizeMessage, so overlong candidates are never rejected (never null) and always win the candidate chain in BuildCommitChain (RelayDriver.Artifacts.cs:97-111), shadowing shorter intact candidates. GitCommitter.CommitAsync (GitCommitter.cs:189-216) tries candidates in order until the hook accepts one, so the mangled first candidate is what gets sealed. BuildFallbackSubject (lines 142-155) already does visible-ellipsis truncation correctly. The stage 10 Verify prompt (RelayStages.cs:107) and its output contract lack the 72-char subject limit guidance. The ledger already supports inline advisories via > **Note**: format (Stage5.cs:78, RepoGuards.cs:172). Existing tests (TrySanitizeSubject_TruncatesAt72Chars, OverflowWithInternalPeriod_DoesNotEndWithPeriod) assert the current truncation behavior and must change per policy.",
  "constraints": [
    "MaxSubjectChars (72) must not be relaxed.",
    "The commit-msg hook stays authoritative; sanitizer output must always pass the structural validator (SubjectRules.Check enforces ≤72 chars).",
    "Body-bullet handling (SanitizeBullet) is out of scope except where subject policy touches it.",
    "Repo-agnostic: candidate fallthrough in GitCommitter must keep working for target repos with stricter hooks.",
    "TrySanitizeMessage and TrySanitizeSubject are internal/private — behavioral change is allowed. FromRawOrFallback is the public entry point used by BuildCommitChain.",
    "BuildCommitChain chain shape changes: must inject ellipsis-truncated safety-net candidate(s) before generic chore(relay) fallback, not replace the generic fallback.",
    "Stage 10 prompt must document the 72-char subject limit so candidates arrive fitting.",
    "Advisories must be observable via ledger > **Note**: lines; optionally via EventSink.PublishAsync warn events.",
    "Existing tests TrySanitizeSubject_TruncatesAt72Chars and OverflowWithInternalPeriod_DoesNotEndWithPeriod must adapt to the new rejection/ellipsis policy."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "CommitMessageSanitizer.SanitizeSubject() (CommitMessageSanitizer.cs:78) calls Truncate() (lines 157-167), which silently cuts any subject >72 chars at the last space boundary, discarding the tail. Because truncation happens inside TrySanitizeMessage() (line 36), overlong candidates are never rejected: they always return a non-null value. BuildCommitChain() (RelayDriver.Artifacts.cs:97-111) collects all non-null sanitized results into the candidate chain and appends the generic chore(relay) fallback. GitCommitter.CommitAsync() (GitCommitter.cs:189-216) tries candidates in order until the commit-msg hook accepts one. Since the truncated candidate is ≤72 chars and structurally valid, it always passes the hook on the first try, shadowing shorter intact candidates that follow — including the guaranteed fallback and any shorter stage-authored candidate.\n\nTwo sealed commits from the 2026-07-15 drain are the real-world repro. For task 'split-key-setup-panel-ui-tests', stage 10 (ledger.md:83-87) offered three candidates: (1) 'revert: abandon KeySetupPanelUiTests split — replacement tests 17 % slower' [74 chars after em-dash→hyphen normalization], (2) a revert headline, and (3) 'docs: record KeySetupPanelUiTests split attempt — no improvement' [61 chars]. Candidate 1 was word-boundary truncated at char 72, dropping 'slower', and sealed as 'revert: abandon KeySetupPanelUiTests split - replacement tests 17 %' (git SHA 8727c468) — the percentage sign is left dangling with '17 %' of what? Candidate 3 at 61 chars would have fit intact. For task 'merge-nocommit-contamination-tests-data-driven', stage 10 (ledger.md:95-99) offered: (1) 'refactor(test): merge 3 NoCommitContaminationTests facts into data-driven theory' [81 chars], (2) 'perf(test): share expensive setup...' [56 chars], (3) 'test: consolidate three near-identical...' [73 chars]. Candidate 1 was truncated at the space before 'data-driven', sealing 'refactor(test): merge 3 NoCommitContaminationTests facts into' (git SHA 10405867) — 'into' what? Candidate 2 at 56 chars would have fit. In both cases, shorter intact candidates existed but were never reached because the mangled first candidate won.\n\nBuildFallbackSubject() (lines 142-155) already demonstrates the correct visible-truncation pattern: when the taskId overflows, it preserves a head slice plus an ellipsis '…' so the description is never empty and the truncation is observable. The Truncate() method lacks this.\n\nThe stage 10 Verify system prompt (RelayStages.cs:107) tells the agent to produce '3-5 DISTINCT Conventional-Commit subject candidates' but never mentions the 72-char limit. The commit-messages.md doc (docs/commit-messages.md:38) states the limit but is not linked from the prompt. The OutputContract (RelayStages.cs:22) is just '{ \"summary\": string, \"commitMessages\": string[] }' — no length constraint.\n\nExisting tests explicitly assert the broken behavior: TrySanitizeSubject_TruncatesAt72Chars (CommitMessageSanitizerTests.cs:50-58) asserts truncation succeeds with length ≤72, and OverflowWithInternalPeriod_DoesNotEndWithPeriod (CommitMessageSanitizerHardeningTests.cs:131-137) relies on the truncation path. Both must adapt to the new rejection/ellipsis policy.",
  "excerpts": [
    "CommitMessageSanitizer.cs:78 — return StripTrailingPeriods(Truncate(clean)); // silent word-boundary cut inside SanitizeSubject",
    "CommitMessageSanitizer.cs:157-167 — Truncate() cuts at last space ≤72, no ellipsis, no signal returned to caller",
    "CommitMessageSanitizer.cs:36 — var subject = SanitizeSubject(lines[0]); // truncation happens HERE inside TrySanitizeMessage, before prefix check, so overlong never yields null",
    "RelayDriver.Artifacts.cs:97-111 — BuildCommitChain calls TrySanitizeMessage (never null for overlong→truncated) and collects all non-null + fallback; truncated always-first candidate wins in GitCommitter",
    "GitCommitter.cs:189-216 — foreach candidate in commitMessages → tries until hook accepts; truncated subject passes hook on first attempt",
    ".relay/split-key-setup-panel-ui-tests/ledger.md:83-87 — stage 10 offered candidate 'replacement tests 17 % slower' (74 chars) plus a 61-char candidate; sealed commit dropped 'slower'",
    ".relay/merge-nocommit-contamination-tests-data-driven/ledger.md:95-99 — stage 10 offered candidate 'facts into data-driven theory' (81 chars) plus a 56-char candidate; sealed commit cut at 'into'",
    "git log — 8727c468: 'revert: abandon KeySetupPanelUiTests split - replacement tests 17 %'; 10405867: 'refactor(test): merge 3 NoCommitContaminationTests facts into'",
    "CommitMessageSanitizerTests.cs:50-58 — TrySanitizeSubject_TruncatesAt72Chars asserts truncate-succeeds, not reject",
    "CommitMessageSanitizerHardeningTests.cs:131-137 — OverflowWithInternalPeriod_DoesNotEndWithPeriod relies on truncation path",
    "RelayStages.cs:107 — stage 10 Verify system prompt: no mention of 72-char subject limit",
    "CommitRules.cs:19 — public const int MaxSubjectChars = 72; // authoritative limit, shared with SubjectRules.cs:22-25 which enforces it in the hook",
    "RelayStages.cs:22 — stage 10 OutputContract: { 'summary': string, 'commitMessages': string[] } — no length constraint",
    "CommitMessageSanitizer.cs:142-155 — BuildFallbackSubject already does visible '…' truncation correctly when taskId overflows budget"
  ],
  "repro": "1. Create a stage-10-like raw candidate with a valid Conventional Commit prefix that exceeds 72 chars after em-dash→hyphen normalization, e.g.: `fix: this is a deliberately overlong commit subject that should exceed seventy two characters` (79 chars).\n2. Pass it as the first element of a rawCandidates list to `RelayDriver.BuildCommitChain([overlong, 'fix: short fitting subject'], 'test-task')`.\n3. Observe: the chain's first element is the silently truncated version (`fix: this is a deliberately overlong commit subject that should exceed`) — NOT null, NOT the second fitting candidate, NOT an ellipsis-truncated variant.\n4. Pass this chain to `GitCommitter.CommitAsync()` and confirm the sealed commit subject is the truncated gibberish, because it passes the commit-msg hook (≤72 chars, valid prefix) and is tried first.\n5. The real-world git history already contains the two mangled commits: `git log --oneline --all | grep -E 'replacement tests 17|facts into$'` shows `8727c468` and `10405867`."
}

## Stage 4 - Plan

{
  "plan": "## Policy: Reject-overlong with visible-ellipsis safety-net\n\nWhen a commit-subject candidate exceeds 72 chars after normalization, `TrySanitizeMessage` / `TrySanitizeSubject` return **null** instead of silently word-chopping it. `BuildCommitChain` falls through to the next intact candidate. For the all-overlong edge case, an ellipsis-truncated safety-net candidate (using `…`, same spirit as `BuildFallbackSubject`) is injected between the sanitized candidates and the generic `chore(relay): <taskId>` fallback. Every rejection or truncation emits a `warn`-level `commit_msg_rejected` event into the run log, so degradation is observable.\n\n### File-by-file changes\n\n**1. `src/VisualRelay.Core/Execution/CommitMessageSanitizer.cs`**\n\n- `SanitizeSubject()` (line 69-79): change return type `string` → `string?`. After normalization (`LowercaseAfterPrefix`), if `clean.Length > CommitRules.MaxSubjectChars`, return `null`. Otherwise return `StripTrailingPeriods(clean)` (no Truncate call).\n- Remove the old `Truncate()` method (lines 157-167) — no longer called.\n- Add new private `SanitizeSubjectWithEllipsis(string subject)` (same normalization pipeline), then: if `≤72` return intact; otherwise reserve 1 char for `…`, cut at last space within budget 71, `StripTrailingPeriods`, append `…`.\n- `TrySanitizeMessage()` (line 28-51): after `SanitizeSubject(lines[0])`, add null-guard: `if (subject is null || !HasConventionalPrefix(subject)) return null`.\n- `TrySanitizeSubject()` (line 57-67): same null-guard after `SanitizeSubject(lines[0])`.\n- Add new `internal static string? TrySanitizeSubjectWithEllipsis(string? raw)` — same structure as `TrySanitizeSubject` but calls `SanitizeSubjectWithEllipsis`.\n- Add new `internal static string? TrySanitizeMessageWithEllipsis(string? raw)` — same structure as `TrySanitizeMessage` but calls `SanitizeSubjectWithEllipsis` for the subject line; preserves body bullets.\n\n**2. `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs`**\n\n- Change `BuildCommitChain` return type from `IReadOnlyList<string>` to `(IReadOnlyList<string> Chain, IReadOnlyList<string> Advisories)`.\n- In the loop: for each raw candidate, try `TrySanitizeMessage`. If non-null, add to chain. If null, try `TrySanitizeMessageWithEllipsis` — if non-null, add to a `safetyNet` list and append an advisory string (`\"overlong commit subject truncated with ellipsis: \\\"<subject>\\\"\"`). If both null, the candidate had no valid prefix — silently skipped (no change from current behavior).\n- After the loop: `chain.AddRange(safetyNet)` then `chain.Add(CommitMessageSanitizer.FromRawOrFallback(null, taskId))`.\n- Return `(chain, advisories)`.\n\n**3. `src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`** (line 206)\n\n- Deconstruct the tuple: `var (chain, advisories) = BuildCommitChain(commitMessages, taskId);`\n- After building the chain, loop over advisories and publish each via `await _dependencies.EventSink.PublishAsync(new RelayEvent(DateTimeOffset.UtcNow, \"warn\", \"commit_msg_rejected\", runId, rootPath, taskId, 12, Data: new Dictionary<string, string> { [\"message\"] = advisory }), cancellationToken);`\n\n**4. `src/VisualRelay.Core/Execution/RelayStages.cs`** (line 107, Verify prompt)\n\n- Append to the Verify system prompt: `\"Each subject must fit within 72 characters total (type prefix, optional scope, colon, space, and description); subjects exceeding 72 chars will be rejected, not truncated. \"` before the existing bullet-requirement sentence.\n\n**5. `tests/VisualRelay.Tests/CommitMessageSanitizerTests.cs`**\n\n- `TrySanitizeSubject_TruncatesAt72Chars` (line 50-58): rename to `TrySanitizeSubject_Overlong_ReturnsNull`, change assertion from `.NotNull` + `.Length <= 72` to `Assert.Null(result)`.\n- Add `TrySanitizeSubject_Exactly72Chars_ReturnsSubject`: construct a subject at exactly 72 chars, assert it returns intact (not null, not truncated).\n- Add `TrySanitizeSubjectWithEllipsis_Overlong_EllipsizedWithinLimit`: overlong subject → result non-null, length ≤72, ends with `…`.\n- Add `TrySanitizeSubjectWithEllipsis_FittingSubject_ReturnsIntact`: a ≤72 subject → returns intact, no ellipsis appended.\n\n**6. `tests/VisualRelay.Tests/CommitMessageSanitizerHardeningTests.cs`**\n\n- `OverflowWithInternalPeriod_DoesNotEndWithPeriod` (line 131-138): change `TrySanitizeSubject` → `TrySanitizeSubjectWithEllipsis` so the test exercises the ellipsis path (which must also re-strip trailing periods).\n- `OverflowStrippedToEmptyDescription_FallsBackAndValidates` (line 140-149): no code change needed. `FromRawOrFallback` now falls through to `BuildFallbackSubject` for overlong input; the test already only asserts the output validates clean.\n\n**7. `tests/VisualRelay.Tests/RelayDriverCommitChainTests.cs`**\n\n- All three existing tests: deconstruct the tuple from `BuildCommitChain` (`var (chain, _) = …` or `var (chain, advisories) = …` as needed). Assertions on `chain` unchanged.\n- Add `BuildCommitChain_OverlongFirstFittingSecond_UsesSecondIntact`: feed `[overlong, fitting]`; assert `chain[0] == fitting` (the intact second candidate wins), `advisories` non-empty.\n- Add `BuildCommitChain_AllOverlong_YieldsEllipsisSafetyNetBeforeFallback`: feed only-overlong candidates; assert chain contains an entry with `…`, the generic `chore(relay):` fallback is last, and advisories are emitted for each overlong.",
  "manifest": [
    "src/VisualRelay.Core/Execution/CommitMessageSanitizer.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs",
    "src/VisualRelay.Core/Execution/RelayStages.cs",
    "tests/VisualRelay.Tests/CommitMessageSanitizerTests.cs",
    "tests/VisualRelay.Tests/CommitMessageSanitizerHardeningTests.cs",
    "tests/VisualRelay.Tests/RelayDriverCommitChainTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/CommitMessageSanitizerTests.cs",
    "tests/VisualRelay.Tests/CommitMessageSanitizerHardeningTests.cs",
    "tests/VisualRelay.Tests/RelayDriverCommitChainTests.cs"
  ],
  "rationale": "Three test files modified to assert the reject-overlong-with-ellipsis-safety-net policy. CommitMessageSanitizerTests.cs: renamed truncation test to assert null, added exactly-72-char boundary test, added TrySanitizeMessage overlong-null test. CommitMessageSanitizerHardeningTests.cs: changed OverflowWithInternalPeriod to assert null (overlong subjects rejected, not truncated-and-stripped). RelayDriverCommitChainTests.cs: added regression test for overlong-first+fitting-second → uses second intact, and all-overlong → ellipsis safety net before generic fallback. All 7 new/updated tests fail red against current silent-truncation code as expected."
}

## Stage 6 - Implement

{ "summary": "Implemented reject-overlong-with-visible-ellipsis-safety-net policy. SanitizeSubject returns null for >72-char candidates so BuildCommitChain falls through to shorter intact candidates. Ellipsis-truncated safety-net entries injected before the generic chore(relay) fallback for the all-overlong case. warn-level commit_msg_rejected advisories published per rejection. Verify stage prompt updated with 72-char limit. Old Truncate() method removed. All 3135 tests pass." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: All changes are C# business logic, prompt text, and test assertions — no UI, markup, styles, images, charts, or visual assets of any kind._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{ "summary": "CommitMessageSanitizer's SanitizeSubject() now returns null for candidates exceeding 72 chars after normalization, instead of silently word-chopping them into mid-sentence gibberish. BuildCommitChain falls through to the next intact candidate; an ellipsis-truncated safety-net entry is injected per overlong candidate before the generic chore(relay) fallback, so the all-overlong case produces a recognizable subject with a visible … marker. warn-level commit_msg_rejected advisories are published for every rejection/truncation, making degradation observable. The Verify stage prompt was updated to state the 72-char limit upstream. Old Truncate() method removed. All 3135 tests pass (0 failed, 114 skipped).", "commitMessages": ["fix(core): reject overlong commit subjects instead of truncating silently", "refactor: replace silent subject truncation with null rejection and ellipsis safety-net", "test: add regression tests for overlong subject rejection and candidate fallthrough", "docs: document 72-char subject limit in Verify stage prompt", "perf: avoid mid-sentence gibberish in sealed git history by rejecting overlong subjects"] }

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

