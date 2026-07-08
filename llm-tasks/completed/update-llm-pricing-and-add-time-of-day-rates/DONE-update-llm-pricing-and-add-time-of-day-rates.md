# Update Hardcoded LLM Pricing and Add Time-of-Day Rate Schedules

Visual Relay computes run costs itself: swival's per-stage `report.json` supplies token counts
(`timeline[].prompt_tokens_est`, `stats.prompt_cache`), and
`src/VisualRelay.Core/Costs/RelayCostEstimator.cs` multiplies them by **hardcoded** USD-per-1M
rates in `src/VisualRelay.Core/Costs/RelayPricing.cs` (`RelayPricing.Default`, keyed by the
report's `model` string — tier aliases like `cheap`/`balanced`/`frontier`/`vision` plus concrete
names like `glm-5.2`, `kimi-k2`). Several entries are stale, and DeepSeek has announced
**time-of-day pricing** (2× during peak hours from mid-July 2026), which the current
flat-rate model cannot represent. Update the rates and teach the estimator about rate schedules.

## Current state (researched)

- **`RelayPricing.cs` (25 lines)** — `ModelPricing(Input, Output, CachedInput?, CacheWrite?)`
  record; `CacheWrite: null` falls back to the Input rate, `CachedInput: null` likewise (via
  `pricing.CachedInput ?? pricing.Input` in the estimator).
- **`RelayCostEstimator.cs` (134 lines)** — `EstimateReport(JsonElement report)`: uncached input
  = last cumulative `prompt_tokens_est` (context telescopes within a stage); cached/cache-write
  tokens from `stats.prompt_cache`; **output tokens are estimated**
  (`ceil(answer.Length/4) + 50/turn` — reports carry no measured output-token field); unknown
  model → cost 0 with `pricingFound: false`. The report's top-level `timestamp` records when the
  stage finished; individual `llm_call` timeline entries carry no timestamps.
- Swival provides tokens and timing only — it has no cost concept; pricing is entirely VR's.

## Researched rates (verified 2026-07-06/07 — sources noted; re-verify each at implementation)

| Key | Current entry (In/Out, CachedIn, CacheWrite) | Correct as of research | Source |
|---|---|---|---|
| `cheap` (deepseek-v4-flash) | 0.14 / 0.28, 0.0028, 0.14 | **unchanged as the off-peak base**; peak = 2× (see below) | api-docs.deepseek.com/quick_start/pricing |
| `balanced` (deepseek-v4-pro) | 0.435 / 0.87, 0.003625, 0.435 | **unchanged as the off-peak base**; peak = 2× | same |
| `frontier` / `glm-5.2` | 0.80 / 2.40, 0.08, 0.80 | **stale** — Z.AI lists **1.40 / 4.40, cached 0.26**. VR routes via HF inference providers (novita/zai passthrough); confirm the HF-provider price on the zai-org/GLM-5.2 model page and use that number | docs.z.ai pricing; HF model page |
| `vision` | 0.30 / 1.50 | Qwen3-VL-235B-A22B-Instruct ≈ **0.20 / 0.88** (aggregate across providers; confirm Novita's exact rate — the configured route is `huggingface/novita/...`) | openrouter.ai/qwen/qwen3-vl-235b-a22b-instruct |
| `kimi-k2` | 0.95 / 4.0, 0.16, 0.95 | 0.95 / 4.00, **cache-hit 0.19** | platform.kimi.ai/docs/pricing/chat-k27-code (official) |
| `claude-opus-1m` | 5.0 / 25.0, no cache rates | 5.0 / 25.0 ✓ (current Opus has 1M context at standard pricing — no long-context premium); **add cache rates: CachedInput 0.50 (0.1×), CacheWrite 6.25 (1.25×, 5-min TTL)** | Anthropic pricing |
| `claude-sonnet` | 3.0 / 15.0 | 3.0 / 15.0 sticker ✓; add CachedInput 0.30, CacheWrite 3.75. (If the bound model is Sonnet 5: intro pricing 2.0 / 10.0 applies through 2026-08-31 — use the sticker rate, note the intro in a comment) | Anthropic pricing |
| `claude-haiku` | 1.0 / 5.0 | ✓; add CachedInput 0.10, CacheWrite 1.25 | Anthropic pricing |
| `gpt-5` | 1.25 / 10.0 | 1.25 / 10.0 (verify current; cached input 0.125) | platform.openai.com pricing |
| `hf-qwen3-coder-next` | 0.30 / 1.30 | verify Novita's Qwen3-Coder-480B-A35B-Instruct rate (route is `huggingface/novita/...`) | Novita / HF model page |

**DeepSeek time-of-day pricing** (announced 2026-06-30, effective with the V4 official release
"mid-July 2026" — i.e. roughly when this task runs): the **entire model lineup** is charged at
**2× the listed rate during peak hours, 9:00–12:00 and 14:00–18:00 daily**; the listed rates
above are the off-peak base. The announcement (TechNode, 2026-06-30) does not state the
timezone — almost certainly China Standard Time (UTC+8, no DST), but **confirm the window and
timezone against api-docs.deepseek.com/quick_start/pricing at implementation time**; the official
docs had not yet published the schedule as of 2026-07-07. If still unpublished, implement the
mechanism with the 9–12/14–18 Asia/Shanghai window as clearly-commented provisional constants.

## What to build (TDD-first)

1. **Schedule-aware pricing model.** Extend `ModelPricing` with an optional rate schedule, e.g.
   `IReadOnlyList<RateWindow>? Windows` where
   `RateWindow(TimeOnly StartLocal, TimeOnly EndLocal, string TimeZoneId, double Multiplier)` —
   a multiplier applied to Input/Output/CachedInput/CacheWrite when the evaluation instant falls
   inside the window (local time in `TimeZoneId`, resolved via `TimeZoneInfo`; support both IANA
   and Windows ids or normalize — CST has no DST so `Asia/Shanghai` is stable). No window
   matched → base rates. This is pure mechanical schedule math in the harness — no model
   involvement anywhere.
2. **Evaluation instant.** `RelayCostEstimator.EstimateReport` evaluates the schedule at the
   report's top-level `timestamp` (the stage-end instant; individual llm_calls carry no
   timestamps). Stages are bounded by the stage ceiling, so at most one boundary crossing per
   stage — accept that approximation and note it in a comment. Thread the instant as a parameter
   with the report value as default so tests can pin arbitrary times.
3. **Update the rate table** per the researched table above, re-verifying each flagged number at
   the cited source, and attach the DeepSeek peak window (2×) to the `cheap` and `balanced`
   entries. Keep the USD-per-1M unit and the existing null-fallback semantics unchanged.
4. **Tests** (new test file alongside existing patterns): exact-rate lookups for updated entries;
   schedule math — inside window, outside window, boundary minutes, window spanning midnight
   (not needed for DeepSeek but the type should handle it or explicitly reject it), timezone
   conversion from a UTC report timestamp into Asia/Shanghai; multiplier applied to all four
   rates; entries without windows unaffected; unknown model still returns `pricingFound: false`.
5. **Comment provenance.** Each updated entry gets a short comment with the source and the
   verified date (the file already does this for kimi/glm) so the next staleness audit is easy.

## Done when

- `RelayPricing` carries the corrected rates with provenance comments, and cheap/balanced carry
  the DeepSeek peak window at 2× (window/timezone confirmed against the official pricing page,
  or clearly marked provisional if DeepSeek has not yet published it).
- A stage report timestamped inside the peak window costs exactly 2× the same report timestamped
  off-peak, for DeepSeek-tier models only.
- All schedule/rate tests pass; `./visual-relay check` passes.

## Guardrails

- Do not change the estimator's token logic (telescoping context, estimated output tokens) — this
  task is rates and schedules only.
- Rates stay hardcoded in `RelayPricing.cs` (no config plumbing in this task); the schedule is
  part of the pricing entry, not a new config surface.
- No LLM/tier behavior changes — pricing is display/accounting only and must not influence
  routing.
- Keep files under the 300-line guard (both files are small; a new `RateWindow` type may warrant
  its own file if `RelayPricing.cs` grows past ~250 lines).
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs.
