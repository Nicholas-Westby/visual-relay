# Give the verify snapshot its own node_modules cache folders

The verify snapshot links a large ignored entry such as `node_modules` to the
checkout's real folder instead of copying it. Tools that write caches inside
`node_modules` then write into the real checkout, which the sandbox mounts
read-only for the verify run. vite (and so vitest) writes its bundled config
to `node_modules/.vite-temp` before any test starts, so on the Windows arm a
vitest suite failed at startup with EACCES, named no test, and the task went
to Fix-verify, whose agent "repaired" it by changing the real `node_modules`
permissions (`chmod 555`, so vite falls back to another folder). That
workaround stayed on disk and hid the problem for the next task.

## Evidence

Windows arm, i18next, 2026-09-14 (0.367). Evidence on the Windows PC at
`~/vr-eval/i18next-run1-evidence`; the stage 10 output sample was sent to the
Mac as `i18next-verify-output.txt` (1929 bytes, sha256 25d40500...be1bc).

- Task 1 stage 10 (06:59) and, after a restart, stage 10 attempt 2 (07:34):
  vitest printed its startup error for `node_modules/.vite-temp` (EACCES,
  permission denied) and exited 1 with no test run. Stage 5 and 6 vitest runs
  in the checkout itself had recreated `.vite-temp` in between.
- Fix-verify attempts ran `rm -rf node_modules/.vite-temp && chmod 555
  node_modules` on the real checkout; the change is invisible to the task
  diff. The operator restored mode 755 twice.
- Mac runs never hit this: on APFS the snapshot is a clone, so `node_modules`
  is a real copy there.

## Current state (researched at 37403a8a)

- `Execution/RelayDriver.VerifyWorktreeOverlay.cs:20-40`: the ignored overlay
  copies small ignored entries and SYMLINKS large ones (`node_modules`,
  `.venv`, `vendor`) to the checkout; `RelayDriver.VerifyWorktree.cs:151,170`
  document the same choice.
- `Execution/RelayDriver.VerifyWorktreeCopy.cs:40-50` creates those links;
  `RelayDriver.VerifyWorktreeInDistro.cs` does the copy inside the distro on
  the Windows arm; `RelayDriver.VerifyWorktreeClone.cs` is the APFS clone path.
- `Execution/RelayDriver.VerifyWorktreeCleanup.cs:15` removes the links first
  so cleanup never deletes the real folder's contents; any new layout must keep
  that property.
- A red run whose output names no failing test goes straight to Fix-verify
  (`RelayDriver.BaselineVerify.cs:21-22` returns "verify failed").

## Prescribed approach

1. For a large ignored directory named `node_modules`, create a real
   directory in the snapshot and link each of its entries to the checkout's
   entry, except cache folders tools write into during a run: `.vite-temp`,
   `.vite`, `.cache`. Those are left absent, so each tool creates its own
   inside the snapshot. `.bin`, `.pnpm`, `.modules.yaml`,
   `.package-lock.json` and every package stay links. Apply this on the copy
   path and the in-distro copy path; the clone path already copies.
2. Cleanup removes the per-entry links before deleting the snapshot folder
   (extend the rule at `VerifyWorktreeCleanup.cs:15`), and a test proves the
   real `node_modules` keeps its contents.
3. Publish the layout in the existing `verify_snapshot_created` event
   (`node_modules=entries-linked`) so a log shows which layout ran.
4. Separately and smaller: when a red verify names no failure and its output
   contains "EACCES" or "permission denied" on a path outside the task's
   files, say so in the flag or Fix-verify input ("the run failed before any
   test started: <line>") instead of the bare "verify failed".

## Tests

- `VerifyWorktreeIgnoredOverlayNodeModulesTests` (new): a checkout with
  `node_modules/pkg/index.js`, `node_modules/.bin/tool` and
  `node_modules/.vite-temp/x` above the size threshold; the snapshot's
  `node_modules` is a real directory, `pkg` and `.bin` resolve to the
  checkout, `.vite-temp` is absent, and writing
  `snapshot/node_modules/.vite-temp/y` leaves the checkout untouched; cleanup
  leaves the checkout's `node_modules` intact.
- The in-distro variant is covered by the Windows test matrix; mark it
  `SkipUnless` the host as the existing in-distro tests do.

## Verification (through the control API)

Windows arm, i18next reset to 4ebd19d with `node_modules` at mode 755 and a
fresh `node_modules/.vite-temp` created by running `npx vitest run` once in
the checkout. `run-all` one task. Expected: stage 10's verify output starts
with `RUN v4.1.11` and runs the suite (no EACCES), the checkout's
`node_modules` is still 755 afterwards, and `verify_snapshot_created` shows
the new layout.

## Out of scope

The Fix-verify prompt rules about environment errors (carried by
`subtract-base-failures-in-fix-verify`); Python `.venv` caches (no failure
seen).
