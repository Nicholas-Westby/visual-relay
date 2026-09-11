# Close the four Windows entry-point gaps the WSL cutover left open

The WSL cutover made every sandboxed launch, every workspace git call and the
guard profile live inside the distro, and the pure units for all of it are
tested on macOS. Four entrances into that design were not brought along. The
gate that decides a distro is usable never asks whether `git` exists there,
although every workspace git call now runs there. `sandboxExtraAllowPaths`
entries written as `~/…` are expanded against the Windows profile and then
granted as `/mnt/c/Users/…`, a DrvFs path the distro's toolchain never writes
to. `POST /command/open-folder` skips the folder-pick policy the Browse button
applies, so a `C:\` root can be opened through the API and refused at every
launch afterwards. And bootstrap on a Windows box without a usable distro
smoke-runs the operator's command through `cmd.exe` and writes a `testCmd` the
pipeline can never run there. Each gap is closed at its own entrance; nothing
here needs a Windows machine to be pinned.

## Evidence

- Review of 2026-09-11, items 15 to 18. All four are code-reading findings on
  the unverified Windows arm; the runtime proof is the separate task
  `verify-the-windows-arm-on-a-hosted-runner`.
- `Execution/Wsl/WslProber.cs:42-102` runs six steps (listing, `uname -r`,
  `command -v nono` through a login shell, `nono --version`, the LSM list,
  `$HOME`); `WslProbe.IsUsable` (`WslProbe.cs:33-34`) is the conjunction of
  those. `Execution/Wsl/GitRouting.cs:64-68` puts a bare `git` into the
  `wsl.exe -d <distro> --exec` argv for every routed call, and `GitInvoker.cs:73`
  routes every UNC or Linux root that way. `WslGate.FirstFailingCheck`
  (`tools/VisualRelay.Cli/Gates/WslGate.cs:34-72`) has no git line.
- `Configuration/RelayConfigLoader.cs:103-110` expands `~` and `$HOME` against
  `Environment.SpecialFolder.UserProfile`, `:120-134` requires the result under
  that profile or the root, and `SandboxHost.MapGrant` (`Execution/SandboxHost.cs:62-71`)
  then maps the drive path to its `/mnt/<letter>` mount. A Linux entry such as
  `/home/u/.cache/x` is rejected on Windows at `:120` because `Path.GetFullPath`
  reads it as `C:\home\u\.cache\x`. `docs/OPERATIONS.md:57-69` documents `~`
  expansion and the `$HOME` rule with no Windows caveat.
- `Services/ControlApi.cs:217-235` sets `viewModel.RootPath = path` after
  `Directory.Exists`; `ViewModels/MainWindowViewModel.Commands.cs:19-33`
  (`BrowseAsync`) goes through `FolderPickerWslTranslation.Decide`
  (`Services/FolderPickerWslTranslation.cs:30-44`), which refuses a drive pick
  and a foreign share on Windows. `ControlApiTests.InvokeCommand_OpenFolder_SetsRootPath_AndRejectsMissingFolder`
  (`:136`) is the only API test; nothing covers a refused pick.
- `Execution/ShellTestRunner.cs:67-72`: with `host.Wsl` null the launch is
  `BuildShellLaunch(command, host.IsWindows, …)`, which on Windows is
  `cmd.exe /c <batch>` (`:92-96`, `WriteWindowsCommandBatch` `:100-108`);
  `ShellTestRunnerWslRouteTests.ResolveLaunch_WindowsWithoutAContext_KeepsTheCmdBatch`
  (`:40`) pins it. `ProjectBootstrapper.CreateValidationRunner` (`Init/ProjectBootstrapper.cs:77-78`)
  builds that runner with no host, and `BootstrapAsync` (`:84-114`) neither
  takes a host nor asks the gate. Four surfaces reach it: `tools/VisualRelay.Init/Program.cs:14`,
  the GUI button (`MainWindowViewModel.Bootstrap.cs:37`), create-config
  (`MainWindowViewModel.Execution.cs:193-201`) and the placeholder upgrade
  (`MainWindowViewModel.RunnableGate.cs:29`).

## Current state (researched at d5bf93cc)

- `WslContext` (`Execution/Wsl/WslContext.cs:9`) is `(WslExePath, Distro,
  NonoPath, DistroHome)`; `WslContextResolver.FromProbe` (`:98-101`) builds it
  from a usable probe only, and the app keeps nothing of a failed probe.
- The in-app gate is `SandboxedStage.MissingRequiredTools`
  (`Execution/SandboxedStage.ToolPresence.cs:23-65`): on a Windows host it
  reports `WslRequirement` (`:11`) when `host.Wsl` is null, and
  `MissingToolsMessage` (`:72-75`) renders the generic "not installed or not on
  PATH" line. The CLI's `NonoGate.Require` (`tools/VisualRelay.Cli/Gates/NonoGate.cs:23-33`)
  prints the full `WslGate` message and installs the context as the override.
- `EnsureRunnableAsync` (`RunnableGate.cs:14-81`) resolves the host with an
  await first (`:21`) and hands it to the tool gate (`:60-61`); bootstrap and
  create-config have no such step.
- Tests that pin today's behavior: `WslProberTests.HealthyMachine_RunsExactlyTheSixDocumentedStepsInOrder`
  (`:48`), `WslGateDecisionTests` (ten facts), `SandboxExtraAllowPathsConfigTests.Tilde_ExpandsToHome`
  (`:29`), `BuildNonoPrefixWslPathsTests.MapGrant_OnTheWslHost_IsThePathAsNonoSeesItInsideTheDistro`
  (`:68`), `FolderPickerWslTranslationTests` (six facts), `ShellTestRunnerWslRouteTests` (four).

## Prescribed approach

1. Git in the distro. `WslProber.ProbeAsync` gains a seventh step after the
   nono version: `-d <distro> --exec sh -c 'command -v git'`, a non-login
   shell because that is the PATH `--exec git` resolves against. `WslProbe`
   gains `string? GitPath`; `IsUsable` requires it; `WslContext` gains `GitPath`
   and `GitRouting.Launch` puts it into the argv in place of the bare `git`
   (a UNC root naming a distro other than the resolved one keeps the bare
   name, as today). `WslGate.FirstFailingCheck` gains, after the nono check:
   "git was not found inside the WSL distro '<d>'." with the fix "Install it
   there (`sudo apt install git` on Ubuntu), then check `wsl -d <d> --exec sh
   -c 'command -v git'`." TROUBLESHOOTING.md's gate table (`:171-179`) gains
   the row.
2. Extra allow paths resolve where nono runs. `RelayConfigLoader` takes the
   host's platform through an internal overload (`isWindows`, the way
   `ShellTestRunner.BuildShellLaunch` does) so the rule is tested on macOS. On
   Windows a `~/<rel>` or `$HOME/<rel>` entry is validated by its relative
   part alone (no `..`; not one of the sensitive names at `:136-142`, expressed
   relative to home) and stored as written; a drive path keeps today's rule
   and today's DrvFs mapping; a `/`-rooted entry is `Malformed` with the
   message "write it as ~/… so it resolves inside the WSL distro". Off Windows
   nothing changes. `SandboxHost.MapGrant` expands `~/` and `$HOME/` against
   `Wsl.DistroHome` on the WSL host (null with no distro, and every launch is
   blocked then anyway) and against the user profile on the local host, which
   never sees an unexpanded entry today and so is unchanged. OPERATIONS.md's
   paragraph (`:57-69`) gains the Windows sentence.
3. One folder-open rule. `MainWindowViewModel.Commands.cs` gains
   `internal async Task<FolderPick> OpenPickedFolderAsync(string picked)`:
   `FolderPickerWslTranslation.Decide(picked, OperatingSystem.IsWindows())`,
   set the root and refresh on acceptance, put a message (refusal or warning)
   into `StatusText`, return the pick. `BrowseAsync` and the API's `open-folder`
   both call it. The API answers a refusal with
   `409 {"ok":false,"command":"open-folder","error":"<the policy message>"}`
   and leaves the root unchanged; "folder not found" stays the first check.
   AGENTS.md's `open-folder` entry gains the clause "applies the same
   folder-pick policy as Browse, so a Windows drive folder is refused with the
   reason".
4. Bootstrap refuses where the pipeline would. `ShellTestRunner.ResolveLaunch`
   throws `InvalidOperationException` when `host.IsWindows` and `host.Wsl` is
   null, so the runner answers exit 126 with the message as the DrvFs refusal
   does (`:42-45`); `BuildShellLaunch` loses its `isWindows` arm and
   `WriteWindowsCommandBatch` is deleted. `ProjectBootstrapper.BootstrapAsync`
   and `TryUpgradePlaceholderTestCommandAsync` take a `SandboxHost host`
   (callers pass `await SandboxHost.CurrentAsync()`; the GUI's
   `SandboxHostResolver` seam already exists at `RunnableGate.cs:22`) and
   return before writing anything when the host is Windows without a distro:
   `ProjectBootstrapResult` gains `string? Refusal`, and the CLI init prints it
   and exits 127, the GUI puts it in `StatusText`. The message is the gate's:
   `WslGate` moves from the CLI into `Execution/Wsl/WslGate.cs` (the CLI's
   `NonoGate.Decide` keeps calling it), `WslContextResolver` keeps the last
   `WslProbe` beside the context (`TryGetProbeAsync`), and
   `SandboxedStage.MissingToolsMessage` on a Windows host renders
   `WslGate.Decide(probe).Message`, so `EnsureRunnableAsync`, bootstrap and
   create-config print the one fix table TROUBLESHOOTING.md documents.

## Tests

- `WslProberTests`: `HealthyMachine_RunsExactlyTheSevenDocumentedStepsInOrder`
  (replaces the six-step fact); `GitMissing_LeavesGitNullAndTheProbeUnusable`;
  `GitPath_TakesTheLastAbsoluteLine`. `WslGateDecisionTests.GitMissing_NamesTheInstallInsideTheDistro`
  and the seven-check ordering in `SeveralChecksFailing_TheFirstOneIsNamed`.
  `GitRoutingTests`: the launch argv carries the context's `GitPath`.
- `SandboxExtraAllowPathsConfigTests`: `OnWindows_ATildeEntry_IsStoredUnexpanded`;
  `OnWindows_ATildeEntryIntoSsh_IsRejectedByItsRelativePart`;
  `OnWindows_ALinuxAbsoluteEntry_IsRejectedWithTheTildeHint`;
  `OnWindows_ADriveEntry_KeepsTodaysRule`; `Tilde_ExpandsToHome` stays for the
  local host. `BuildNonoPrefixWslPathsTests.MapGrant_OnTheWslHost_ExpandsTildeAgainstTheDistroHome`
  (`~/.cache/x` with `DistroHome` `/home/u` gives `-a /home/u/.cache/x`).
- `ControlApiTests.InvokeCommand_OpenFolder_AppliesTheFolderPickPolicy`: with
  the view model's pick rule driven as Windows (inject the platform flag
  through the view model, as `FolderPickerWslTranslationTests.Browse_RefusedPick_KeepsTheRootAndExplainsInTheStatus`
  does), `C:\repo` answers 409 with the DrvFs message and `rootPath` is
  unchanged; a UNC pick answers 200. `MainWindowViewModelTests`: Browse and
  the API share `OpenPickedFolderAsync` (one fact asserting the status text
  from each path is identical).
- `ShellTestRunnerWslRouteTests`: `ResolveLaunch_WindowsWithoutAContext_IsRefused`
  replaces `..._KeepsTheCmdBatch`; `RunAsync_WindowsWithoutAContext_AnswersExit126WithoutSpawning`.
- `ProjectBootstrapperTests.BootstrapAsync_WindowsWithoutADistro_RefusesAndWritesNothing`
  (no `.relay/`, no `.git`, `Refusal` carries the gate text);
  `TryUpgrade_WindowsWithoutADistro_LeavesThePlaceholder`.
  `MainWindowViewModelInitTests`: create-config on that host reports the
  refusal in `StatusText` and writes no config.
- `SandboxedStageToolPresenceWslTests.MissingToolsMessage_OnWindows_IsTheGatesMessage`.

## Verification (through the control API)

The macOS run proves no regression: `open-folder` on a real folder still
answers 200 and `/state.rootPath` follows; `bootstrap` on a fresh clone still
writes the config. The Windows commands below go on the checklist that
`verify-the-windows-arm-on-a-hosted-runner` runs, with `$U` the distro user:

    curl -s -X POST -d '{"path":"C:\\Users\\me\\repo"}' http://127.0.0.1:8765/command/open-folder
    curl -s -X POST -d '{"path":"\\\\wsl.localhost\\Ubuntu\\home\\'$U'\\repo"}' http://127.0.0.1:8765/command/open-folder
    curl -s http://127.0.0.1:8765/state | jq -r .rootPath
    wsl -d Ubuntu --exec sh -c 'command -v git'

Expected: the first answers 409 with the DrvFs message and `rootPath` is still
the previous root; the second answers 200; with git removed from the distro
(`sudo apt remove git`) `bootstrap` answers with the gate's git line in
`statusText` and writes no `.relay/`. Put the four answers in the commit body.

## Out of scope

Running any of this on Windows (the runner task); the DrvFs policy itself
(`WslWorkspacePolicy.MntPolicy`, decided by probe 1b there); the process-model
items (`SandboxHost.Current` reads, CPU sampling, git reaping) in
`finish-the-wsl-process-model`; a git-version floor inside the distro.

## Rejected alternatives

- Letting nono expand `~` in `-a` grants: nono's handling of an unexpanded
  tilde is undocumented and cannot be measured from here, while
  `DistroHome` is already probed and already places the profile.
- Keeping the `cmd.exe` smoke as a fallback: a command validated through
  `cmd.exe` is validated where the pipeline will never run it, which is the
  bug; a refusal that names the fix is what the launch gate already does.
- Answering `open-folder` with 200 plus a warning field for a drive root: the
  caller cannot see the workspace policy and the root would be refused at the
  next launch anyway; a 409 with the reason is the answer Browse already gives.
