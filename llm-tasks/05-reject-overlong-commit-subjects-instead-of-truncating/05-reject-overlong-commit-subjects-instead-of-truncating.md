## Task: Stop silently truncating commit subjects into mid-sentence gibberish

`CommitMessageSanitizer` hard-truncates any candidate subject longer than
`CommitRules.MaxSubjectChars` (72) at a word boundary and silently discards the
rest (`src/VisualRelay.Core/Execution/CommitMessageSanitizer.cs:157-167`,
applied in `SanitizeSubject` at line 78). The result is permanent git history
whose subjects stop mid-sentence, which reads as corruption and destroys the
information the subject was supposed to carry. Two sealed commits from the
2026-07-15 drain demonstrate it:

| Sealed subject (git log) | Stage-authored candidate (stage 10 report) |
|---|---|
| `revert: abandon KeySetupPanelUiTests split - replacement tests 17 %` | `revert: abandon KeySetupPanelUiTests split — replacement tests 17 % slower` (74 chars after em-dash swap → "slower" cut) |
| `refactor(test): merge 3 NoCommitContaminationTests facts into` | `refactor(test): merge 3 NoCommitContaminationTests facts into data-driven theory` (81 chars → cut at "into") |

"replacement tests 17 %" (17 % *what*?) and "facts into" (into *what*?) are
what future readers of this repo's history get. Since the sealed commit is
Visual Relay's product in every target repository, this is a general quality
bug, not a cosmetic one.

### Current mechanics (verified)

- Stage reports provide multiple `commitMessages` candidates (the split task's
  stage 10 offered three, including a 61-char one that would have fit:
  `docs: record KeySetupPanelUiTests split attempt — no improvement`).
- `BuildCommitChain` (`RelayDriver.Artifacts.cs:97-111`) sanitizes each
  candidate, drops non-Conventional ones, and appends the guaranteed
  `chore(relay): <taskId>` fallback. `GitCommitter` then tries candidates in
  order until the hook accepts one (`GitCommitter.cs:189-216`).
- Because truncation happens *inside* sanitize, an overlong first candidate is
  never rejected — it is mangled and then wins, shadowing shorter candidates
  that would have survived intact.
- The fallback subject already has the right idea: it truncates with a visible
  `…` and keeps the id recognizable (`BuildFallbackSubject`, lines 142-155).

### What to build

Pick and implement a coherent policy — the key requirement is that a subject
that cannot fit intact must never be silently word-chopped:

1. Preferred: `TrySanitizeMessage` returns null for subjects that exceed 72
   chars after normalization, so `BuildCommitChain` falls through to the next
   candidate; the guaranteed fallback still ensures the chain is never empty.
   Weigh the trade-off: if ALL candidates overflow, history gets the generic
   `chore(relay): <taskId>` — decide whether that is acceptable or whether a
   final "truncate WITH visible ellipsis" candidate (fallback-style) should sit
   between rejection and the generic fallback.
2. Upstream pressure: the stage contract that asks for `commitMessages` should
   state the 72-char subject limit so candidates arrive fitting (check the
   stage prompt/contract text; the hook rules in `docs/commit-messages.md` and
   `CommitRules` are the source of truth). Prompt-side guidance plus
   sanitizer-side rejection covers both halves.
3. Whatever policy lands, add a run-log advisory when a candidate is rejected
   or altered for length, so message degradation is observable instead of
   silent.

### Constraints

- The commit-msg hook (`check-commit-message`, rules in
  `src/VisualRelay.Core/CommitLint/`) stays authoritative; the sanitizer must
  keep producing hook-passing messages. Body-bullet handling
  (`SanitizeBullet`) is out of scope except where subject policy touches it.
- Do not relax `MaxSubjectChars`.
- Repo-agnostic: target repos' own hooks may be stricter; candidate fallthrough
  in `GitCommitter` must keep working.

### Tests (red first)

- Sanitizer test: a 74-char-after-normalization subject is not silently
  truncated (either rejected or visibly ellipsized per chosen policy) — assert
  the exact policy.
- Chain test: overlong candidate #1 + fitting candidate #2 → the sealed message
  uses candidate #2 intact (this is the regression the two real commits hit).
- All-overlong case: asserted, documented outcome (fallback or ellipsis).

### Verification

- `./visual-relay check` fully green including the new tests.
