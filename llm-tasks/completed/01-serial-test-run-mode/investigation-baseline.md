# Baseline data — 2026-07-19 slow-test investigation

Why per-test timings from the parallel suite cannot be trusted, with the
measurements behind this task batch. All "solo" numbers were taken with
`NO_BUILD=1 ./visual-relay test <ClassName>` (one class per invocation,
sequential) on the guest VM on 2026-07-19; TRX files are in `test-logs/`
(`20260719T13*`). Reported numbers come from full parallel runs.

## Full-suite parallelism signature

| Run | Tests | Sum of per-test durations | Wall clock |
|---|---|---|---|
| Host `20260712T222826_Mac_17600.trx` | 3,174 | 5,204s | 91s |
| VM `20260718T124658_…_5259.trx` | 3,306 | 5,194s | 92s |

~56 tests are "in flight" at any moment; a reported duration is mostly queue
time between awaits. Await-heavy tests (the full-pipeline driver family has
~100 async artifact writes per run) absorb the most queuing, which is why they
uniformly report 25-30s.

## Reported vs solo (selected)

| Test | Reported (host run) | Solo |
|---|---|---|
| RelayDriverVerifyFixTests.RunTaskAsync_VerifyGreen_… | 29s | 0.02s |
| NoCommitContaminationTests.…TwoTasks… | 32s | 0.19s (parametrized rows 0.05s) |
| RelayDriverNonResumeStaleStateTests.…ArchivesAndRunsFresh | 35s | 0.04s |
| ControlServerKestrelHandlerTests.UnknownRoute_Returns404_Json | 20s | 0.25s |
| GitCommitterProbeRetryTests.CommitAsync_ProbeFailsTwice… | 5s | 0.02s |
| RewriteHistoryRunnerTests.RunAsync_RewritesNonConforming… | 37s | 0.96s |
| GitCommitterRunBaseSquashGuardsTests.…RestoresOrigHead | 5s | 2.54s (real retry sleep) |

The same test also swings wildly between full runs (Kestrel handler test:
0.0s in the July 12 host run vs 20s in the July 18/19 one; FdLeak sampler:
0.1s vs 8s) — scheduling luck, not test changes.

## Full cause map

The complete per-test cause classification produced by the investigation was
delivered as `slow-test-causes.csv` (user's Desktop, 2026-07-19): columns
Real Git / Retry Sleep / Real Process / Roslyn Parse / UI Thread /
Full Pipeline / Parallel Scheduling, with reported and solo seconds per test.
