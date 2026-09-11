# Windows Sandbox via WSL2 nono Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Windows sandbox (Microsoft MXC `wxc-exec`) with `nono` running inside a WSL2 distro, so every platform enforces the same `packaging/nono/vr-guard.json` through the same binary; delete the MXC path; make the loss of Windows-only toolchains explicit in the gate and the docs.

**Architecture:** A `Wsl` namespace of pure, unit-testable units (path translation, distro-list parsing, gate decision, launch argv construction, workspace policy, process-tree control commands, profile placement, git routing) plus thin seams in the existing execution layer (`ProcessCapture`, `NonoProfileEnsurer`, `SandboxPathInspector`, `GitInvoker`, the two launch builders, the CLI gate). Everything that needs a Windows machine is a Windows-gated, opt-in test plus a manual GitHub Actions probe workflow on `windows-2025`, and is recorded as unverified.

**Tech Stack:** C# / .NET 10, xunit v3, GitHub Actions (`windows-2025` hosted runner has WSL 2.7.12 preinstalled).

**Spec:** `llm-tasks/windows-sandbox-via-wsl-nono/windows-sandbox-via-wsl-nono.md` (binding). Research notes with the code map, wsl.exe semantics from Microsoft's source, kernel-config evidence and the probe checklist: `/private/tmp/claude-501/-Users-admin-Dev-visual-relay/b4a8a6c0-864a-40c1-a57e-c9b1c0ef494f/scratchpad/notes/20-windows-wsl.md`.

## Global Constraints

- Commits go directly on `main`, Conventional Commit subjects at most 72 characters, lowercase after the prefix, no trailing period, no em dash, body of at most three `- ` bullets of at most 20 words each, no changed-file names or path-like tokens in subject or bullets. Stage only your own files; the pre-commit hook bumps `VERSION`.
- Every `*.cs`/`*.axaml` under `src/`, `tests/`, `tools/` at or under 300 lines. Shell scripts at most 24 logic lines (`visual-relay.ps1` is not a shell script for the guard).
- `./visual-relay check` exits 0 with `inspect-code: 0 findings` before each task's last commit; `git checkout -- docs/images` afterwards.
- Tests never use real git (`GitSim`), real sleeps, wall-clock waits, `.Result`/`.Wait()`, `new HttpClient`, or `new GitInvoker(`; the execution layer never coalesces `?? new GitInvoker()` (guards). A test that spawns a real process must be gated by BOTH `Assert.SkipUnless(OperatingSystem.IsWindows() && ..., "...")` and `NonoIntegration.SkipIfNotOptedIn()` (`VR_RUN_NONO_INTEGRATION=1`), or the sandbox-skip guard fails.
- Rulings already made (binding): implement now and delete MXC in this plan (the spec forbids two sandboxes behind a flag); the Windows runtime proof is outstanding and is stated plainly in commit bodies and docs. `/mnt/*` workspaces are refused (one policy constant marks the downgrade point). The bootstrap's shell runner also goes through WSL on Windows. The user templates directory grant is translated to `/mnt/<drive>/...` and listed on the probe checklist. The CI probe workflow is added even though it cannot be run from this machine.
- The premise evidence to cite: Microsoft's WSL2 kernel configs (`arch/x86/configs/config-wsl` on the 5.15, 6.6 and 6.18 branches) set `CONFIG_SECURITY_LANDLOCK=y` and list `landlock` first in `CONFIG_LSM`; nono documents WSL2 as supported (PR 522, 2026-03-30); VR pins nono 0.75.0.
- wsl.exe facts (from Microsoft's open source, see the notes): `--exec` splits with `CommandLineToArgvW` and `execvpe`s the argv with no Linux shell; the Linux exit code returns verbatim, WSL-level failures return -1; `WSL_UTF8=1` makes wsl.exe's own output UTF-8 (default UTF-16LE); killing wsl.exe never signals the Linux child; `--cd` failure is non-fatal, so the launch envelope does its own `cd`.
- `llm-tasks/**` is not edited by these tasks.

---

### Task 1: WSL paths, distro list parsing, probe record and the gate

**Files:**
- Create: `src/VisualRelay.Core/Execution/Wsl/WslPath.cs`, `src/VisualRelay.Core/Execution/Wsl/WslListParser.cs`, `src/VisualRelay.Core/Execution/Wsl/WslProbe.cs`, `src/VisualRelay.Core/Execution/Wsl/WslProber.cs` (the process-running half, uses a delegate so tests never spawn), `tools/VisualRelay.Cli/Gates/WslGate.cs`, tests `WslPathTests.cs`, `WslListParserTests.cs`, `WslGateDecisionTests.cs`, `WslProberTests.cs`.
- Modify: `tools/VisualRelay.Cli/Gates/NonoGate.cs` (`Decide` gains the probe; on Windows it delegates to `WslGate.Decide` instead of proceeding silently; `Require` runs the prober on Windows), `tests/VisualRelay.Tests/CliGateDecisionTests.cs` (`Nono_Missing_Windows_ProceedsSilently` becomes "Windows without a usable WSL probe exits 127 with the WSL message").

**Interfaces (produces):**

```csharp
public static class WslPath
{
    public static bool TryParseUnc(string path, out string distro, out string linuxPath); // \\wsl$\D\a b\ü and \\wsl.localhost\D\a b\ü -> ("D", "/a b/ü"); trailing separators dropped; forward slashes accepted; rejects relative paths, empty distro, any ".." segment
    public static string ToUnc(string distro, string linuxPath);                          // \\wsl.localhost\D\a b\ü
    public static bool TryDriveToMnt(string windowsPath, out string linuxPath);           // C:\x y\ü -> /mnt/c/x y/ü (drive letter lowercased); rejects relative and UNC
    public static bool IsMntPath(string linuxPath);                                       // /mnt/<letter> or /mnt/<letter>/...
}
public sealed record WslDistro(string Name, int Version, bool IsDefault, string State);
public static class WslListParser { public static IReadOnlyList<WslDistro> Parse(string text); } // wsl -l -v output decoded as UTF-8; column positions from the header line, not English words; '*' marks the default; tolerant of \r\n, NUL bytes from a UTF-16 misdecode, and a localized header
public sealed record WslProbe(
    bool WslExeFound, string? WslExePath, IReadOnlyList<WslDistro> Distros, string? RequestedDistro, string? DistroName,
    bool IsWsl2, string? KernelRelease, string? NonoPath, string? NonoVersion, bool LandlockActive, string? DistroHome, string? Diagnostics)
{ public bool IsUsable => WslExeFound && DistroName is not null && IsWsl2 && NonoPath is not null && LandlockActive && DistroHome is not null; }
public static class WslProber
{
    public const string DistroEnvVar = "VR_WSL_DISTRO";
    public static Task<WslProbe> ProbeAsync(Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> runWsl, string? requestedDistro, string? wslExePath, CancellationToken ct);
    // steps: [ "-l","-v" ] -> parse; pick requested or default distro; [ "-d",D,"--exec","uname","-r" ] must contain "microsoft-standard-WSL2" for IsWsl2; [ "-d",D,"--exec","sh","-lc","command -v nono" ] -> NonoPath (absolute); [ "-d",D,"--exec",nono,"--version" ]; [ "-d",D,"--exec","cat","/sys/kernel/security/lsm" ] must contain "landlock"; [ "-d",D,"--exec","sh","-lc","printf %s \"$HOME\"" ] -> DistroHome. Each wsl.exe run gets WSL_UTF8=1 (the runner delegate owns the environment).
}
public static class WslGate
{
    public static (int ExitCode, string? Message) Decide(WslProbe probe); // 0/null when IsUsable; else 127 with a message naming the FIRST failing check and, always, the toolchain consequence
}
```

**Messages (binding content, wording may be tightened):** each failure message starts with what is missing and the exact fix, and ends with the consequence sentence: "Visual Relay on Windows runs your project's build and test commands inside the WSL2 distro <name or 'you install'>; install the toolchain there. A Windows-only toolchain (MSBuild against .NET Framework, Visual Studio build tools, Unity on Windows, anything that needs an .exe) is not supported." Fixes: WSL missing: `wsl --install -d Ubuntu` in an elevated PowerShell, reboot, run `wsl -d Ubuntu` once to create the user. No distro or not WSL2: `wsl --install -d Ubuntu` or `wsl --set-version <name> 2`; `VR_WSL_DISTRO=<name>` selects a distro other than the default. nono missing: install nono 0.75.0 inside the distro (`curl -fsSL https://nono.sh/install.sh | sh`, or the `.deb` from the v0.75.0 release) and make sure a login shell finds it. Landlock inactive: remove a custom `kernel=` or `kernelCommandLine=` from `%UserProfile%\.wslconfig`, run `wsl --update` and `wsl --shutdown`; stock kernels since 5.15.57.1 enable Landlock.

- [ ] **Step 1: Write the failing tests**: `WslPathTests` (both UNC prefixes, spaces, non-ASCII, trailing separator, forward slashes, `..` rejection, relative rejection, round trip UNC to Linux to UNC, drive to mnt with lowercase letter, `IsMntPath` true/false table); `WslListParserTests` (an English sample with `*` default and a WSL1 row, a sample with a localized header, `\r\n`, NUL-padded UTF-16 misdecode returning nothing usable rather than throwing); `WslProberTests` with a scripted delegate returning canned outputs per argv (usable probe; requested distro not installed; WSL1 distro; nono missing; Landlock line missing); `WslGateDecisionTests` (each failing probe produces 127 and a message naming that check and containing the consequence sentence; usable probe → 0); `CliGateDecisionTests` update.
- [ ] **Step 2: Run** `./visual-relay test Wsl` and `./visual-relay test CliGateDecisionTests` — expected FAIL.
- [ ] **Step 3: Implement**; keep `NonoGate.Decide` pure by passing the probe in.
- [ ] **Step 4: Run** those classes, `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** in two commits: `feat(wsl): add path translation and distro list parsing` and `feat(wsl): gate windows launches on a usable wsl2 distro` (body: the checks the gate names and the toolchain consequence; the kernel-config evidence line).

---

### Task 2: Launcher, workspace policy, process-tree control, profile placement

**Files:**
- Create: `src/VisualRelay.Core/Execution/Wsl/WslLauncher.cs`, `Wsl/WslWorkspacePolicy.cs`, `Wsl/WslProcessControl.cs`, `Wsl/WslProfilePlacement.cs`, `Wsl/WslProcessTreeControl.cs` (implements the strategy below using the plain launcher), `src/VisualRelay.Core/Execution/IProcessTreeControl.cs`, tests `WslLauncherArgvTests.cs`, `WslWorkspacePolicyTests.cs`, `WslProcessControlTests.cs`, `WslProfilePlacementTests.cs`, `ProcessTreeCpuSamplerWslSampleTests.cs`.
- Modify: `src/VisualRelay.Core/Execution/ProcessCapture.cs` and `ProcessCapture.GracefulStop.cs` (optional `IProcessTreeControl? treeControl` parameter on `RunAsync`; when given, CPU samples and the stop sequence go through it; always set `StandardOutputEncoding` and `StandardErrorEncoding` to UTF-8), `src/VisualRelay.Core/Execution/NonoProfileEnsurer.cs` (Windows placement inside the distro, overwrite-always, failure message names the `[automount]` setting when the UNC share is unreachable).

**Interfaces (produces):**

```csharp
public sealed record WslLaunch(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment); // Environment always has WSL_UTF8=1 and WSL_DISABLE_WARNINGS=1
public static class WslLauncher
{
    public const string Envelope = "p=$1; d=$2; shift 2; cd \"$d\" || exit 127; setsid \"$@\" & c=$!; echo \"$c\" > \"$p\"; wait \"$c\"";
    public static WslLaunch Build(string wslExe, string distro, string linuxWorkspace, string pidFile, IReadOnlyList<string> nonoPrefix, string program, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyCollection<string>? envRemove = null);
    // -> wsl.exe -d <distro> --exec /bin/sh -c <Envelope> vr <pidFile> <linuxWorkspace> env [-u K]... [K=V]... <nonoPrefix...> <program> <args...>
    // nonoPrefix is the absolute nono path followed by run --profile <linuxProfile> --allow-cwd ... -- (from BuildNonoPrefix with Linux paths); every element is passed as ONE argv element and never re-quoted
    public static WslLaunch BuildPlain(string wslExe, string distro, IReadOnlyList<string> argv); // wsl.exe -d <distro> --exec <argv...>
}
public enum WslWorkspaceDecision { Allow, Warn, Refuse }
public static class WslWorkspacePolicy
{
    public const WslWorkspaceDecision MntPolicy = WslWorkspaceDecision.Refuse; // the one-line downgrade point once probe 1b shows DrvFs enforces
    public static (WslWorkspaceDecision Decision, string? Message) Decide(string linuxWorkspace); // /mnt/<x>/... -> MntPolicy with a message: DrvFs is roughly an order of magnitude slower for many small files and its permission model is not the one Landlock was designed against; move the repository onto the distro's own filesystem (for example /home/<user>/...) and open it as \\wsl.localhost\<distro>\home\<user>\...
}
public static class WslProcessControl
{
    public static IReadOnlyList<string> KillArgv(int pgid, string signal); // ["kill", "-TERM", "--", "-1234"]
    public static IReadOnlyList<string> SampleArgv();                     // ["ps", "-axo", "pid=,ppid=,time="]
    public static IReadOnlyList<string> ReadPidFileArgv(string pidFile);  // ["cat", pidFile]
    public static string PidFilePath(string runId, string attemptTag);     // /tmp/visual-relay/<runId>-<attemptTag>.pid
}
public interface IProcessTreeControl
{
    Task<long?> SampleCpuMsAsync(CancellationToken ct);      // WSL: ps through wsl.exe, ProcessTreeCpuSampler.SumTreeCpuMs(linuxPid, output)
    Task StopAsync(bool graceful, CancellationToken ct);      // WSL: TERM to -pgid, then KILL to -pgid after the existing grace window; then kill wsl.exe
}
public static class WslProfilePlacement { public static (string WritePath, string LinuxPath) For(string distro, string distroHome); } // (\\wsl.localhost\D\home\u\.config\visual-relay\vr-guard.json, /home/u/.config/visual-relay/vr-guard.json)
```

- [ ] **Step 1: Write the failing tests**: `WslLauncherArgvTests` asserts the argv element by element for arguments containing a space, `"`, `'`, `\`, `$`, a backtick, a newline and `héllo/日本` (each stays one element, verbatim; `--exec` precedes `/bin/sh`; the envelope is the constant; env removals come before assignments; the environment carries both WSL variables); `WslWorkspacePolicyTests` (`/mnt/c/Users/x/repo` refused with the message, `/home/u/repo` allowed, `/mnt` prefix without a drive letter allowed); `WslProcessControlTests` (argv shapes, pid file path); `ProcessTreeCpuSamplerWslSampleTests` (a captured Linux `ps -axo pid=,ppid=,time=` sample with a three-level tree sums correctly for the root pid); `WslProfilePlacementTests` (paths for a home with a space); a `ProcessCapture` test that a fake `IProcessTreeControl` receives the sample and stop calls (use the existing fake-clock patterns; no real sleeps); an `NonoProfileEnsurer` pure test that the Windows placement returns the Linux path.
- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement**. In `ProcessCapture`, the default (no strategy) behavior is unchanged except the UTF-8 encodings.
- [ ] **Step 4: Run** `./visual-relay test Wsl`, `./visual-relay test ProcessCapture`, `./visual-relay test ProcessTreeCpuSampler`, `./visual-relay test NonoProfileEnsurer`, `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** as `feat(wsl): build the nono launch through wsl.exe exec` (launcher + policy), `feat(wsl): stop and sample the linux tree from inside the distro` (process control + capture seam + encodings; body states that the kill proof runs only on Windows and is outstanding), `feat(wsl): place the guard profile inside the distro`.

---

### Task 3: Inspector platform seam, git routing, folder picker, hook permissions

**Files:**
- Create: `src/VisualRelay.Core/Execution/Wsl/WslContext.cs` (process-wide resolved probe: `WslExePath`, `Distro`, `NonoPath`, `DistroHome`; `TryGetCurrent()` returns null off Windows; `Override(WslContext?)` for tests; resolved once at launch by the CLI gate or the app's startup), `src/VisualRelay.Core/Execution/Wsl/GitRouting.cs`, tests `GitRoutingTests.cs`, `SandboxPathInspectorPlatformTests.cs`, `FolderPickerWslTranslationTests.cs`, `HookInstallerWslTests.cs`.
- Modify: `SandboxPathInspector.cs` and `SandboxPathInspector.Inherited.cs` (an explicit `SandboxPlatform { MacOS, Linux }` parameter replaces the `OperatingSystem.IsLinux()` checks in `ShouldSkipByWhen`/`ShouldSkipPlatformToken`; on Windows the platform is Linux, the temp profile is written into the distro via `WslProfilePlacement`, and `nono profile show`/`nono profile groups` run through `WslLauncher.BuildPlain`; `ExpandPath` uses the distro home on Windows), `GitInvoker.cs` (`GitRouting.Decide(rootPath)`: UNC or Linux absolute roots run `wsl.exe -d D --exec git -C <linuxRoot> ...` through the plain launcher; Windows drive roots keep Git for Windows so VR's own Windows checkout and tooling are unchanged), the folder-picker path in the view model (`MainWindowViewModel` open-folder handling: a `\\wsl$`/`\\wsl.localhost` pick is accepted and kept as the UNC root; a drive-letter pick on Windows is refused with the `/mnt` policy message in `StatusText`), `HookInstaller` (after writing the target repo's pre-commit hook under a UNC root, run `chmod +x` through WSL), delete `SandboxPathInspector.Windows.cs` and the `WindowsCredentialCaveat`/`Url` members, `MainWindowViewModel.Sandbox.cs` caveat props and the `SandboxPaths.axaml` caveat row.

**Interfaces (produces):**

```csharp
public abstract record GitRoute { public sealed record Native : GitRoute; public sealed record Wsl(string Distro, string LinuxRoot) : GitRoute; }
public static class GitRouting { public static GitRoute Decide(string rootPath, WslContext? wsl); } // UNC -> Wsl(distro from the path, linuxPath); "/..." absolute on Windows with a context -> Wsl(context distro, path); anything else -> Native
public enum SandboxPlatform { MacOS, Linux }
```

- [ ] **Step 1: Write the failing tests**: `GitRoutingTests` (UNC root, Linux root, drive root, relative root, no context); inspector tests that `when: linux` entries survive and `when: macos` entries drop when the platform is Linux, and the reverse; the picker translation tests (UNC accepted, drive refused with the message); hook test that the WSL chmod argv is issued for a UNC root and not for a native root (through the plain launcher delegate, no spawn).
- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement**. Remove the caveat everywhere (the denials are enforced by the kernel now).
- [ ] **Step 4: Run** the affected classes (`SandboxPathInspector`, `GitInvoker`, `HookInstaller`, view-model sandbox tests), `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** `feat(wsl): inspect the sandbox through wsl on windows` and `feat(wsl): route workspace git and hooks through wsl`.

---

### Task 4: Launch dispatch through WSL and deletion of the MXC path

**Files:**
- Modify: `src/VisualRelay.Core/Agent/Tools/SandboxedCommandExecutor.Launch.cs` (Windows arm: resolve the Linux workspace from the UNC root, apply `WslWorkspacePolicy`, shell verdicts run `/bin/sh -c <command>` inside the distro, then `WslLauncher.Build` with the nono prefix built from Linux paths; the `cmd.exe` batch path is removed for sandboxed runs), `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs` (`ResolveWindowsLaunch` becomes the WSL launch; the blocked message is the gate's), `SandboxedStage.ToolPresence.cs` (Windows arm reads `WslContext`; message names WSL and nono in the distro), `SandboxedStage.cs` (`BuildNonoPrefix` gets a path-mapper seam so on Windows `--profile` is the Linux profile path and `-a <templatesDir>` is the `/mnt/<drive>/...` translation; pure test), `ProjectBootstrapper.cs`/`ShellTestRunner.cs` (bootstrap validation shell runs through WSL on Windows for a UNC root), `ProcessCapture.RunAsync` callers that launch wsl.exe pass the `WslProcessTreeControl` and the pid file, delete `WindowsSandbox.cs`, `MxcPolicyGenerator.cs`, `MxcProvisioner.cs`, `MxcInstaller.cs`, `tools/VisualRelay.Cli/Commands/ProvisionMxcCommand.cs`, tests `WindowsSandboxTests.cs`, `MxcInstallerTests.cs`, `MxcRealSandboxTests.cs`, `WindowsCredentialDenyTests.cs`; remove the `provision-mxc` verb from `Program.cs`/`CommandRouter.cs` and from `RunAllModesTests*.cs` verb lists; remove the `MxcInstaller.cs` allowlist entry in `tools/VisualRelay.Guards/HttpClientConstructionGuard.cs`; reword the five "Unix nono wrapper (Windows uses the MXC seam)" skip messages; update `WindowsExecutionTests.cs` expectations (no `cmd.exe` batch, no Toolhelp kill for sandboxed runs); update the two `visual-relay` launcher comment lines that mention `provision-mxc` without changing its logic-line count.
- Create: tests `SandboxedTestRunnerWslLaunchTests.cs`, `SandboxedCommandExecutorWslLaunchTests.cs`, `BuildNonoPrefixWslPathsTests.cs` (all pure, using `WslContext.Override`).

- [ ] **Step 1: Write the failing tests**: for a UNC root with an overridden context, `ResolveLaunch("go test ./...")` yields `wsl.exe -d D --exec /bin/sh -c <envelope> vr <pidfile> /home/u/repo env ... /usr/local/bin/nono run --profile /home/u/.config/visual-relay/vr-guard.json --allow-cwd ... -- /bin/sh -c "go test ./..."`; a `/mnt/c` root is refused with the policy message; the executor's launch for a shell verdict has the same shape; `BuildNonoPrefix` on the WSL mapper emits Linux profile and `/mnt/c/...` templates paths; the CLI verb list no longer contains `provision-mxc`.
- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement** the rewiring, then the deletions; `grep -rn "Mxc\|wxc\|MXC" src tools tests` must return nothing.
- [ ] **Step 4: Run** `./visual-relay test Sandboxed`, `./visual-relay test RunAllModes`, `./visual-relay test WindowsExecution`, `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** `feat(wsl): launch sandboxed commands through wsl on windows` and `refactor(sandbox): delete the mxc path` (body states plainly that the WSL runtime proof on a Windows machine is outstanding and where the checklist lives).

---

### Task 5: Docs, launcher script, CI probe workflow, Windows-gated probe tests

**Files:**
- Modify: `README.md` (line 10 "nono on macOS, Linux and Windows (inside WSL2)"; the Windows install section: WSL2 + a distro + nono 0.75.0 in it, `VR_WSL_DISTRO`, the workspace must live on the distro filesystem and is opened as `\\wsl.localhost\<distro>\...`, build and test commands run inside WSL, Windows-only toolchains are not supported), `TROUBLESHOOTING.md` (replace the MXC "Task execution is blocked" entry with the WSL gate messages, `VR_WSL_DISTRO`, the `/mnt` refusal, the UTF-8 note, the `[automount]` failure, the state-locations row for the profile inside the distro and the removal of the old `mxc-policy.json`; add "Windows runtime verification checklist" with the exact commands from the notes §4.6 and the statement that these were not run by the change that introduced WSL support), `docs/OPERATIONS.md` (Sandbox section: the same profile on all three platforms; Windows enforces through the distro's kernel; no "may be readable" caveat), `AGENTS.md` (one paragraph under the sandbox/agent section: Windows requires WSL2 and drives repos whose toolchain lives in the distro), `visual-relay.ps1` (keep .NET SDK and git provisioning; drop the Git-for-Windows `usr\bin` PATH rationale that existed for command resolution; add one line pointing at the WSL requirement).
- Create: `.github/workflows/wsl-probe.yml` (`workflow_dispatch`; `runs-on: windows-2025`; steps: `wsl --install Ubuntu --no-launch` then `wsl -d Ubuntu --exec dbus-launch true`; install `nono-cli_0.75.0_amd64.deb` from the v0.75.0 release inside the distro; print `uname -r`, `/sys/kernel/security/lsm`, `nono --version`; `dotnet test` the Windows-gated classes with `VR_RUN_NONO_INTEGRATION=1`; upload the outputs as an artifact), tests `WslArgvRoundTripTests.cs` (receiver `printf '%s\0' "$@"` through the real launcher; asserts the received bytes for the hostile argument set), `WslWatchdogKillsHungTreeTests.cs` (`sleep 3600` under the envelope; stop through `WslProcessTreeControl`; assert `ps -p <pid>` fails inside the distro), `WslConfinementProbeTests.cs` (the spec's verdict table on ext4 and on `/mnt/c`: workspace write, workspace delete, git, toolchain cache write, `~/Documents` write denied, `~/.ssh` read denied, network reachable, `/usr/bin` readable; record wall times for `git status` on both), `WslExitCodeAndUtf8Tests.cs` (`exit 42` returns 42; `printf 'héllo\n'` round trips). All four skip unless Windows with a usable `WslContext` and the opt-in variable; they must compile and skip cleanly on macOS.

- [ ] **Step 1: Write the gated tests** (they skip here; make each assertion concrete so a Windows run is a real proof).
- [ ] **Step 2: Write the docs and the workflow.**
- [ ] **Step 3: Run** `./visual-relay test Wsl` (the gated classes report skipped), `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`. Validate the workflow YAML parses (`python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/wsl-probe.yml'))"` or `ruby -ryaml`).
- [ ] **Step 4: Commit** `docs: describe the windows wsl requirement and its toolchain consequence` and `ci: add a manual wsl probe workflow with windows-gated tests`.

---

### After the tasks

The controller retires the spec into `llm-tasks/completed/` with a commit body that says the Windows runtime items (Landlock line at runtime, DrvFs verdicts and timings, watchdog kill proof, exit-code and UTF-8 round trip, GUI end to end) are unverified and how to run them.
