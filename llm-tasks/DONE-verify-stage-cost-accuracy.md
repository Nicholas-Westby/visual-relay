# Make stage and session cost provably accurate (no silent under-count)

The per-stage and running-session dollar figures are suspiciously low — about
$0.001–$0.004 per stage and roughly $0.01 for a four-stage real run — for
hundreds of thousands of prompt tokens. The numbers are not merely small; the
cost calculation is structurally wrong in a way that drives the **input** cost
toward zero, so the displayed amount is almost certainly a large under-count.
This task makes the estimator auditable and correct: recompute cost from token
usage against a documented price table, count every token class, sum every
attempt and stage, and surface (never swallow) any stage whose cost cannot be
computed.

## Current state (researched)

The estimator lives in `src/VisualRelay.Core/Costs/RelayCostEstimator.cs` with
the price table in `src/VisualRelay.Core/Costs/RelayPricing.cs` (USD per
1,000,000 tokens — units are fine). What it does today, and where the
under-count hides:

- **Prompt tokens are double/triple counted, then cancelled out — the dominant
  bug.** `EstimateReport` (`RelayCostEstimator.cs:31`) sums
  `prompt_tokens_est` across every `llm_call` in `timeline`. But in real
  reports that field is the **cumulative context size for that turn**, i.e. it
  grows monotonically each turn. Verified on
  `.relay/author-edit-and-manage-task-attachments/stage4-attempt1.report.json`:
  the 18 per-turn values run `8619, 14630, 23392, … , 44038` (strictly
  increasing); the true context at the end is ~44k, yet the code sums them to
  **585,901**. `stats.prompt_cache.cached_tokens` (650,240) is *also* a
  per-turn cumulative sum, so at `RelayCostEstimator.cs:36`
  `uncached = max(0, 585901 − 650240) = 0`. Net effect: essentially the entire
  prompt is priced at the cache rate (or zero), and the real fresh
  (uncached) input tokens are **never charged at the full input rate**. This is
  the single biggest source of the under-count.
- **Cache-write tokens are dropped entirely.** `stats.prompt_cache` carries
  `cache_write_tokens` (0 in the captured fixtures, but present in the schema),
  and `ReadCachedTokens` (`RelayCostEstimator.cs:52-62`) reads only
  `cached_tokens`. Cache writes are typically billed at a premium and are
  silently free here.
- **Output tokens are guessed, not measured.** `RelayCostEstimator.cs:33`
  estimates output as `ceil(answer.Length / 4) + llmCalls.Length * 50`. The
  reports contain no real output-token field, so output is an approximation;
  it must be documented as such (and the 50-tokens/turn constant
  `OutputTokensPerTurn`, `RelayCostEstimator.cs:16`, justified or replaced).
- **No reasoning-token class.** Nothing reads a reasoning/thinking token count;
  if `swival` ever emits one it is uncounted.
- **swival does NOT report a cost.** The real reports
  (`.relay/author-edit-and-manage-task-attachments/stage*-attempt*.report.json`)
  have top-level keys `version, mode, timestamp, task, model, provider,
  settings, sandbox, result, stats, timeline` — there is no `cost`, `usage`, or
  `total_cost` field. So we **must** recompute; there is no vendor cost to
  trust.
- **A stage that can't be priced silently becomes $0.** `TryEstimateCost`
  (`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs:90-105`) catches
  `JsonException` and returns `null`; the driver then does
  `sessionCostUsd += cost?.CostUsd ?? 0` (`RelayDriver.cs:71`) and displays
  `MoneyFormatter.Dollars(cost?.CostUsd ?? 0)` (`RelayDriver.cs:251`). A
  malformed/missing report, or an unknown model (which returns
  `Priced=false, CostUsd=0` at `RelayCostEstimator.cs:41`), contributes $0 to
  the session total with no visible signal. `Priced` is plumbed into history
  (`RelayRunHistory.cs:37,158`) but the live driver cost line only uses
  `CostUsd`.
- **Driver "commit" stage** (`RelayDriver.cs:62-65`) is a non-LLM driver stage
  with genuinely no cost — that $0 is correct and should stay $0, but should be
  distinguishable from "couldn't compute".
- **Multiple attempts and all stages:** attempts are summed per stage in
  `SquashAttempts` (`RelayRunHistory.cs:145-159`, sums `CostUsd`,
  `PromptTokens`, etc.) and stages summed in `TaskRunMetric`. The live
  `sessionCostUsd` (`RelayDriver.cs:42,71`) accumulates per stage. Confirm
  nothing drops a re-run attempt.

Existing coverage — `tests/VisualRelay.Tests/RelayCostEstimatorTests.cs`:
- `EstimateReport_*` tests use small **hand-written** JSON literals with
  `prompt_tokens_est` values that happen to exceed `cached_tokens`, so they
  never exercise the real cumulative-token case where `uncached` collapses to
  0. None feed a captured real `report.json`.
- They assert `CostUsd > 0` (`:90`) or a number derived from the same flawed
  formula (`:115`, `:138`) — they lock in the bug rather than catch it.
- There is **no** test that runs a real fixture through the estimator and
  checks an independently-computed dollar amount, **no** test that any token
  class is non-zero, and **no** test that an uncomputable stage is flagged
  rather than silently $0.

## What to build

Write the failing tests first, then make them pass. One committed direction —
recompute cost from per-turn token usage against the documented price table,
count every token class, and make an uncomputable stage loud.

1. **Fix prompt-token accounting (the core correctness fix).** Stop summing the
   cumulative `prompt_tokens_est` across turns. Derive **per-turn fresh
   (uncached) input** correctly. The defensible reading of the data: each
   turn's `prompt_tokens_est` is that turn's total context; the per-turn cached
   portion is covered by `prompt_cache.cached_tokens` (cumulative). Compute
   uncached input as the **incremental new context per turn** (turn N context
   minus turn N−1 context, floored at 0) summed across turns, and treat the
   final-turn context (or the explicit cached counter) as the cached read
   volume. Document the chosen model in a comment with a worked example. The
   invariant to enforce: `uncached_input > 0` whenever there is more than one
   turn — it must never silently floor to 0 the way it does today.
2. **Sum every token class.** Input (uncached) at the input rate, cached-read
   at the cache rate, **cache-write** at its own rate (read
   `cache_write_tokens`; if `RelayPricing` has no cache-write rate, document the
   fallback and add the field), output at the output rate, and reasoning tokens
   if/when present. No class may be silently zero because a field name was
   missed. Extend `RelayCostEstimate` (`RelayCostEstimator.cs:5-12`) with the
   fields needed so the breakdown is auditable from the record.
3. **Make an uncomputable cost visible, not $0.** Distinguish three cases:
   (a) a genuine zero-cost driver stage, (b) a priced stage with a computed
   cost, (c) a stage whose cost is **unknown** (malformed report, missing file,
   or unknown model). For case (c), do **not** fold $0 into `sessionCostUsd`
   silently — propagate an "unknown/unpriced" signal to the displayed stage
   line and the session total so the user sees a marker (e.g. a `?`/warn badge)
   rather than a falsely precise small number. `TryEstimateCost`
   (`RelayDriver.Artifacts.cs:90-105`) must still not crash the run, but the
   swallowed failure has to surface as "unknown", not absorbed as free.
4. **Audit the full sum path.** Verify attempts (`SquashAttempts`) and stages
   (`TaskRunMetric` / live `sessionCostUsd`) sum every priced stage, and that an
   unknown stage taints the aggregate's priced flag (already
   `ordered.All(... Priced)` at `RelayRunHistory.cs:158` — keep that, and make
   the live driver path honor it too).

## Done when

- **Real-fixture regression tests exist and assert an exact, hand-computed
  dollar amount.** Capture the four real reports from
  `.relay/author-edit-and-manage-task-attachments/stage*-attempt*.report.json`
  as test fixtures (under `tests/VisualRelay.Tests/Fixtures/`, alongside the
  existing fixture there). For at least the two `balanced-kimi` stages and one
  `cheap-kimi` stage, the task author has worked the expected cost **by hand**
  from the token counts × the documented `RelayPricing` rates and the test
  asserts that exact value (e.g. stage3: uncached input × 0.435 + cached ×
  0.003625 + output × 0.87, all ÷ 1e6 — show the arithmetic in the test
  comment). These must reflect the corrected per-turn accounting, so the
  stage3/stage4 input cost is materially above zero, not the ~$0.0039 the
  current formula yields.
- **A test proves no token class is omitted.** Feed a report exercising
  non-zero uncached input, cached read, cache write, output (and reasoning if
  modeled); assert each contributes to `CostUsd` and that zeroing any one class
  strictly lowers the total.
- **A test proves the cumulative-token bug is gone:** a multi-turn report whose
  per-turn `prompt_tokens_est` is monotonically increasing yields a positive
  uncached-input charge (the current code returns ~0).
- **A guard/visible signal for unknown cost:** a malformed/missing report or an
  unknown-model report produces an "unknown" stage cost that is surfaced in the
  driver's stage line and prevents the session total from being reported as a
  trustworthy precise number — covered by a test asserting the unknown stage is
  flagged (not silently $0), while a genuine $0 driver stage still reads $0.
- All new/updated tests were written to fail first against current `main`, then
  pass.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
  Commit subjects.
