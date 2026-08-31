# Phase 0 baseline — first-party agent pipeline

Measured on this machine at VERSION 0.109 (commit f3e36d7), 2026-08-31, 13:30–14:10
local (America/Los_Angeles). These are the numbers the replacement is judged against.
Where a figure differs from the one quoted in the spec (measured at 0.106), the
measurement here supersedes it and the spec's value is shown for comparison.

## Build

| Measure | Value | Spec's 0.106 value |
| --- | --- | --- |
| Incremental build, no changes | 10.7 s wall (4.1 s MSBuild) | 21.4 s |
| Incremental build, after checkout | 38.8 s wall (31.4 s MSBuild) | — |

## Test suite

Trust only the TRX `<Times>` delta; summed per-test durations are scheduler
artifacts inflated up to ~1000x.

| Measure | Value | Spec's 0.106 value |
| --- | --- | --- |
| TRX finish − start | 26.36 s | 37.0 s |
| Wall clock incl. build | 35.6 s | — |
| Total / passed / skipped | 3545 / 3448 / 97 | 3542 / 3445 / 97 |
| Failed | 0 | 0 |

## Dependency footprint

| Component | Size |
| --- | --- |
| `backend-venv` (LiteLLM 1.92.0, Python 3.13) | 482 MB |
| Homebrew `swival` 0.1.2 (Python 3.14) | 361 MB |
| `uv` | 52 MB |
| `python@3.14` | 168 MB |
| Total | 1063 MB |

## Cold start

| Measure | Value | Spec's 0.106 value |
| --- | --- | --- |
| Launch to `vr-control: listening` | 39.0 s | — |
| Proxy boot (`backend: ready after`) | 11 s | 5 s |

## `check` red baseline

`./visual-relay check` exits 1 after 199.5 s, short-circuiting at step 7
(InspectCode) and never reaching format, tests or screenshot.

**174 findings** at or above the SUGGESTION floor (spec's 0.106 value: 171).
A moved count is not this change's fault unless the histogram below moves with it.

| Rule | N | Rule | N |
| --- | --- | --- | --- |
| RedundantUsingDirective | 24 | UseCollectionExpression | 3 |
| ConvertToPrimaryConstructor | 19 | RedundantExplicitArrayCreation | 3 |
| InvalidXmlDocComment | 14 | InconsistentNaming | 3 |
| RedundantNameQualifier | 12 | UnusedMember.Local | 2 |
| UseStringInterpolation | 11 | RedundantTypeDeclarationBody | 2 |
| RedundantStringFormatCall | 11 | RedundantStringInterpolation | 2 |
| MergeIntoPattern | 11 | NotAccessedPositionalProperty.Global | 2 |
| UnusedVariable | 10 | EmptyGeneralCatchClause | 2 |
| MemberCanBePrivate.Global | 8 | AccessToDisposedClosure | 2 |
| UnusedMember.Global | 6 | *(13 rules at N=1)* | 13 |
| PossibleMultipleEnumeration | 5 | | |
| UseObjectOrCollectionInitializer | 4 | | |
| UnusedParameter.Local | 4 | | |
| ConditionIsAlwaysTrueOrFalse…NullableAPIContract | 4 | | |

The full SARIF is archived outside the repo; regenerate with `./visual-relay check`.

## Guard inventory

| Measure | Value | Spec's 0.106 value |
| --- | --- | --- |
| `tools/VisualRelay.Guards` lines | 4023 | 4,023 |
| Matcher classes (`public static class`) | 21 | 21 |
| Guard-test files (`using VisualRelay.Guards`) | 38 | 35 |
| `[Fact]` count across those files | 249 | 202 |

## Boundary code to be removed

4631 lines across the proxy lifecycle, the Swival runner's 16 partials, the
profile-pin registry, the trace tailer/parser and the watchdog. The spec's
estimate of "roughly 4,180 lines" counts a slightly narrower set.

## Corpus snapshot

`.relay/` holds 393 MB: **1109 reports**, 967 traces, 118 task dirs, 124 distinct
tasks. Every figure below is computed over all 1109 reports and becomes a
regression threshold for Phase 6.

### Per-stage statistics

| Metric | Mean | Median |
| --- | --- | --- |
| Turns | 21.38 | 14 |
| Tool calls | 31.56 | 23 |
| Failed tool calls | 0.95 | — |
| Compactions | 0.37 | — |

### Outcomes

| Outcome | N |
| --- | --- |
| success | 1092 |
| error | 10 |
| exhausted | 7 |

### Resilience counters (corpus totals)

| Counter | Total |
| --- | --- |
| Compaction firings | 414 |
| Guardrail interventions | 95 |
| Stormed calls | 41 |
| Recovered responses | 28 |
| Truncation repairs | 2 |
| **Turn drops** | **0** |
| **Scavenged calls** | **0** |

`turn_drops` and `scavenged_calls` are zero across the entire corpus, so any
non-zero value from the replacement is a regression needing no statistics.

### Retry rate

25 of 1074 distinct (task, stage) pairs needed more than one attempt: **2.33%**.
This is the sharpest single signal for Phase 6 acceptance.

### Tier usage

| Tier | Stages |
| --- | --- |
| balanced | 533 |
| cheap | 425 |
| frontier | 131 |
| vision | 20 |
| **claude / gpt-5 / opus / sonnet** | **0** |

Zero stages in the entire corpus ran on a premium provider, and neither
`ANTHROPIC_API_KEY` nor `OPENAI_API_KEY` is present in the user's environment.
This is the evidence for retiring both providers in Phase 1.

### End-to-end task cost and duration

Derived from the corpus rather than one fresh run: 124 tasks give a distribution
where a single sample gives a point, and DeepSeek's weekday peak windows make any
single sample time-of-day dependent.

| Measure | Value |
| --- | --- |
| LLM time per task, median | 1673 s (27.9 min) |
| LLM time per task, mean | 2027 s (33.8 min) |
| Tool time per task, mean | 556 s (9.3 min) |
| Corpus total LLM time | 69.8 h |
| Corpus total tool time | 19.1 h |

## Live model-output visibility (the headline "before" number)

Swival writes its whole JSONL transcript at process exit, so every record in a
trace file carries a timestamp within milliseconds of every other. Measured over
**957 stages** with both a report and a parseable trace:

| Measure | Value |
| --- | --- |
| Total agent wall time (LLM + tool) | 4536.0 min (75.6 h) |
| Total time spanned by trace records | 1.71 min |
| **Stage wall time with live model output** | **0.04%** |
| **Stage wall time blind** | **99.96%** |

The spec's figure of 99.7% was measured over 15 stages of one drain; over the
whole corpus it is 99.96%. `RelayTraceTailer`'s 200 ms poll loop, its byte-offset
bookkeeping and `watchdog.Pulse("trace")` are inert for the entire duration of
every stage, and the Commands tab is blank throughout.
