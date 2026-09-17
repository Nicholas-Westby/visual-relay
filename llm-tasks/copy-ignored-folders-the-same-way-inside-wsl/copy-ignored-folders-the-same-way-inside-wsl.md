# Copy ignored folders into the verify snapshot the same way inside WSL

Verify runs the suite in a throwaway snapshot of the checkout, and the
snapshot has to contain the git-ignored folders a suite needs (dependencies, a
virtualenv). The app's own copy code handles a large ignored folder with care:
the folder becomes a real directory in the snapshot and its children are
copied or linked one by one, so a tool can still create files in it. On the
Windows arm the copy is done by a short shell script inside the distro, and
that script is coarser: a large folder is linked whole. The sandbox mounts the
real checkout read-only for verify, so any tool that writes into such a folder
fails before a test runs. vite did exactly that on i18next, the red run named
no test, and the Fix-verify agent "repaired" the environment by changing
permissions on the real checkout. This task brings the script in line with the
app's rule, makes a failure of this kind say what it is, and tells the
Fix-verify agent that the environment is not its to repair. No tool or folder
name is involved.

This spec replaces `give-the-verify-snapshot-its-own-node-modules-caches`
(2026-09-14), which special-cased a folder named `node_modules` and three
cache names (`.vite-temp`, `.vite`, `.cache`), and it carries the Fix-verify
prompt rule from `subtract-base-failures-in-fix-verify`, which was dropped on
2026-09-17 (Visual Relay is for repositories whose suite is already green).

## Evidence

Windows arm, i18next (vitest 4.1.11), 2026-09-14, builds 0.367 to 0.372.
Evidence on the Windows PC at `~/vr-eval/i18next-run1-evidence`.

- Stage 10, twice: vitest printed its startup error for
  `node_modules/.vite-temp` (EACCES, permission denied) and exited 1 with no
  test run. The flag reason and the Fix-verify input said only "verify
  failed".
- Fix-verify attempts ran `rm -rf node_modules/.vite-temp && chmod 555
  node_modules` on the real checkout. The change is invisible to the task's
  diff, it stayed on disk, and it hid the problem from the next task. A later
  attempt found that workaround by reading `.relay/<task>/run.log` and
  repeated it.
- Mac runs never hit this: on APFS the snapshot's ignored folders are
  copy-on-write clones, real and writable.

## Current state (researched at f8a0d07b)

- The app-side rule, `Execution/RelayDriver.VerifyWorktreeRecursive.cs:47-126`
  `OverlayIgnoredDirRecursive`: an ignored directory always becomes a REAL
  directory in the snapshot (`:78`); each child directory at or above the
  limit is linked whole (`:71-75`), a smaller one is recreated and walked;
  files are copied until the entry's copy budget (the same limit) is used up,
  after which the rest is linked with a `verify_overlay_skipped` advisory
  (`:62-68`). The limit is 64 MB (`RelayDriver.VerifyWorktree.cs:153`).
- The in-distro rule, `Execution/Wsl/WslTreeCopy.cs:25-32`
  `IgnoredEntriesScript`: per ignored entry, `du -sk`; below the limit
  `cp -a`, otherwise `ln -s` of the WHOLE entry.
  `RelayDriver.VerifyWorktreeOverlay.cs:45-50` takes this path whenever the
  workspace and the snapshot are shares of the sandbox's distro, and the
  app-side walk never runs there.
- `WslTreeCopyScriptTests` runs the script under a real `/bin/sh` on macOS
  (`IgnoredEntries_LargeDirectory_IsLinkedToTheSource` pins today's whole
  link).
- Cleanup is already safe for nested links: `Execution/WorktreeLinks.cs:18-44`
  `UnlinkAll` removes every link at any depth before anything is deleted, and
  never walks through one (`RelayDriver.VerifyWorktreeCleanup.cs:17`).
- A red run that names no failing test becomes the bare "verify failed"
  (`RelayDriver.BaselineVerify.cs:21-22`, `RelayDriver.cs:246`).
- The Fix-verify prompt (`Execution/RelayStages.cs:144-157`) tells the agent to
  resolve a non-test gate "legitimately" and says nothing about the
  environment, file permissions, dependency folders or VR's own logs.

## Prescribed approach

1. One rule on both arms. `IgnoredEntriesScript` treats a directory at or
   above the limit the way the app does: `mkdir` it in the snapshot, then for
   each child (dot names included, `.` and `..` excluded) copy it with `cp -a`
   when it is below the limit and the entry's running copy total is still
   under the limit, else `ln -s` it to the source. Entries below the limit,
   large files, existing destinations and the `vr-overlay-failed` reporting
   stay as they are. The script's doc comment names
   `OverlayIgnoredDirRecursive` as the rule it mirrors, and that method's
   comment names the script, so the two are changed together from now on.
2. Cleanup keeps its property. No code change is expected; a test proves that
   after the new layout is cleaned up the source folder still has every file.
3. The snapshot event says which layout ran: `verify_snapshot_created` gains
   `overlay=app` or `overlay=in-distro`.
4. An environment failure is named. When a red verify names no failing test
   and its output holds an operating-system refusal (`EACCES`, "permission
   denied", "operation not permitted", "read-only file system"), the reason is
   `the run failed before any test started: <that line>` instead of "verify
   failed", in the flag and in Fix-verify's input alike.
5. The Fix-verify prompt gains: "An error that comes from the environment (a
   permission refused on a dependency folder, a missing tool, a failed
   download) is not this task's to fix. Report it in your summary and stop.
   Never change file permissions or anything inside a dependency folder, and
   never repeat a workaround you find in `.relay` logs."

## Tests

- `WslTreeCopyScriptTests` (real `/bin/sh`, as the existing facts):
  `IgnoredEntries_LargeDirectory_IsARealDirectoryOfLinkedAndCopiedChildren`
  replaces the whole-link fact (a folder above the limit with a large child, a
  small child and a dot-named child: the folder is real, the large child is a
  link, the small and dot-named ones are copies);
  `IgnoredEntries_AFileCreatedInTheSnapshotsFolder_DoesNotReachTheSource`;
  `IgnoredEntries_TheCopyBudget_LinksWhatIsLeft`;
  `IgnoredEntries_Cleanup_LeavesTheSourceFolderIntact` (through
  `WorktreeLinks.UnlinkAll` and a recursive delete).
- `VerifyWorktreeIgnoredOverlayCopyTests`: one fact asserting the app-side
  walk and the script produce the same layout for the same fixture tree (kind
  of each top-level child: real, copy or link).
- `RelayDriverBaselineFailureIdTests` (or the class that owns the stage 10
  reason): output with `EACCES: permission denied, mkdir '...'` and no test
  name gives the new reason; output that names a failing test is unchanged.
- The prompt test beside the existing `SandboxedStage.Prompt` tests: the
  Fix-verify system prompt contains the environment rule.
- Watch each fail first.

## Verification (through the control API)

Windows PC, the next time it is synced: i18next reset to a clean clone with
`node_modules` at mode 755, and `npx vitest run` run once in the checkout so a
stale `node_modules/.vite-temp` exists. `run-all` one small task. Expected:
stage 10's verify output starts with vitest's `RUN` banner and runs the suite,
`verify_snapshot_created` carries `overlay=in-distro`, and the checkout's
`node_modules` is still mode 755 afterwards with nothing new in it. On the
Mac, run one task in any node project to confirm the app-side path is
unchanged (`overlay=app`). Put the event line and the two modes in the commit
body.

## Out of scope

Whether stage agents should be able to read `.relay/<task>/run.log` at all (a
sandbox grant question of its own); the copy limit's value; the APFS clone
path; hard-link copies.

## Rejected alternatives

- Special-casing `node_modules` and a list of cache folder names, as the
  replaced spec asked: the next tool or ecosystem needs another name, while
  the app-side rule already solves the general case.
- A hard-link copy (`cp -al`) of large folders: a file changed in place
  through a hard link changes the real checkout, and the sandbox cannot tell a
  hard link from the snapshot's own file.
- Granting the sandbox write access to the real dependency folder: verify
  must not be able to change the checkout.
