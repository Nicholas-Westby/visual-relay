# Timing-sensitive tests flake under parallel load and fast disks — deflake at the root

Observed costs, 2026-06-11: the pin-swival-profile run's stage 10 spent a 64m53s /
116-turn fix-verify cycle re-running the full suite ~8 times to convince itself the
failures it saw were pre-existing flakes, and hand-run suites on a /tmp (native-disk)
worktree showed varying failures (1, 3, 2, then 0 across four consecutive runs) that
all pass instantly in isolation. Flagged offenders so far:

- `RelayDriverPlanOnlyTests.RunTaskAsync_ResumeFrom5_AfterPlanOnly_SealChainUnbroken`
- `MainWindowViewModelTests.LoadRunHistoryAsync_CompletedRun_AllStagesShowComplete`
- a v22-ledger report naming "watchdog timing, CPU sampler, baseline-verify" failures
  as pre-existing flakes (630/633)

Pattern: failures appear under PARALLEL xUnit execution (class-level parallelism) and
shifted timing profiles (fast native disk vs virtio-fs latency; CPU contention from
the suite's own CPU-burn watchdog tests, added 227b937). All pass solo → these are
intra-suite races, not product bugs. They now tax every stage-9/10 gate run and every
hand verification.

Primary mechanism identified 2026-06-11: process-global environment mutation. The
suite has 13 `Environment.SetEnvironmentVariable` call sites concentrated in
`KeyEnvFileTests` (8) and `KeySetupPanelUiTests` (5), including temporarily NULLING
`MOONSHOT_API_KEY`/`DEEPSEEK_API_KEY` and redirecting config-home style variables.
Environment is process-wide while classes run in parallel — any concurrently running
test whose subject reads key/config env (MainWindowViewModel init and its
readiness/key-setup branching; config/seal paths in driver tests) sees a randomly
mutated environment for a few milliseconds. This explains sub-second failures that
pass in isolation. Fix shape for this class: route env access through an injectable
accessor for the code under test, or put ALL env-mutating tests in one serialized
xUnit collection together with every env-READING test family — never raw global
mutation racing parallel readers. CPU-burn isolation remains a secondary fix.

## Goal

The full suite is deterministic: N consecutive full-suite runs (say 5) pass on both
the repo's virtio-fs checkout and a /tmp native-disk worktree, with no test relying
on wall-clock generosity to survive parallel siblings. No blanket serialization of
the whole suite and no deleted/skipped tests — each flaky test gets a root-cause fix:
fake/injectable clocks or pulse sources instead of real sleeps where the subject
allows it; explicit xUnit collections to isolate genuinely CPU-heavy tests (the
cpu-pulse burn tests) from timing-assertion tests; **await the real operation `Task`
— do not poll for its side effects.**  A `Func<bool>` poll on a wall-clock budget
(e.g. 50×20 ms = 1 000 ms cap) is itself a flake source under parallel CPU load:
the scheduler-starvation watchdog tests can push the awaited I/O past the cap and
the poll false-fails.  Await the operation Task directly (e.g. `await viewModel.
LastSelectionLoad`, `await viewModel.SelectTaskAsync(task)`, or `await command.
ExecuteAsync(null)` for an `IAsyncRelayCommand`).  Condition-polling helpers are
**banned** — see `harness-await-not-poll-async-tests` and `BannedSymbols.txt`.
For `[AvaloniaFact]` binding settle after an await, a single non-looping
`Dispatcher.UIThread.RunJobs()` flush is the only sanctioned "settle".

## Approach (suggested)

- Reproduce first: run the suite in a loop capturing per-test failures to identify
  the full flaky set (the list above is what got noticed, not necessarily all).
- Classify each: needs fake time / needs isolation collection / needs condition-poll
  wait. Fix accordingly; keep the real-process watchdog integration tests but put
  CPU-burners in an isolated serial collection so they cannot starve timing tests.
- Acceptance: scripted 5x consecutive green full-suite runs (document the loop in
  the test README or a tools/guards script so the gate can reuse it cheaply later).
