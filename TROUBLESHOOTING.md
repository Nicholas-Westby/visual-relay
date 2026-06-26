# Troubleshooting

Operational notes for the dev loop. Add entries as you hit (and solve) things.

## A test run hangs / never finishes

`./visual-relay test` normally finishes in ~10s. If it sits at `Testing (NNNs)` with the
counter climbing and **no test ever completing**, a test has deadlocked — it's hung, not slow
(a slow test still eventually prints `Passed … [NNNNN ms]`).

Find the culprit — abort after 30s of inactivity and dump which test(s) were running:

```bash
./visual-relay test --blame-hang --blame-hang-timeout 30s
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

## Backend state lives under `$XDG_DATA_HOME/visual-relay/`

The model backend (LiteLLM proxy) keeps all per-machine state in your user data
directory, never in the repo tree. This prevents host/VM venv collisions when the
working copy is shared.

| What | Location |
|------|----------|
| LiteLLM venv | `$XDG_DATA_HOME/visual-relay/backend-venv/` |
| Scratch (pidfile, log, generated config) | `$XDG_DATA_HOME/visual-relay/scratch/` |
| LiteLLM log | `$XDG_DATA_HOME/visual-relay/scratch/litellm.log` |
| Pidfile | `$XDG_DATA_HOME/visual-relay/scratch/litellm.pid` |

`XDG_DATA_HOME` defaults to `~/.local/share` if unset, so typical paths are
`~/.local/share/visual-relay/backend-venv/` and
`~/.local/share/visual-relay/scratch/`.

The venv is a **disposable cache** — you can delete it at any time. The next
`start` rebuilds it from pinned inputs. If a venv becomes broken (e.g. its Python
interpreter path goes stale after a uv update), the launch detects this and
rebuilds automatically.

Legacy repo-local state (`tools/backend/.venv/` and `.relay-scratch/`) is cleaned
up on first start by the new code.

## Windows

**State locations.** Windows has no `XDG_DATA_HOME`/`HOME`, so Visual Relay falls back to the
standard Windows folders (XDG/`HOME` still win when explicitly set):

| What | Location |
|------|----------|
| UI state, settings (`.env`), sandbox policy | `%APPDATA%\visual-relay\` |
| LiteLLM venv, scratch (pidfile, log) | `%LOCALAPPDATA%\visual-relay\` |
| Provisioned .NET SDK (when the launcher installs it) | `%LOCALAPPDATA%\visual-relay\dotnet\` |

**`.\visual-relay` is blocked / "running scripts is disabled".** The PowerShell execution
policy is blocking the launcher. The `.cmd` shim already passes `-ExecutionPolicy Bypass`, so
prefer `.\visual-relay launch` (which runs `visual-relay.cmd`). To run the `.ps1` directly,
`powershell -ExecutionPolicy Bypass -File visual-relay.ps1 launch`.

**`dotnet` not found after the launcher installed it.** The launcher prepends its install dir to
PATH for that session only (no global machine change). Re-run through `.\visual-relay`, or add
`%LOCALAPPDATA%\visual-relay\dotnet` to your PATH for a standalone `dotnet`.

**Task execution is blocked.** Windows confines writes with Microsoft Execution Containers
(MXC); when `wxc-exec` is not provisioned and no opt-in is set, execution is blocked rather
than run uncontained. Run `visual-relay provision-mxc` to download and install the pinned,
Microsoft-signed `wxc-exec` runtime into `%LOCALAPPDATA%\visual-relay\mxc\` (a no-op if it is
already present); set `VR_WINDOWS_SANDBOX=builtin` for swival's degraded sandbox; or run
execution inside WSL2 with `nono`. Inspection (queue, logs, traces, settings) works without any
sandbox.

Write-confinement is empirically verified against the real `wxc-exec` (a command writing
outside the workspace is denied, inside is allowed). Where the BaseContainer/processcontainer
backend is unavailable, MXC falls back to the **AppContainer + DACL** tier; for the fewest
caveats run `wxc-host-prep prepare-system-drive` (elevated) once so AppContainer processes can
read the system-drive root metadata. The confinement policy lists only writable roots that
exist — a missing toolchain-cache dir is never sent to `wxc-exec` (it would fail to stamp it).

**Git hooks.** `install-hooks` works on Windows through Git for Windows' bundled bash (the
pre-commit hook is `#!/usr/bin/env bash`); a working `git` on PATH is required.
