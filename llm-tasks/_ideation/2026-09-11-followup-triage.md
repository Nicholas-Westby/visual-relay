# Follow-up triage, 2026-09-11

Where every open item from the 2026-09-10/11 session went. Items are numbered
as in the brief (`followups-brief.md`); each maps to the task under
`llm-tasks/` that carries it, or is dropped with the reason. Twelve tasks
carry twenty-three items. The task runner skips this directory.

| # | Item (short) | Carried by |
| --- | --- | --- |
| 1 | `reset-selected` leaves the flagged run's staged work in the tree | `reset-selected-restores-the-working-tree` |
| 2 | Bootstrap's smoke run dirties the target tree (insta `.snap.new`) | `bootstrap-smoke-leaves-the-tree-clean` |
| 3 | No `testFileCmd` seeded for cargo, go, dotnet | `per-file-test-commands-for-dotnet-go-and-node` (cargo stays null, by decision) |
| 4 | Dead rspec/phpunit/mix forms; jest/vitest forms drop the project's flags | `per-file-test-commands-for-dotnet-go-and-node` |
| 5 | Should a red gate suppress the audit's re-ask | `stage-five-gate-corrections` (decided: no; pinned with the reason) |
| 6 | Advisory calls get the write-capable tool set | `advisory-calls-answer-in-one-tool-less-turn` |
| 7 | Absolute manifest entries silently mangled | `stage-five-gate-corrections` |
| 8 | `verify_result.attempt` diverges from trace attempts on stages 10/11 | `stage-five-gate-corrections` |
| 9 | The `[Fact]` count baseline in a 300-line file | `retire-the-fact-count-baseline` |
| 10 | `create-task` while the archive view shows | `create-task-switches-to-the-queue-view` |
| 11 | A stage answer lacking a required contract key is flagged, not re-asked | `re-ask-once-when-a-contract-key-is-missing` |
| 12 | `reasoning_effort: none` for one-shot advisory calls | `advisory-calls-answer-in-one-tool-less-turn` |
| 13 | README screenshots stale; `check` regenerates and the diff is reverted | `render-readme-screenshots-deterministically` |
| 14 | The Windows runtime verification (the big one) | `verify-the-windows-arm-on-a-hosted-runner` |
| 15 | The WSL gate never checks `git` inside the distro | `close-the-windows-entry-point-gaps` |
| 16 | `sandboxExtraAllowPaths` resolved against the Windows profile | `close-the-windows-entry-point-gaps` |
| 17 | `open-folder` bypasses the folder-pick policy | `close-the-windows-entry-point-gaps` |
| 18 | Bootstrap without a usable distro smoke-runs through `cmd.exe` | `close-the-windows-entry-point-gaps` |
| 19 | `SandboxHost.Current` read synchronously on driver threads | `finish-the-wsl-process-model` |
| 20 | A `wsl.exe` per CPU sample | `finish-the-wsl-process-model` (throttled, not streamed) |
| 21 | Git in the distro has no tree control; a timed-out git is orphaned | `finish-the-wsl-process-model` |
| 22 | The flagged-work bundle path assumes the root is the top level | `finish-the-wsl-process-model` (computed against the top level; proven on git 2.50.1) |
| 23 | The probe workflow's action majors were never resolved | `verify-the-windows-arm-on-a-hosted-runner` (measured 2026-09-11: `checkout@v7`, `upload-artifact@v7`, `setup-dotnet@v6` all exist) |

## Dropped, with agreement

- The retired `deepseek-v4-pro` and `deepseek-v4-flash-vision-exp` aliases:
  dropped: landed in 5e1cc770 and d5bf93cc; nothing left to do.
- `.venv` candidates being POSIX-shaped (`.venv/bin/...`): dropped: Windows
  runs every command inside the distro by design, where the POSIX shape is
  the right one.

## Order that pays off

1. `retire-the-fact-count-baseline` first: every other task adds facts to the
   families it counts, and each would otherwise need a dated bump in a file
   with no room.
2. `stage-five-gate-corrections`, `per-file-test-commands-for-dotnet-go-and-node`
   and `re-ask-once-when-a-contract-key-is-missing` next; they change what
   the three validation repositories record and are worth one more run of
   the nine tasks together.
3. `advisory-calls-answer-in-one-tool-less-turn`, `create-task-switches-to-the-queue-view`,
   `reset-selected-restores-the-working-tree`, `bootstrap-smoke-leaves-the-tree-clean`
   and `render-readme-screenshots-deterministically` are independent of each
   other and of the above.
4. Windows: `close-the-windows-entry-point-gaps` and `finish-the-wsl-process-model`
   before `verify-the-windows-arm-on-a-hosted-runner`, so the one dispatch
   measures the arm as it will ship; the runner task does not strictly
   depend on either.

## Decisions taken while writing, so nobody re-litigates them

- Advisory calls are tool-less single turns with reasoning off; a read-only
  tool catalog was rejected (a one-turn call with any catalog spends the
  turn calling it, c68fd37d).
- The audit's re-ask is not suppressed by a red gate: a red earned by a hunk
  that does not compile is not proof.
- `sandboxExtraAllowPaths` on Windows: `~/…` resolves against the distro
  home; a `/`-rooted entry is rejected with the hint to write it as `~/…`.
- The CPU sampler behind wsl.exe is throttled to one `ps` per 15 s, not
  replaced by a streaming process.
- The flagged-work bundle's fetch argument is computed with
  `git rev-parse --show-prefix`; refusing a non-top-level root is a separate
  gate question and stays open.
- `check` never writes `docs/images/`; `./visual-relay screenshot` is the
  deliberate refresh, and `check` proves the render is deterministic.
- `MntPolicy` is decided by verdict parity on DrvFs alone; timings inform the
  warning text, not the decision.
- The workflow's action majors stay as majors.
