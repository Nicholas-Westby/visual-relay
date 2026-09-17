# Follow-up triage, 2026-09-14 (Mac and Windows validation session)

Where every open item from the 2026-09-13/14 cross-machine session went. The
runs drove VR through the control API on real repositories on the Mac and on
the Windows arm (nono inside WSL2). Larger items have their own task; smaller
ones are listed here with their evidence so they are not lost. The task
runner skips this directory: it is a record, not a task.

Revised on 2026-09-17 after the task list was pruned (see the decisions at the
end of `2026-09-11-followup-triage.md`).

## Carried by tasks

| Item (short) | Carried by |
| --- | --- |
| Fix-verify's check is exit-code only; its prompt demands exit 0, so agents "fix" old failures (i18next TZ pin, twice) | Dropped 2026-09-17: Visual Relay is for repositories whose suite is green, so failures on the base commit are fixed before it is used |
| Fix-verify agents changed permissions on the real `node_modules` and copied a workaround out of `run.log` | `copy-ignored-folders-the-same-way-inside-wsl` (the prompt rule) |
| vite's `.vite-temp` EACCES in the verify snapshot (linked `node_modules`, Windows arm) | `copy-ignored-folders-the-same-way-inside-wsl` (the in-distro copy follows the app's own rule; no folder or tool is named) |
| Nothing reviews Fix-verify's edits before Commit | `review-what-fix-verify-changed` |
| Stale index entries after a rejected commit; `.relay/` staged by restore; green verify with a failure reason; silent restart after resolver failure; halt survives a successful resume | `make-resume-after-a-rejected-commit-consistent` |
| `/state` shows idle during bootstrap; `/state` stalled 20 s after cancel | `keep-state-responsive-and-accurate-during-bootstrap-and-cancel` |
| Ocelot: no usable `dotnet test` form for a Microsoft.Testing.Platform repo whose solutions refuse | `bootstrap-asks-the-model-when-no-test-command-passes` (a general fallback instead of .NET project scanning) |

## Smaller follow-ups (no task yet)

| Item | Evidence | Suggested handling |
| --- | --- | --- |
| Windows arm has not run 0.373 to 0.379 | Windows stopped on tree 141603852f (0.372); 0.373+ carries Testing Platform ids, dotnet bootstrap refusals, per-solution candidates, bootstrap check in the user's environment, project-label ids | Sync the Windows checkout once the snapshot task lands, and rerun on a repository that is green on that machine (i18next has three time-zone failures there unless TZ is set) |
| A repo-wide prettier check fails on VR's own untracked `.relay/config.json` | i18next on Windows: `.relay/config.json` is negated in `.relay/.gitignore`, so prettier sees it | Write the config in prettier's default style, or add `.relay` to the format check's ignore list when the check is prettier |
| `run.log` `reason=` fields keep raw ANSI escapes | i18next stage 5 `verify_result reason=ESC[41mESC[1m FAIL ...` | Strip escapes where the reason is built (the id reader already has the pattern) |
| Agents can read `.relay/<task>/run.log`, including earlier agents' reasoning | i18next Fix-verify attempt 3 grepped it for `chmod` | Decide whether stage agents should see `run.log` at all; the sandbox grants the repo cwd |
| The verify snapshot id stays `<task>-verify-s10-a1` on every resume | Mac notes, 2026-09-14 | Name it by attempt, like the output file, so a crash leftover cannot collide |
| A plan-stage flag resumes by re-running all planning | Mac notes | Resume from the flagged planning stage |
| `VerifyWorktreeRecursive` swallows a failed symlink with no event | `Execution/RelayDriver.VerifyWorktreeRecursive.cs:55` | Publish a warn event like the copy-budget skip |
| CLI `InitCommand.IsExecutable` is true for any existing file on Windows | Windows portability notes | Check PATHEXT |
| Host seams: `PlanningWorktree`, `FlaggedWorkStore` and CLI `NonoGate.Require` read the real host | Windows portability notes | Inject the host as elsewhere |
| `BaselineGuardGate`'s refusal quotes a raw 200-char tail that lands on nono's JSON | swift-format on the Mac | Use `SandboxedStage.ExtractFailureReason` |
| ILSpy blocked: NUnit scans parent folders the sandbox denies | Windows, icsharpcode/ILSpy | Needs a grant decision; not retried |
| PHP (FreshRSS composer script) and nested Makefile (crawl) bootstrap to the placeholder | Windows | The composer rule landed in 87d053a1; nested Makefiles fall to `bootstrap-asks-the-model-when-no-test-command-passes` |
| jest's `● Validation Warning` and mocha's failure lines are not read as failure ids | Mac samples | Add patterns from real output when a mocha repo is in the set |
| nono's command blocking does nothing for VR's commands | Every command runs as `/bin/sh -c`, so a blocked-command list never matches; nono deprecated the feature | Document it; rely on filesystem and network rules |
| wsl.exe once printed "Failed to start the systemd user session" | Windows, first call after an idle gap; nothing failed | None unless it recurs with a failure |
| `FdLeakTests.StoppedCommand` comment says "unlike the scripts above" in its own partial file | Mac, 0.362 | Reword when the file is next touched |
| Ocelot run itself (task for #2143, upstream path templates with `$`) never started | Bootstrap problems above took the time | Run once `bootstrap-asks-the-model-when-no-test-command-passes` lands |
