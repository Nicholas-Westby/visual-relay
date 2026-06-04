# Attempt number is hardcoded to `attempt1`, so re-runs overwrite history and merge stale traces

Every stage run writes to `stage{n}-attempt1` — the attempt suffix is a literal, never computed:

```csharp
// src/VisualRelay.Core/Execution/RelayDriver.cs:177-178 (BuildInvocation)
Path.Combine(taskDirectory, $"stage{stage.Number}-attempt1"),
Path.Combine(taskDirectory, $"stage{stage.Number}-attempt1.report.json"),
```

Stages run single-pass (`RelayDriver.cs:44`, `foreach (var stage in RelayStages.All)`; any
failure halts via `FlagAsync`), so there is no intra-run retry — every additional attempt comes
from the user **re-running the task** later. Because the index is fixed at `1`, each re-run:

- **Overwrites `stage{n}-attempt1.report.json`** — the prior attempt's outcome, cost, and timing
  are lost. Only the latest run survives.
- **Piles another Swival session JSONL into the same `stage{n}-attempt1/` dir** (the dir is
  `CreateDirectory`'d but never cleared; Swival names each file by session id). Observed in the
  sample: `stage1-attempt1/` holds three session files from three runs across two days.

The trace reader then **merges all of them**: `RelayTraceLocator.FindTraceFiles`
(`src/VisualRelay.Core/Traces/RelayTraceLocator.cs:22-26`) globs every `stage{n}-attempt*` dir
and reads every `*.jsonl` inside, concatenated. So the `LLM COMMANDS` / trace pane shows entries
from prior runs intermixed with the current one — you can't tell which run you're looking at.

This contradicts the rest of the system, which is already built for incrementing attempts:
`RelayRunHistory.SquashAttempts` (`src/VisualRelay.Core/Tasks/RelayRunHistory.cs:137`) sums
cost/time **across** a stage's attempts, the report/trace regexes match `attempt\d+`
(`RelayRunHistory.cs:177,180`), and `RelayEvent.Attempt` exists — all dead or misleading while
the producer is pinned to `attempt1`.

## Recommended fix

Make attempts real and have readers default to the latest attempt (greenfield: this is the
coherent model the existing infra already assumes).

1. **Allocate the next attempt index in `BuildInvocation`** instead of hardcoding `1`: scan
   `taskDirectory` for existing `stage{n}-attempt{k}` (dirs and/or reports), and use
   `max(k) + 1` (first run → `attempt1`, each re-run → `attempt2`, `attempt3`, …). Thread the
   chosen index into both the trace dir and the report path so they stay paired.
2. **Default trace/report reads to the latest attempt per stage**, not a merge.
   `RelayTraceLocator.FindTraceFiles` should select the highest-numbered attempt dir for a
   stage (mirror the existing per-stage grouping) and read only its sessions; optionally accept
   an explicit attempt to inspect an older one. Stale sessions then stop bleeding into the pane.
3. **Order attempts numerically, not by ordinal string.** `SquashAttempts` picks "latest" via
   `OrderBy(metric => metric.ReportPath, StringComparer.Ordinal)` (`RelayRunHistory.cs:139`) and
   the locator uses `Order(StringComparer.Ordinal)` — both rank `attempt10` before `attempt2`.
   Parse the attempt number and order by it so "latest" is correct past nine attempts.
4. **Confirm cost/outcome semantics now that multiple attempts coexist:** status/outcome comes
   from the latest attempt; cost/time accumulate across all attempts of the stage (every attempt
   is billed). `SquashAttempts` already sums — verify it's summing intentionally, not by accident.

`.gitignore` already excludes `.relay/*/stage*-attempt*/` and the per-attempt reports, so no
ignore changes are needed.

## Sequencing

- **Do this before `reveal-stage-artifacts-in-finder.md`.** That task reveals a stage's *latest*
  trace dir; until attempts increment and readers default to the latest attempt, "latest" is a
  directory full of merged stale sessions, so reveal would open ambiguous/wrong artifacts. With
  real per-run attempts it opens a clean single-session dir.
- **Coordinate with `surface-stage-error-in-detail-pane.md`** — both edit `RelayRunHistory`
  (`ReadStageMetric` / `SquashAttempts`, same file and adjacent regions). Land one first and
  rebase the other; don't develop them in parallel against the same code.

## Done when

- A first run of a stage produces `attempt1`; a re-run produces `attempt2` (etc.) — neither the
  prior report nor its trace sessions are overwritten or deleted.
- The trace / LLM-commands pane for a stage shows only the latest attempt's session(s); a re-run
  no longer intermixes traces from earlier runs.
- A stage with multiple attempts reports cumulative cost/time and the latest outcome; ordering
  is correct beyond `attempt9`.
- Unit coverage: next-attempt allocation (1 → 2), latest-attempt trace selection (no stale
  merge), cumulative cost across attempts, numeric ordering past nine. Write the failing tests
  first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
