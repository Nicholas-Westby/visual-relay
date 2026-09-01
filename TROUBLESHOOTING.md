# Troubleshooting

Operational notes for the dev loop. Add entries as you hit (and solve) things.

## A test run hangs / never finishes

`./visual-relay test` normally finishes in ~10s. If it sits at `Testing (NNNs)` with the
counter climbing and **no test ever completing**, a test has deadlocked — it's hung, not slow
(a slow test still eventually prints `Passed … [NNNNN ms]`).

Find the culprit — abort after 30s of inactivity and dump which test(s) were running:

```bash
./visual-relay test --blame-hang --blame-hang-timeout 120s
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

Until 2026-09-01 a LiteLLM proxy ran behind every stage, provisioned into a
`uv`-built Python venv under your user data directory. The proxy, the venv and
the `uv` dependency are gone: providers are called directly, in process.

Nothing reads these any more, so delete them if you want the space back:

| What | Location |
|------|----------|
| LiteLLM venv | `$XDG_DATA_HOME/visual-relay/backend-venv/` |
| Pidfile, log, generated config | `$XDG_DATA_HOME/visual-relay/scratch/` |

`XDG_DATA_HOME` defaults to `~/.local/share` if unset. Settings and sandbox
policy live elsewhere and are still in use — remove only the two paths above.

## Windows

**State locations.** Windows has no `XDG_DATA_HOME`/`HOME`, so Visual Relay falls back to the
standard Windows folders (XDG/`HOME` still win when explicitly set):

| What | Location |
|------|----------|
| UI state, settings (`.env`), sandbox policy | `%APPDATA%\visual-relay\` |
| Provisioned .NET SDK (when the launcher installs it) | `%LOCALAPPDATA%\visual-relay\dotnet\` |

**`.\visual-relay` is blocked / "running scripts is disabled".** The PowerShell execution
policy is blocking the launcher. The `.cmd` shim already passes `-ExecutionPolicy Bypass`, so
prefer `.\visual-relay launch` (which runs `visual-relay.cmd`). To run the `.ps1` directly,
`powershell -ExecutionPolicy Bypass -File visual-relay.ps1 launch`.

**`dotnet` not found after the launcher installed it.** The launcher prepends its install dir to
PATH for that session only (no global machine change). Re-run through `.\visual-relay`, or add
`%LOCALAPPDATA%\visual-relay\dotnet` to your PATH for a standalone `dotnet`.

**Task execution is blocked.** Windows confines writes with Microsoft Execution Containers
(MXC); when `wxc-exec` is not provisioned, execution is blocked rather than run uncontained —
there is no opt-out. Run `visual-relay provision-mxc` to download and install the pinned,
Microsoft-signed `wxc-exec` runtime into `%LOCALAPPDATA%\visual-relay\mxc\` (a no-op if it is
already present), or run execution inside WSL2 with `nono`. Inspection (queue, logs, traces,
settings) works without any sandbox.

Write-confinement is empirically verified against the real `wxc-exec` (a command writing
outside the workspace is denied, inside is allowed). Where the BaseContainer/processcontainer
backend is unavailable, MXC falls back to the **AppContainer + DACL** tier; for the fewest
caveats run `wxc-host-prep prepare-system-drive` (elevated) once so AppContainer processes can
read the system-drive root metadata. The confinement policy lists only writable roots that
exist — a missing toolchain-cache dir is never sent to `wxc-exec` (it would fail to stamp it).

**Git hooks.** `install-hooks` works on Windows through Git for Windows' bundled bash (the
pre-commit hook is `#!/usr/bin/env bash`); a working `git` on PATH is required.
