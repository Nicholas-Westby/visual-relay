# Close the two Windows entry-point gaps that are still open

On Windows every sandboxed launch and every workspace git call runs inside the
WSL distro. Four entrances into that design were found open on 2026-09-11.
Two have been closed since: `sandboxExtraAllowPaths` now resolves against the
distro home (d3415218), and `POST /command/open-folder` applies the same
folder-pick policy as Browse (8a7a8bfa). Two are left. The gate that decides a
distro is usable never asks whether `git` exists there, although every
workspace git call runs there. And bootstrap on a Windows machine without a
usable distro checks the test command through `cmd.exe` and writes a `testCmd`
the pipeline can never run there. Neither needs a Windows machine to be
pinned.

## Evidence

- Review of 2026-09-11, items 15 and 18, both code-reading findings. The real
  Windows runs of 2026-09-13/14 did not hit either, because that machine had
  git and a working distro from the start; `README.md:53-58` (a27bc752) now
  tells a new user to `sudo apt install -y git curl` inside the distro, which
  is the only protection today.
- `Execution/Wsl/WslProber.cs:45-134` runs seven steps (listing, `uname -r`,
  `command -v nono` through a login shell, `nono --version`, nono's Landlock
  check, `$HOME`, and the login shell's PATH at `:127`); none asks for git.
  `WslProbe.IsUsable` (`WslProbe.cs:46-47`) is the conjunction of the first
  six. `Execution/Wsl/GitRouting.cs:64` puts a bare `git` into the argv of
  every routed call, and `GitInvoker.cs:72-86` runs it with the login shell's
  PATH (fe0b406d). `WslGate.FirstFailingCheck`
  (`tools/VisualRelay.Cli/Gates/WslGate.cs:34-76`) has no git line, and the
  gate table in `TROUBLESHOOTING.md:172-180` has no git row.
- `Execution/ShellTestRunner.cs:71-82`: with `host.Wsl` null the launch is
  `BuildShellLaunch(command, host.IsWindows, ...)`, which on Windows is
  `cmd.exe /c <batch>` (`:102-106`, `WriteWindowsCommandBatch` `:110-118`);
  `ShellTestRunnerWslRouteTests.ResolveLaunch_WindowsWithoutAContext_KeepsTheCmdBatch`
  (`:40`) pins it. `ProjectBootstrapper.CreateValidationRunner`
  (`Init/ProjectBootstrapper.cs:80-81`) builds that runner with no host, and
  neither `BootstrapAsync` (`:87-92`) nor
  `TryUpgradePlaceholderTestCommandAsync` (`:146-151`) takes a host or asks
  the gate. Four surfaces reach it: `tools/VisualRelay.Init/Program.cs:14`,
  the GUI button (`MainWindowViewModel.Bootstrap.cs:41`), create-config
  (`MainWindowViewModel.Execution.cs:199-200`) and the placeholder upgrade
  (`MainWindowViewModel.RunnableGate.cs:29`).

## Current state (researched at f8a0d07b)

- `WslContext` (`Execution/Wsl/WslContext.cs:10`) is `(WslExePath, Distro,
  NonoPath, DistroHome, UserPath)`; the resolver builds it from a usable probe
  only, and the app keeps nothing of a failed probe.
- The in-app gate is `SandboxedStage.MissingRequiredTools`
  (`Execution/SandboxedStage.ToolPresence.cs:23-65`): on a Windows host it
  reports `WslRequirement` (`:11`) when `host.Wsl` is null, and
  `MissingToolsMessage` (`:72-75`) renders the generic "not installed or not
  on PATH" line. The CLI's gate prints the full `WslGate` message.
- `EnsureRunnableAsync` (`MainWindowViewModel.RunnableGate.cs`) resolves the
  host with an await first and hands it to the tool gate; bootstrap and
  create-config have no such step.
- `ProjectBootstrapResult` (`Init/ProjectBootstrapper.cs:8-19`) has no way to
  say "refused".

## Prescribed approach

1. Git in the distro. `WslProber.ProbeAsync` gains an eighth step after the
   login PATH is read: `command -v git` through `sh -c` with that PATH, because
   that is the PATH the routed git launch gets. `WslProbe` gains
   `string? GitPath` and `IsUsable` requires it. `WslGate.FirstFailingCheck`
   gains one line for a probe that passed every earlier check: "git was not
   found inside the WSL distro '<d>'." with the fix "Install it there (`sudo
   apt install -y git` on Ubuntu), then check `wsl -d <d> --exec sh -lc
   'command -v git'`."
   `TROUBLESHOOTING.md`'s gate table gains the row. The routed launch keeps
   its bare `git`: the PATH it runs with is the one the probe checked.
2. Bootstrap refuses where the pipeline would. `ShellTestRunner.ResolveLaunch`
   throws `InvalidOperationException` when `host.IsWindows` and `host.Wsl` is
   null, so the runner answers exit 126 with the message, as the DrvFs refusal
   does (`:47-49`); `BuildShellLaunch` loses its `isWindows` arm and
   `WriteWindowsCommandBatch` is deleted. `ProjectBootstrapper.BootstrapAsync`
   and `TryUpgradePlaceholderTestCommandAsync` take a `SandboxHost host`
   (callers pass `await SandboxHost.CurrentAsync()`) and return before writing
   anything when the host is Windows without a distro:
   `ProjectBootstrapResult` gains `string? Refusal`; the CLI init prints it and
   exits 127, the GUI puts it in `StatusText`. The message is the gate's:
   `WslGate` moves from the CLI into `Execution/Wsl/WslGate.cs` (the CLI keeps
   calling it), `WslContextResolver` keeps the last `WslProbe` beside the
   context, and `SandboxedStage.MissingToolsMessage` on a Windows host renders
   `WslGate.Decide(probe).Message`, so the run gate, bootstrap and
   create-config print the one fix table `TROUBLESHOOTING.md` documents.
3. One sentence left over from the allow-list change: `docs/OPERATIONS.md`'s
   `sandboxExtraAllowPaths` paragraph (`:74-76`) says that on Windows `~` and
   `$HOME` resolve against the distro user's home, not the Windows profile.

## Tests

- `WslProberTests`: `HealthyMachine_RunsExactlyTheEightDocumentedStepsInOrder`
  (replaces the seven-step fact); `GitMissing_LeavesGitNullAndTheProbeUnusable`;
  `GitStep_RunsWithTheLoginShellsPath`.
  `WslGateDecisionTests.GitMissing_NamesTheInstallInsideTheDistro` and the
  ordering in `SeveralChecksFailing_TheFirstOneIsNamed`.
- `ShellTestRunnerWslRouteTests`: `ResolveLaunch_WindowsWithoutAContext_IsRefused`
  replaces `..._KeepsTheCmdBatch`;
  `RunAsync_WindowsWithoutAContext_AnswersExit126WithoutSpawning`.
- `ProjectBootstrapperTests.BootstrapAsync_WindowsWithoutADistro_RefusesAndWritesNothing`
  (no `.relay/`, no `.git`, `Refusal` carries the gate text);
  `TryUpgrade_WindowsWithoutADistro_LeavesThePlaceholder`.
  `MainWindowViewModelInitTests`: create-config on that host reports the
  refusal in `StatusText` and writes no config.
- `SandboxedStageToolPresenceWslTests.MissingToolsMessage_OnWindows_IsTheGatesMessage`.
- Every fact states its host, so all of them run on macOS.

## Verification (through the control API)

The macOS run proves no regression: `bootstrap` on a fresh clone still writes
the config and `open-folder` still answers 200. On the Windows PC, the next
time it is synced: remove git from the distro (`sudo apt remove -y git`),
launch, `open-folder` on a `\\wsl.localhost\...` clone and `bootstrap`.
Expected: `statusText` carries the gate's git line and no `.relay/` is
written; after `sudo apt install -y git` the same two calls write the config.
Put both status texts in the commit body.

## Out of scope

The DrvFs policy (`WslWorkspacePolicy.MntPolicy` stays `Refuse`); a git
version floor inside the distro; the WSL process-model items from the same
review (dropped on 2026-09-17: two days of real Windows runs hit none of
them).

## Rejected alternatives

- Threading the probed git path into every routed launch, as the first
  version of this spec asked: since fe0b406d the launch carries the login
  shell's PATH, so checking `command -v git` against that PATH proves what the
  launch will find.
- Keeping the `cmd.exe` check as a fallback: a command checked through
  `cmd.exe` is checked where the pipeline will never run it, which is the bug.
