# Follow-up triage, 2026-09-11

Where every open item from the 2026-09-10/11 session went. Items are numbered
as in the brief (`followups-brief.md`); each maps to the task under
`llm-tasks/` that carries it, or says why it has none. The task runner skips
this directory: it is a record, not a task.

Revised on 2026-09-17 after the task list was pruned: specs already done were
deleted, the Windows items overtaken by the real Windows PC runs were dropped,
and language-specific specs were replaced by general ones.

| # | Item (short) | Carried by |
| --- | --- | --- |
| 1 | `reset-selected` leaves the flagged run's staged work in the tree | `reset-selected-restores-the-working-tree` |
| 2 | Bootstrap's smoke run dirties the target tree (insta `.snap.new`) | `bootstrap-smoke-leaves-the-tree-clean` |
| 3 | No `testFileCmd` seeded for cargo, go, dotnet | `prove-the-per-file-test-command-at-bootstrap` (one general mechanism instead of a snippet per toolchain) |
| 4 | Dead rspec/phpunit/mix forms; jest/vitest forms drop the project's flags | rspec and phpunit rules landed (fbf242f7, 87d053a1); the rest in `prove-the-per-file-test-command-at-bootstrap` |
| 5 | Should a red gate suppress the audit's re-ask | Nothing to do. Decided: no; `RelayDriverStage5AuditTests.Stage5_AuditReportsAHunk_ReasksExactlyOnce` already pins it |
| 6 | Advisory calls get the write-capable tool set | `advisory-calls-answer-in-one-tool-less-turn` |
| 7 | Absolute manifest entries silently mangled | `absolute-plan-paths-resolve-or-are-reported` |
| 8 | `verify_result.attempt` diverges from trace attempts on stages 10/11 | Done in bc714feb; the one docs sentence left rides with `absolute-plan-paths-resolve-or-are-reported` |
| 9 | The `[Fact]` count baseline in a 300-line file | `retire-the-fact-count-baseline` |
| 10 | `create-task` while the archive view shows | `create-task-switches-to-the-queue-view` |
| 11 | A stage answer lacking a required contract key is flagged, not re-asked | Done in a9ff2fa2; spec deleted 2026-09-17 |
| 12 | `reasoning_effort: none` for one-shot advisory calls | `advisory-calls-answer-in-one-tool-less-turn` |
| 13 | README screenshots stale; `check` regenerates and the diff is reverted | `render-readme-screenshots-deterministically` |
| 14 | The Windows runtime verification (the big one) | Dropped 2026-09-17: the real Windows PC runs of 2026-09-13/14 overtook the hosted-runner plan. `TROUBLESHOOTING.md` still calls the checklist unverified |
| 15 | The WSL gate never checks `git` inside the distro | `close-the-windows-entry-point-gaps` |
| 16 | `sandboxExtraAllowPaths` resolved against the Windows profile | Done in d3415218 |
| 17 | `open-folder` bypasses the folder-pick policy | Done in 8a7a8bfa |
| 18 | Bootstrap without a usable distro smoke-runs through `cmd.exe` | `close-the-windows-entry-point-gaps` |
| 19 | `SandboxHost.Current` read synchronously on driver threads | Dropped 2026-09-17 with 20 to 22: code-reading findings that two days of real Windows runs did not hit |
| 20 | A `wsl.exe` per CPU sample | Dropped (the sampler itself changed in 7b7af51d) |
| 21 | Git in the distro has no tree control; a timed-out git is orphaned | Dropped |
| 22 | The flagged-work bundle path assumes the root is the top level | Dropped |
| 23 | The probe workflow's action majors were never resolved | Dropped with 14 (measured 2026-09-11: all three majors exist; the workflow was never dispatched) |

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
2. `bootstrap-asks-the-model-when-no-test-command-passes` before
   `prove-the-per-file-test-command-at-bootstrap`, which reuses its seam.
3. The rest are independent of each other.

## Decisions taken while writing, so nobody re-litigates them

- Advisory calls are tool-less single turns with reasoning off; a read-only
  tool catalog was rejected (a one-turn call with any catalog spends the
  turn calling it, c68fd37d).
- The audit's re-ask is not suppressed by a red gate: a red earned by a hunk
  that does not compile is not proof.
- `check` never writes `docs/images/`; `./visual-relay screenshot` is the
  deliberate refresh, and `check` proves the render is deterministic.
- Visual Relay is a general-purpose tool (2026-09-17): a spec that teaches it
  one language's project files or one tool's cache folders is a sign to look
  for a general mechanism instead.
- Visual Relay is for repositories whose suite is already green (2026-09-17):
  failures a repository has on its base commit are fixed before it is used,
  not worked around by the pipeline.
