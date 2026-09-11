# Finish the WSL process model: one host per run, a cheap sampler, a reapable git

The WSL cutover gave every sandboxed launch a Linux-side process model: an
envelope that records the tree's pid, a control that samples and stops the
tree from inside the distro, and a host record that says where paths and
processes live. Four seams were left in the older shape. The execution layer
still reaches for `SandboxHost.Current`, a synchronous read of the memoized
probe, from three places on driver threads. Behind wsl.exe every CPU sample is
a fresh `wsl.exe ps`, one per four seconds per running verify. VR's own git
calls inside the distro carry no tree control, so a timed-out git (or the
pre-commit hook it runs) is orphaned when the relay is killed. And the
flagged-work bundle is handed to git as a repo-relative path that `fetch`
resolves against the top level while `bundle create` and `verify` resolve it
against the root: right only while the root is the top level. Each is closed
where the cutover left it, with the same seams the cutover built.

## Evidence

- Review of 2026-09-11, items 19 to 22. Runtime proof on Windows is the
  separate task `verify-the-windows-arm-on-a-hosted-runner`.
- `SandboxHost.Current` (`Execution/SandboxHost.cs:24-25`) is
  `WslContextResolver.TryGetCurrent()`, which blocks on the memo
  (`Wsl/WslContext.cs:47`, `GetAwaiter().GetResult()`). Readers on driver
  threads: `PlanningWorktree.cs:34,161`, `FlaggedWorkStore.cs:50`,
  `CommitLint/HistoryRewriter.Replay.cs:128`; defaults `host ?? SandboxHost.Current`
  at `SandboxedTestRunner.cs:33`, `Agent/Tools/SandboxedCommandExecutor.cs:39`,
  `SandboxedStage.cs:57`, `SandboxedStage.ToolPresence.cs:30`, `ShellTestRunner.cs:40`;
  `GitInvoker.ContextFor` (`GitInvoker.cs:108-115`) reads the same memo. The
  GUI warms it (`MainWindowViewModel.RunnableGate.cs:21`); `tools/VisualRelay.RunTask/Program.cs:30-34`
  and `tools/VisualRelay.DrainQueue/Program.cs:35-49` build their dependencies
  without resolving a host, so on Windows their first read blocks a pool
  thread for the whole six-step probe (60 s per step, `WslContext.cs:20`).
- `SandboxedTestRunner.Watched.cs:15` samples every 4 000 ms;
  `ProcessCapture.CpuSampling.cs:41-83` calls the control on that cadence;
  `Wsl/WslProcessTreeControl.cs:22-30` runs `wsl.exe --exec ps` per call and
  `cat <pidfile>` (`:46-61`) per call until the pid resolves. Every call is a
  Windows process through the WSL service (`Wsl/WslSandboxLauncher.cs:92-97`).
  A host-side `ps` per sample was acceptable on macOS; a `wsl.exe` is not the
  same cost.
- `GitInvoker.cs:73-83`: the WSL route launches `WslLauncher.BuildPlain`
  (`GitRouting.cs:48-69`, no envelope, no pid file) with `reapProcessTree: false`
  and no `treeControl`, timeout `DefaultTimeout` 30 s (`:16`). On timeout
  `ProcessCapture.cs:176-181` kills wsl.exe and reaps through the control,
  which is null: the Linux git and any hook it spawned keep running.
- Real git 2.50.1, probed on 2026-09-11 in a scratch repo with a subdirectory
  root: `git -C sub bundle create .relay/t/w.bundle …` and `bundle verify`
  resolve the path against `sub` (the file lands in `sub/.relay/t/`), while
  `git -C sub fetch .relay/t/w.bundle …` fails ("the repository exists"),
  because fetch resolves a relative URL against the top level.
  `FlaggedWorkStore.RepoRelativeBundle` (`:250-251`) hands all three the same
  string; `git rev-parse --show-prefix` from `sub` prints `sub/`.

## Current state (researched at d5bf93cc)

- `RelayDriverDependencies` (`Execution/RelayDriverDependencies.cs:15-21`)
  carries runner, test runner, sink, git, environment, clock; no host.
  `RelayDriver.RunTaskAsync` (`RelayDriver.cs:24-35`) ensures the profile once
  at `:35`; that is where a run's host would be resolved.
- `WorktreeNamespace.For/TempIndexFor/TempFileFor/ForGit`
  (`Execution/WorktreeNamespace.cs:37-72`) already take a `SandboxHost`; the
  three readers above pass `SandboxHost.Current` into them. Callers of the
  worktree and store: `PlanPhaseRunner.cs:146,188`, `RelayDriver.VerifyWorktree.cs:98`,
  `VerifyWorktreeCleanup.cs:18`, `GuardAttribution.cs:137`, `TaskRewriteRunner.cs:68,151`,
  `RelayDriver.Events.cs:145`, `Cancel.cs:59`, `FlaggedWork.cs:37`.
- The envelope (`Wsl/WslLauncher.cs:29-31`) is `mkdir -p; cd; setsid "$@" &;
  echo pid; wait`; `SandboxedLaunch.ForWsl` (`Execution/SandboxedLaunch.cs:37-39`)
  attaches `WslSandboxLauncher.TreeControl(context, pidFile)`. The forced
  reap removes the pid file (`WslProcessTreeControl.cs:40-43`).
- Tests that pin today's shape: `ProcessCaptureTreeControlTests` (eight facts,
  `:71` the sample loop), `WslProcessTreeControlTests` (eight, `:26,40`),
  `GitInvokerTests.RunAsync_UncRoot_RunsGitInsideTheDistroThroughTheInjectedRunner`
  (`:199`), `WslLauncherArgvTests.Envelope_IsTheDocumentedScript` (`:65`),
  `RelayDriverResumeFlaggedWork3Tests.CaptureAndRestore_HandGitTheBundle_RepoRelative`
  (`:161`), `WorktreeNamespaceTests` (seven), `SandboxHostResolveTests` (three).

## Prescribed approach

1. One host per run. `RelayDriverDependencies` gains `SandboxHost? SandboxHost = null`;
   `ForTests` defaults it to `SandboxHost.Local`. `RunTaskAsync` resolves
   `var host = _dependencies.SandboxHost ?? await SandboxHost.CurrentAsync(ct)`
   beside the profile ensure and threads it: `PlanningWorktree.CreateAsync`,
   `RemoveAsync` and `PruneLeftoversAsync` take `SandboxHost host`;
   `FlaggedWorkStore.CaptureAsync` and `RestoreAsync` take it; `HistoryRewriter`
   becomes `HistoryRewriter(IGitInvoker git, SandboxHost host)`;
   `TaskRewriteRunner` receives it from the view model, which already awaits
   the host in `EnsureRunnableAsync`. `GitInvoker` gains a public
   `GitInvoker(SandboxHost host)` that stores `host.Wsl` in the existing `_wsl`
   field, and the driver-facing constructions use it; the parameterless
   constructor stays for tooling on a drive-path checkout, whose route is
   native without a probe (`ContextFor`, `:112`). The RunTask and DrainQueue
   programs await `SandboxHost.CurrentAsync()` before building dependencies.
   `SandboxHost.Current` is deleted; the `??` defaults become required
   arguments, and `WslContextResolver.TryGetCurrent()` keeps exactly one
   caller, the parameterless `GitInvoker`, documented as such.
2. A sampler that spawns rarely. `WslProcessTreeControl` takes a `TimeProvider`
   and a spacing constant `SampleSpacing = 15 s`: a `SampleCpuMsAsync` call
   inside the spacing returns the last successful value without launching
   anything (a repeated value is a zero delta, which is "no new work", never
   the baseline-dropping null); `ps` runs at most once per spacing per launch;
   the pid-file `cat` runs at most once per spacing until it resolves, then
   never again. The 4 000 ms loop cadence stays, so the local host is
   untouched. With the default 600 000 ms inactivity window
   (`Configuration/RelayConfigLoader.Defaults.cs:47`) a 15 s pulse spacing is
   forty times inside the window.
3. A git the control can reach. `WslLauncher` gains `GitEnvelope` =
   `p=$1; d=$2; shift 2; mkdir -p "$(dirname "$p")" || exit 127; cd "$d" || exit 127;
   setsid "$@" & c=$!; echo "$c" > "$p"; wait "$c"; s=$?; rm -f -- "$p"; exit "$s"`
   and `BuildGit(wslExe, distro, linuxRoot, pidFile, argv, env)`;
   `GitRouting.Launch` returns a `WslSandboxLaunch` built through it with the
   launch tag `git`, and the WSL branch of `GitInvoker.RunAsync` passes
   `treeControl: WslSandboxLauncher.TreeControl(context, pidFile)`.
   `reapProcessTree` stays false: the envelope removes its own pid file on
   exit, so a normal git call spawns exactly one wsl.exe as today, and only a
   timeout pays for the `kill -TERM -- -pgid`, the `-KILL` and the removal.
4. The bundle, three ways git reads it. `FlaggedWorkStore` reads
   `git rev-parse --show-prefix` once per capture and per restore;
   `bundle create` and `bundle verify` keep the root-relative argument, and
   `fetch` gets `<prefix><root-relative>`, so all three name the file the
   experiment above proved they each resolve. The comment at `:246-249`
   states the two rules. A non-top-level root breaks more than the bundle
   (manifest pathspecs, `.relay/` placement); refusing it is a gate question
   left out of this task.

## Tests

- `RelayDriverDependenciesTests.ForTests_DefaultsTheHostToLocal`;
  `RelayDriverHostThreadingTests.RunTask_ResolvesTheHostOnce_AndHandsItToEveryWorktreeAndStoreCall`
  (a counting `SandboxHostResolver`; GitSim; one resolution per run);
  `PlanningWorktreeRewriteIsolationTests` and `RelayDriverResumeFlaggedWork3Tests`
  compile against the new signatures with `SandboxHost.Local`.
  `GitInvokerTests.HostConstructor_RoutesAUncRootThroughTheHostsContext`.
  A guard fact in `SplitGuardVerificationTests.Conventions`:
  `grep -rn "SandboxHost.Current\b" src/` is empty.
- `WslProcessTreeControlTests`: `Sample_WithinTheSpacing_ReturnsTheLastValueWithoutLaunching`;
  `Sample_AfterTheSpacing_RunsPsAgain`; `Sample_PidFileMissing_IsRetriedOncePerSpacing`;
  `Stop_IsNeverThrottled`. `ProcessCaptureTreeControlTests.SampleLoop_AsksTheControlOnTheInterval_AndPulsesCpuOnGrowth`
  still passes (the loop is unchanged).
- `WslLauncherArgvTests.GitEnvelope_IsTheDocumentedScript` and
  `BuildGit_ProducesTheDocumentedShapeElementByElement`;
  `GitInvokerTests.RunAsync_UncRoot_TimedOut_StopsTheGroupAndRemovesThePidFile`
  (the injected runner records TERM, KILL, `rm -f`);
  `RunAsync_UncRoot_NormalExit_LaunchesExactlyOnce`. Windows-gated:
  `WslExitCodeAndUtf8Tests.GitEnvelopeLaunch_ExitCode_SurvivesTheWait`.
- `RelayDriverResumeFlaggedWork3Tests.CaptureAndRestore_FromASubdirectoryRoot_FetchNamesTheTopLevelPath`
  (real git, a root one directory below the top level: capture, then restore
  succeeds and the fetch argument starts with the prefix);
  `CaptureAndRestore_HandGitTheBundle_RepoRelative` keeps its assertions for
  create and verify.

## Verification (through the control API)

On macOS run one gorilla/mux task through `open-folder`, `create-task`,
`run-selected`, then `cancel` mid-stage and `reset-selected`: the flagged-work
bundle is captured and restored as before (`run.log` shows `cancelled` and the
resume path reads the bundle). Then the Windows recipe, run by the
hosted-runner task: with a verify stage running, `wsl -d Ubuntu --exec ps
-eo pid,etime,cmd | grep -c '[p]s -axo'` sampled every second for a minute
counts at most four `ps` launches; kill a hung `git` by hand
(`git -C <repo> fetch` of a bundle path that blocks on a FIFO) and confirm
`wsl -d Ubuntu --exec ps -p <pgid>` fails after VR's 30 s timeout. Put the
launch count and the kill verdict in the commit body.

## Out of scope

Refusing a non-top-level workspace root (a gate task of its own); the Windows
runtime proof; the local host's `ps` cadence; `WslContextResolver`'s memo,
which stays as the one place a probe runs.

## Rejected alternatives

- A streaming sampler (one long-lived `wsl.exe sh -c 'while …; do ps; sleep;
  done'` per launch): removes the spawn entirely but adds a second process
  lifecycle to supervise, and a dead stream reads as "no signal" until it is
  restarted; a throttle keeps the retry-on-next-call shape that exists.
- Running git through the sandbox envelope with the reap-on-exit that every
  sandboxed launch pays: doubles the wsl.exe count of every git call (VR makes
  hundreds per task) for a reap git does not need; a self-cleaning envelope
  costs nothing on the normal path.
- Keeping `SandboxHost.Current` but warming it in the CLIs: hides the read
  instead of removing it; the next caller on a cold memo blocks again.
- Refusing a subdirectory root inside `FlaggedWorkStore`: the store is the
  wrong place to police the workspace, and computing the prefix is cheaper
  than a refusal path nothing else honors yet.
