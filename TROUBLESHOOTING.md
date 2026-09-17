# Troubleshooting

Operational notes for the dev loop. Add entries as you hit (and solve) things.

## A test run hangs / never finishes

`./visual-relay test` normally finishes in ~10s. If it sits at `Testing (NNNs)` with the
counter climbing and **no test ever completing**, a test has deadlocked — it's hung, not slow
(a slow test still eventually prints `Passed … [NNNNN ms]`).

Find the culprit — abort after 30s of inactivity and dump which test(s) were running:

```bash
./visual-relay test --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none
```

The output prints `The test running when the crash occurred:`. If **two or more** tests are
listed, they were running concurrently and likely deadlocked on shared global state — our
Avalonia headless UI tests each spin up a process-global Avalonia app via
`HeadlessUnitTestSession.StartNew`, and xUnit runs separate test classes in parallel by
default, so two headless classes overlapping can wedge each other.

Cleanup: the hang dump is multi-GB. `TestResults/` is gitignored — delete it when done:

```bash
rm -rf tests/VisualRelay.Tests/TestResults
```

## Headless UI tests must use `[AvaloniaFact]`

Avalonia headless uses **one process-global app/dispatcher per process**. Hand-rolling a
session — `HeadlessUnitTestSession.StartNew(...)` inside a plain `[Fact]` — lets xUnit's
parallel test collections start two sessions at once, which either deadlocks the suite (the
hang above) or throws `the calling thread cannot access this object because a different thread
owns it`. All headless UI tests therefore use `[AvaloniaFact]`/`[AvaloniaTheory]`
(`Avalonia.Headless.XUnit`), which run every UI test on a single shared, serialized session.

`HeadlessUnitTestSession` is **banned** via `Microsoft.CodeAnalysis.BannedApiAnalyzers`
(`tests/VisualRelay.Tests/BannedSymbols.txt`); reintroducing it fails the build (RS0030).

## Serial test mode for trustworthy per-test timings

The default parallel run packs many tests into ~92s of wall clock, but a test's reported
duration is dominated by time queued between awaits, not real work (e.g. a 0.02s test
reports 29s). To get real per-test numbers:

```bash
./visual-relay test serial              # full suite, one collection at a time
./visual-relay test serial GitCommitter # filter within serial mode
```

Serial mode appends `-- xUnit.ParallelizeTestCollections=false` and raises the watchdog
timeout to 1800s (unless `VISUAL_RELAY_TEST_TIMEOUT` is set). The stderr output prints a
`serial mode:` banner so it is obvious in saved logs.

## Leftover backend state under `$XDG_DATA_HOME/visual-relay/`

Until 2026-09-01 a local model gateway ran behind every stage, provisioned into
a `uv`-built Python venv under your user data directory. The gateway, the venv
and the `uv` dependency are gone: providers are called directly, in process.

Nothing reads these any more, so delete them if you want the space back:

| What | Location |
|------|----------|
| Gateway venv | `$XDG_DATA_HOME/visual-relay/backend-venv/` |
| Pidfile, log, generated config | `$XDG_DATA_HOME/visual-relay/scratch/` |

`XDG_DATA_HOME` defaults to `~/.local/share` if unset. Settings and sandbox
policy live elsewhere and are still in use — remove only the two paths above.

## Stage 5 records `unproven`

The Author-tests gate proves the new tests fail before the change exists. When it
cannot, it records `check: unproven` on the stage plus a `reason`, and the task
continues (Verify still gates the end result). The reason names what to fix:

| Reason | What happened | What to do |
|--------|---------------|------------|
| `no test files declared` | The stage returned an empty `testFiles`, so there was nothing to gate. | Fine for a docs-only or config-only change; otherwise the task needs a test. |
| `placeholder test command` | `testCmd` is the bootstrap placeholder: it exits 0 having run nothing. | Set a real `testCmd` in `.relay/config.json`, or re-run `bootstrap` once the project has a toolchain. |
| `gate command unusable` | The command exited 127 (not found) or reported that it collected no tests. | Check `testFileCmd`: its `{files}` expansion must name runnable test files for this project. |
| `no implementation to strip` | The new tests pass against the current code, and nothing outside them could be taken away to make them fail. | So either the behaviour already exists (regression coverage, which is fine) or the tests do not exercise the change. Read the stage-5 diff to tell which. |
| `green before implementation` | The tests passed with everything still in place, and at least one declared test file can carry implementation. The stage was re-asked once and still came back green. | Read the stage-5 diff: the change was probably implemented inside the test file, or the tests do not exercise it. |

Every outcome is in `run.log` as a `verify_result` for stage 5, naming the
command that ran, its exit code, what was stripped and how each declared file was
classified — plus an `author_test_unproven` warning for the reason above.

## Repositories with unusual test layouts

Whether a test can be told from an implementation by its path is a property of the
language. Bootstrap detects it and writes `authorTests` into `.relay/config.json`
(the keys are documented in [docs/OPERATIONS.md](docs/OPERATIONS.md)). Three knobs
fix a repository it reads wrongly:

- **`testPaths`** (top level) — extra globs that count as test paths, on top of the
  built-in filename and directory conventions: `["spec/**", "examples/*_example.go",
  "t/**"]`. Use it when a scope check keeps warning `author_test_scope_suspect`
  about files that really are this project's tests. Bootstrap only creates the key;
  it never overwrites your globs.
- **`authorTests.inlineTestExtensions`** — extensions whose files may carry tests
  beside the implementation. Every file on the model's test-file list is kept
  whatever its extension; an inline-capable extension only changes how the gate
  judges it (the as-is red check rather than the path).
  Bootstrap fills it from the detected languages (`[".rs"]` for Rust, `[".zig"]`
  for Zig, and so on) and empties it for a repository whose languages all keep
  tests in files of their own.
- **`authorTests.diffAudit`** — whether a cheap model call also reads the stage's
  diff for hunks that change behavior instead of asserting it. Set it to `"off"`
  in a repository where that call keeps asking for a re-ask it should not, or to
  `"always"` to audit every Author-tests diff, however the files are classified.

To opt a language in whose inline mechanism you actually use, add its extension by
hand: `".ml"` for OCaml's `ppx_inline_test`, `".erl"` for EUnit behind
`-ifdef(TEST)`, `".ts"` for in-source Vitest, `".move"` for in-module `#[test]`.
The key is per extension, so a Rust and Python repository can treat `.rs` as
inline-capable while `.py` stays gated by path. Re-running `bootstrap` refreshes
`detectedLanguages` and `inlineTestExtensions`, so record a hand-added extension
somewhere you will remember.

## Reset left files in the working tree

`reset-selected` puts the tree back to the run base and deletes the untracked files
the run authored, after re-capturing everything into the archived bundle. Two cases
leave files behind on purpose, and `statusText` says which one you got:

- **A run is active.** The running task owns the tree, so Reset archives the flagged
  run only and says `the working tree was left as is because a run is active`. Reset
  again once the run ends.
- **No pre-run snapshot.** Without `pre-run-untracked.txt` the resetter cannot tell
  the run's untracked files from ones you wrote, so it deletes none of them and says
  `untracked files kept: no pre-run snapshot`. Tracked edits are still reverted.

A third line, `tree reset failed: <error>`, means git refused; the flagged run is
still archived, so nothing is lost, and the tree is yours to clear.

## `vr-control: slow request ...` on the app's stderr

The control server writes that line for any request other than `GET /screenshot`
that took longer than two seconds, naming the method, the path and the
milliseconds. Every command and `/state` are serialized on the UI thread, so a
slow one means something was holding that thread at that moment, not that the
network was slow. `/screenshot` is exempt: it renders the window and is slow by
nature.

Treat the line as the starting point of a report, not a fault in itself: note
the request it names and read the run log around that timestamp to see what the
app was doing. A slow `/state` right after `cancel` was seen twice on Windows
and has no reproduction yet.

## A target repo already tracks `.relay` run artifacts

Older versions force-added each run's bookkeeping (ledger, seals, manifest,
status, per-stage input/report) into the task commit, so a repo Visual Relay ran
against before will have thousands of `.relay/` paths in its history. Nothing
stages them any more, but the ones already tracked keep showing up in
`git status` whenever a run rewrites them.

Untrack them once, from the repo root — the files stay on disk and every run
keeps using them:

    git rm -r --cached --quiet -- .relay ':(exclude).relay/config.json' ':(exclude).relay/.gitignore'

Then commit the removal. Keep `.relay/config.json` tracked if you want the
target's config shared with the repo; `.relay/.gitignore` keeps the rest out.

## Windows

Windows runs the sandbox as `nono` inside a WSL2 distro: the same binary and the same
`vr-guard` profile macOS and Linux use, enforced by the distro's kernel (Landlock).
Visual Relay itself is still a Windows app; the commands it runs are Linux.

**State locations.** Windows has no `XDG_DATA_HOME`/`HOME`, so Visual Relay falls back to the
standard Windows folders (XDG/`HOME` still win when explicitly set):

| What | Location |
|------|----------|
| UI state, settings (`.env`) | `%APPDATA%\visual-relay\` |
| Provisioned .NET SDK (when the launcher installs it) | `%LOCALAPPDATA%\visual-relay\dotnet\` |
| Sandbox profile (`vr-guard.json`) | `~/.config/visual-relay/` **inside the distro**, written through `\\wsl.localhost\<distro>\...` |
| Planning worktrees, verify snapshots, the flagged-work index | `~/.cache/visual-relay/wt/…` and `/tmp/visual-relay/` **inside the distro** (the git that serves a workspace runs there, and a Windows temp path means nothing to it) |

An `mxc-policy.json` beside the settings is left over from the deleted Windows sandbox.
Nothing reads it any more — delete it.

**`.\visual-relay` is blocked / "running scripts is disabled".** In PowerShell, `.\visual-relay`
resolves to `visual-relay.ps1`, not to the `.cmd`, and a stock Windows 11 policy (Restricted)
refuses to run it. Type `.\visual-relay.cmd launch`: the shim passes `-ExecutionPolicy Bypass`
for you. To run the `.ps1` directly, `powershell -ExecutionPolicy Bypass -File visual-relay.ps1 launch`.

**`dotnet` not found after the launcher installed it.** The launcher prepends its install dir to
PATH for that session only (no global machine change), and finds that dir again on every later
launch. Re-run through `.\visual-relay.cmd`, or add `%LOCALAPPDATA%\visual-relay\dotnet` to your
PATH for a standalone `dotnet`.

**Task execution is blocked.** The launch gate checks five things — WSL, a WSL2 distro, nono
inside it, Landlock active there, and git inside it — and names the first one that fails
together with its fix. There is no opt-out and no unsandboxed fallback; inspection (queue,
logs, traces, settings) works without any sandbox. The same message is what `bootstrap` and
`Create config` print when they refuse, because on a Windows host with no usable distro a
test command could only be checked on the Windows host, where the pipeline will never run it.

| What the gate says | What to do |
|--------------------|------------|
| WSL is not installed (Windows 11 ships a wsl.exe that only offers to install WSL) | `wsl --install -d Ubuntu` in an elevated PowerShell, reboot, then `wsl -d Ubuntu` once to create your Linux user |
| no WSL distro is installed | the same, then re-run |
| the WSL distro `<name>` selected by `VR_WSL_DISTRO` is not installed | install that one, or point `VR_WSL_DISTRO` at an installed distro (unset it to use the default) |
| `<name>` is a WSL1 distro | `wsl --set-version <name> 2` |
| nono was not found inside the WSL distro `<name>` | install nono 0.75.0 in the distro (`curl -fsSL https://nono.sh/install.sh \| NONO_VERSION=v0.75.0 sh`, see the README) and check `wsl -d <name> --exec sh -lc 'command -v nono'` |
| Landlock is not active in the WSL distro `<name>` | remove a custom `kernel=` or `kernelCommandLine=` line from `%UserProfile%\.wslconfig`, then `wsl --update` and `wsl --shutdown`; stock kernels have enabled Landlock since 5.15.57.1 |
| the home directory of the default user could not be read | run `wsl -d <name>` once so the distro finishes its first-run user setup |
| git was not found inside the WSL distro `<name>` | install it there (`sudo apt install -y git` on Ubuntu) and check `wsl -d <name> --exec sh -lc 'command -v git'`; every workspace git call runs in the distro, so one without git cannot drive a repository |

Every message ends with the same consequence: the build and test commands of the repository
you point Visual Relay at run **inside that distro**, so their toolchain must be installed
there, and a Windows-only toolchain (MSBuild against .NET Framework, Visual Studio build
tools, Unity on Windows, anything that needs an `.exe`) is not supported.

**Choosing the distro.** Visual Relay uses the WSL default distro. `VR_WSL_DISTRO=<name>`
picks another one; unset it to go back to the default. `wsl -l -v` lists what is installed.

**"The workspace … is a Windows drive seen through DrvFs".** A workspace under `/mnt/<letter>`
(that is, any `C:\...` folder, including one picked through the folder dialog) is refused. DrvFs is roughly
an order of magnitude slower for the many small files a build touches, and its permission
model is not the one Landlock was designed against. Move the repository onto the distro's own
filesystem (`/home/<user>/...`) and open it as `\\wsl.localhost\<distro>\home\<user>\...`.

**Non-ASCII output looks like mojibake.** wsl.exe's own output (the distro listing, its error
text) is UTF-16LE without a BOM unless `WSL_UTF8=1` is set. Visual Relay sets it — with
`WSL_DISABLE_WARNINGS=1` — on every wsl.exe it launches, and decodes both streams as UTF-8.
Reproducing a command by hand in PowerShell needs `$env:WSL_UTF8 = '1'` first, or what you get
back will not parse.

**"Failed to write the vr-guard sandbox profile … inside the WSL distro".** The profile is
written through the distro's `\\wsl.localhost` share, which WSL only serves while `[automount]`
`enabled = true` (the default) in the distro's `/etc/wsl.conf`. With automount and `mountFsTab`
both off the Plan 9 server never starts and the share is unreachable. Re-enable it, then
`wsl --shutdown` and start the distro again.

**Git hooks.** `install-hooks` works on Windows through Git for Windows' bundled bash (the
pre-commit hook is `#!/usr/bin/env bash`); a working `git` on PATH is required. A hook
installed into a workspace inside the distro is made executable through WSL, because files
created from Windows over the share land without the execute bit.

## Windows runtime verification checklist

**These commands were not run by the change that introduced WSL support** — no Windows machine
was available to it. Everything below is unverified and is what a person with Windows should
run before trusting the Windows arm:

- Landlock active at runtime in the WSL2 kernel
- the DrvFs (`/mnt/c`) enforcement verdicts and the ext4-versus-DrvFs timings
- the watchdog kill proof (the Linux process is gone, not just wsl.exe)
- the exit-code and UTF-8 round trip through wsl.exe
- that planning worktrees, verify snapshots and the flagged-work index live inside the
  distro (`~/.cache/visual-relay/wt/…` and `/tmp/visual-relay/`), never under the Windows
  temp directory — a run whose worktree lands in `C:\…` is the failure this checks for
- the `-a <templates dir>` DrvFs grant
- the folder-picker UNC round trip
- the GUI end to end through the control API

What the Windows-gated tests already prove, run with `VR_RUN_NONO_INTEGRATION=1` on
Windows (`./visual-relay test Wsl`; they skip everywhere else) and by the manual
`wsl-probe` GitHub Actions workflow on a `windows-2025` runner:

| Gated class | What it proves |
|-------------|----------------|
| `WslConfinementProbeTests` | the eight confinement verdicts and their timings, on the distro's own filesystem and on DrvFs — including the `~/.ssh` read denial the deleted Windows sandbox could not enforce. It runs at all only when the gate found Landlock active, so a pass is also the Landlock evidence |
| `WslWatchdogKillsHungTreeTests` | the kill proof: the Linux process is gone, not just wsl.exe |
| `WslExitCodeAndUtf8Tests` | the exit code, plain and through the envelope's `wait`, and the UTF-8 round trip |
| `WslArgvRoundTripTests` | the hostile argv reaching the Linux child byte for byte |

The Windows temp-directory start is pinned by the pure launch tests, which need no
Windows machine; everything else on the list above still needs a person at one. By hand,
in PowerShell, with `$D = 'Ubuntu'`:

```powershell
$env:WSL_UTF8 = '1'
wsl --version; wsl --status; wsl -l -v                       # WSL >= 2.x, distro VERSION 2
wsl -d $D --exec uname -r                                     # …-microsoft-standard-WSL2
wsl -d $D --exec sh -c 'dmesg 2>/dev/null | grep -i landlock || journalctl -kb -g landlock'
# nono 0.75.0 inside the distro
wsl -d $D --exec sh -c 'wget -q https://github.com/nolabs-ai/nono/releases/download/v0.75.0/nono-cli_0.75.0_amd64.deb && sudo dpkg -i nono-cli_0.75.0_amd64.deb'
wsl -d $D --exec sh -lc 'command -v nono && nono --version && nono setup --check-only'   # "Landlock enabled (syscall probe)"; the gate reads this, since /sys/kernel/security/lsm is missing on systemd distros
# profile + confinement probe on ext4, then on DrvFs
wsl -d $D --exec sh -lc 'mkdir -p ~/.config/visual-relay && cp /mnt/c/path/to/visual-relay/packaging/nono/vr-guard.json ~/.config/visual-relay/'
wsl -d $D --exec sh -lc 'W=$(mktemp -d ~/vr-probe.XXXX); cd $W; git init -q; P=~/.config/visual-relay/vr-guard.json;
  nono run --profile $P --allow-cwd --silent -- sh -c "echo ok > in.txt && rm in.txt && git status --short && echo WS_OK";
  nono run --profile $P --allow-cwd --silent -- sh -c "touch ~/Documents/vr-probe && echo DOC_WRITE_ALLOWED || echo DOC_WRITE_DENIED";
  nono run --profile $P --allow-cwd --silent -- sh -c "cat ~/.ssh/id_* >/dev/null 2>&1 && echo SSH_READ_ALLOWED || echo SSH_READ_DENIED";
  nono run --profile $P --allow-cwd --silent -- sh -c "ls /usr/bin >/dev/null && echo USRBIN_OK";
  nono run --profile $P --allow-cwd --silent -- sh -c "curl -sI https://api.github.com >/dev/null && echo NET_OK";
  nono run --profile $P --allow-cwd --silent -- sh -c "mkdir -p ~/.npm && echo x > ~/.npm/vr-probe && echo CACHE_OK"'
# repeat the block with W under /mnt/c/vr-probe (DrvFs); record each verdict and `time` for `git status` on a real repo in both places
# exit code + UTF-8 through wsl.exe (from PowerShell)
wsl -d $D --exec sh -c 'exit 42'; $LASTEXITCODE                # 42
wsl -d $D --exec printf 'h\xc3\xa9llo\n' | Format-Hex           # UTF-8 bytes intact
# argv fidelity
wsl -d $D --exec printf '[%s]\n' 'a b' 'c"d' "e'f" 'g\h' '$i' '`j`' 'ü'   # one line per arg, verbatim
# kill proof (SIMPLIFIED envelope: the real one also creates the pid directory and
# cds into the workspace, see WslLauncher.Envelope)
wsl -d $D --exec /bin/sh -c 'p=$1; shift; setsid "$@" & c=$!; echo $c > $p; wait $c' vr /tmp/vr.pid sleep 3600
# in a second shell:
$pid = (wsl -d $D --exec cat /tmp/vr.pid); wsl -d $D --exec kill -TERM -- -$pid; wsl -d $D --exec ps -p $pid   # must fail
# CPU sampling shape
wsl -d $D --exec ps -axo pid=,ppid=,time=
# securityfs / tools presence for the gate
wsl -d $D --exec sh -c 'command -v setsid ps cat && mount | grep securityfs'
# then: ./visual-relay launch, pick \\wsl.localhost\Ubuntu\home\<u>\repo, run a task via the control API (127.0.0.1:8765)
```

Record the Landlock line, the kernel release, the nono version, the eight verdicts on ext4 and
on DrvFs, the `git status` wall time on both, the kill proof, the exit code and the hex bytes.
