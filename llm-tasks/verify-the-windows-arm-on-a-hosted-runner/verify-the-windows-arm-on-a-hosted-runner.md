# Run the Windows arm on the hosted runner and close the checklist

Every pure unit of the WSL sandbox is tested on macOS, and every fact that
needs a Windows machine is written down as unverified: the Landlock line at
runtime, the eight confinement verdicts on ext4 and on DrvFs with their
timings, the watchdog kill proof, the exit-code and UTF-8 round trip, the
templates-directory grant across DrvFs, the folder-picker UNC round trip, and
a task run end to end through the GUI. A manual workflow exists to produce
most of those facts on a hosted `windows-2025` runner and has never been
dispatched, and its action majors were copied rather than resolved. This task
dispatches it, reads every verdict off the artifact, decides the DrvFs policy
from probe 1b as the retired spec instructed, adds the two probes the workflow
does not yet run, and turns the checklist into a record.

## Evidence

- Brief of 2026-09-11, items 14 and 23; the retired spec's "Done when"
  (`llm-tasks/completed/windows-sandbox-via-wsl-nono/DONE-windows-sandbox-via-wsl-nono.md:204-218`);
  ea462fab retired it "with the proof outstanding".
- `TROUBLESHOOTING.md:212-227` lists the eight unverified items and `:244-278`
  the by-hand PowerShell recipe; `:214-216` says in bold that none of it was
  run. `docs/OPERATIONS.md:80-87` cites Microsoft's kernel config for Landlock
  and defers the runtime check to the gate.
- `.github/workflows/wsl-probe.yml` (one commit, d76d55d1, 2026-09-10; no run
  is visible from this guest because `gh` is unauthenticated here) installs
  Ubuntu with `--no-launch` (`:33`, root is the default user), keeps the VM up
  (`:43`), installs git, curl, procps, util-linux and nono 0.75.0 in the distro
  (`:52-57`), records the facts to `probe/wsl-facts.txt` (`:62-69`), runs
  `dotnet test --filter FullyQualifiedName~Wsl` with `VR_RUN_NONO_INTEGRATION=1`
  (`:71-81`) and uploads `probe/` (`:83-88`).
- Item 23 measured on 2026-09-11 from this guest with
  `git ls-remote --tags`: `actions/checkout` has `v5 v6 v7`,
  `actions/upload-artifact` has `v5 v6 v7`, `actions/setup-dotnet` has
  `v4 v5 v6`. All three majors the workflow names exist; the first dispatch
  still has to prove the `windows-2025` image, nested virtualization and
  `wsl --install` on it.
- `WslConfinementProbeTests.WorkspaceOnDrvFs_ProducesTheSpecVerdicts` (`:68-77`)
  asserts the DrvFs table equals the spec table, so by design it FAILS when
  DrvFs does not enforce ("a failure here is the finding that keeps DrvFs
  workspaces refused", `:76`); the workflow would read red for the expected
  finding. `WslWorkspacePolicy.MntPolicy` is `Refuse` (`Execution/Wsl/WslWorkspacePolicy.cs:22`)
  with `Warn` carried as the unused downgrade target (`:6-7`).

## Current state (researched at d5bf93cc)

- Gated classes and what each records: `WslConfinementProbeTests` (eight rows
  `:36-46`, per-row verdict and wall time written through `ITestOutputHelper`
  `:93-97`, on `$HOME/vr-probe-*` and on `%TEMP%` through DrvFs `:53-77`);
  `WslWatchdogKillsHungTreeTests.HungTree_StoppedThroughTheTreeControl_LeavesNoLinuxProcess`
  (`:32-77`: pgid leadership, TERM through the control, `ps -p` fails);
  `WslExitCodeAndUtf8Tests` (four facts, `:23-76`); `WslArgvRoundTripTests`
  (three, `:42-83`). Each skips unless Windows, opted in and a usable context
  (`SkipIfNotOptedIn`, e.g. `WslConfinementProbeTests.cs:138-145`;
  `tests/VisualRelay.Tests/NonoIntegration.cs:19`).
- Not probed by any test: the templates-directory grant (a Windows path,
  `%APPDATA%\visual-relay\templates` via `XdgConfig.ResolveConfigDir`
  `Configuration/XdgConfig.cs:21-22`, mapped by `SandboxHost.MapGrant` to
  `/mnt/c/Users/…` and passed as `-a`); the folder-picker UNC round trip at
  runtime; the GUI through the control API. No workflow uses a secret.
- `git ls-remote` reaches GitHub from this guest; `gh workflow run` needs the
  host session's credentials (memory note `guest-commit-identity-and-push-auth`).

## Prescribed approach

1. Two probes the workflow lacks, added before the first dispatch so one run
   answers everything. A ninth row in `WslConfinementProbeTests.Rows`,
   `templates dir write`: the test computes `TaskTemplates.ResolveUserTemplatesDir()`,
   maps it with `SandboxHost.Windows(wsl).MapGrant`, appends `-a <mount>` to
   the row's nono prefix and writes a file there (allowed); a tenth row
   writes there WITHOUT the grant (denied), so the grant is shown to be what
   allows it. And a second job `gui`, `needs: probe`, that runs only when the
   repository secret `DEEPSEEK_API_KEY` is set (mapped to an env var and
   checked in the step, since `if:` cannot read secrets): install `golang-go`
   in the distro, `git clone` gorilla/mux to `/root/mux` inside it, start
   `dotnet run --project src/VisualRelay.App` with `VR_CONTROL_PORT=8765`,
   wait for `/health`, then through the control API `open-folder` with
   `\\wsl.localhost\Ubuntu\root\mux` (the UNC round trip), `bootstrap`,
   `create-task` with the mux validation task text kept at
   `tests/VisualRelay.Tests/Fixtures/wsl-probe-task.md`, `run-selected`, poll
   `/state` until `isBusy` is false (45 minutes cap), `GET /screenshot` to
   `probe/gui.png`, copy `.relay/<task>/run.log` and `status.json` into
   `probe/`, and fail the job unless `git -C /root/mux log -1 --format=%B`
   inside the distro carries a `Relay-Seal:` trailer.
2. Dispatch from a host session: `gh workflow run wsl-probe.yml`,
   `gh run watch`, `gh run download -n wsl-probe -D /tmp/wsl-probe`. Read
   `wsl-facts.txt` (WSL version, kernel release, the LSM line, nono version,
   the tools), the console log for the two verdict tables with timings, the
   kill fact, the exit codes and the hex bytes, `gui.png` and the task's
   `run.log`. A red `WorkspaceOnDrvFs` is a measurement, not a broken run.
3. Decide `MntPolicy` from probe 1b by one rule: `Warn` if and only if every
   DrvFs row matches the spec table; otherwise `Refuse` stays. Either way the
   constant carries the run id and date in its comment, and
   `WorkspaceOnDrvFs_ProducesTheSpecVerdicts` becomes policy-aware: under
   `Refuse` it records the table and returns; under `Warn` it asserts parity.
   With `Warn`, `FolderPickerWslTranslation.Decide` already passes the
   message through as a warning (`Services/FolderPickerWslTranslation.cs:36-38`)
   and `WslSandboxLauncher.ResolveWorkspace` proceeds (`:60-63`); the
   `UnusedMember` suppression on `Warn` goes.
4. Close the checklist. `TROUBLESHOOTING.md:212-281` becomes "Windows runtime
   verification record": a table of the ten items with verdict, measured
   value (kernel release, the LSM line, the eight-plus-two verdicts on ext4
   and DrvFs, `git status` wall time on both, the pid that was gone, exit 42,
   the UTF-8 bytes, the UNC root the GUI opened, the sealed commit), the run
   id and the date; the by-hand recipe stays under "to reproduce", the bold
   "were not run" paragraph goes. `docs/OPERATIONS.md:80-93` states the
   measured kernel and LSM line and the decided DrvFs policy; README's Windows
   section and `AGENTS.md:59-65` change only if `Warn` (a drive folder opens
   with a warning). The action majors stay as they are, with the
   `ls-remote` lines above in the commit body: the repository pins majors
   everywhere, and existence was the only doubt.

Order: land `close-the-windows-entry-point-gaps` first if it is ready (its git
check would otherwise be the one gate line the record cannot show); this task
does not depend on it.

## Tests

- `WslConfinementProbeTests`: the two templates rows; `WorkspaceOnDrvFs_…`
  reads `WslWorkspacePolicy.MntPolicy` and asserts parity only under `Warn`.
  `WslWorkspacePolicyTests`: the existing facts follow the constant, plus
  `MntPolicy_IsTheDecisionTheRecordNames` pinning the chosen value with the
  run id in its name.
- `BuildNonoPrefixWslPathsTests.MapGrant_OnTheWslHost_MapsTheTemplatesDirToItsDrvFsMount`
  (pure, macOS).
- `FolderPickerWslTranslationTests.Decide_DrivePick_OnWindows_FollowsThePolicy`
  (replaces the `Refuse`-only fact when the policy changes; otherwise unchanged).
- On macOS `./visual-relay test Wsl` still skips every gated fact cleanly and
  `./visual-relay check` exits 0.

## Verification

The run itself is the verification: its URL, the artifact's `wsl-facts.txt`
and the two verdict tables go into the commit body verbatim (kernel release,
LSM line, nono version, ten rows times two filesystems with milliseconds, the
kill proof pid, exit 42, the hex bytes), plus the `gui` job's sealed commit
subject and the `open-folder` answer. If the `gui` job cannot run (no secret,
or the hosted image cannot open the share), the record says which items were
measured and which still need a person at a Windows machine, and the checklist
keeps exactly those lines.

## Out of scope

A physical Windows machine; Windows-toolchain repositories; the entry-point
and process-model items (their own tasks); making the probe run on every
push; `release.yml`'s own action pins.

## Rejected alternatives

- Pin the actions to commit SHAs: the majors resolved, `release.yml` uses the
  same style, and a SHA pin is a maintenance rule this repository has not
  adopted anywhere else.
- Decide `MntPolicy` on timings as well as verdicts: the warning text already
  names the cost, and an operator who accepts a slow DrvFs workspace after
  reading it has been told; enforcement is the only fact that can make the
  refusal necessary.
- Keep the DrvFs probe as a hard assertion: a workflow that is red whenever
  the platform behaves as measured hides every other verdict behind one
  expected failure.
- Skip the GUI job and call the unit-level probes enough: the retired spec's
  first "Done when" line is a task committed through the desktop app on a
  Windows box, and nothing else on the list proves the UNC root, the share
  and the control API together.
