# Visual Relay

Visual Relay is an Avalonia desktop control room for Relay-style LLM task processing. It brings a staged task pipeline into a modern dark GUI: choose a repository root, inspect and reorder the task queue, pause at safe boundaries, and drill into stages, logs, and live LLM command traces.

![Visual Relay main window](docs/images/visual-relay-main.png)

## Install

<!-- BEGIN install section (self-contained; sibling tasks may shorten the README) -->

The recommended way to run Visual Relay is to **clone the repo and launch it with the
`./visual-relay` wrapper** — one command bootstraps everything else:

```bash
git clone https://github.com/Nicholas-Westby/visual-relay.git
cd visual-relay
./visual-relay launch
```

`./visual-relay` is a tiny launcher that provisions its own toolchain via
[Nix](https://nixos.org): on first run it enters a **nix devshell** supplying `dotnet`,
`python3.13`, `uv`, and `nono` from the Nix store — zero global installs. `uv` provisions the
LiteLLM model backend on first launch; `nono` provides OS-level sandboxing (Seatbelt on macOS,
Landlock on Linux) for Swival subagents. The sandbox is always on; `nono` is a hard requirement.

**No Nix yet?** On an interactive terminal the launcher offers a single `[y/N]` prompt to
install [Determinate Nix](https://determinate.systems/nix) (the only global change) and re-enters
through `nix develop`. A `n`/Enter or any non-interactive context (CI, piped) prints a manual
one-liner and exits — nothing global is installed without explicit per-invocation consent.

This is a terminal app for developers; there is no `.app` bundle to double-click. To prepare your
own repository, run `./visual-relay init` in it first (it auto-detects your test command and
writes `.relay/config.json`), then `./visual-relay launch` and point the folder picker at a repo
containing `llm-tasks/`. Common commands: `build`, `test`, `check`, `install-hooks`. See
[AGENTS.md](AGENTS.md) for contributor dev tooling (`sample`, `run-task`, `screenshot`).

### Windows

Nix does not run on native Windows, so Windows uses a sibling launcher that provisions the same
toolchain natively (the `gradlew`/`gradlew.bat` pattern). Clone the repo and run:

```powershell
git clone https://github.com/Nicholas-Westby/visual-relay.git
cd visual-relay
.\visual-relay launch
```

`visual-relay.cmd` hands off to `visual-relay.ps1`, which detects the .NET 10 SDK (offering
Microsoft's official installer with one consent prompt when absent, into
`%LOCALAPPDATA%\visual-relay`), hints `uv` and `git` if missing, then runs the same C# CLI. A
non-interactive run installs nothing and prints the manual one-liners. UI state and settings
persist under `%APPDATA%`/`%LOCALAPPDATA%`.

The GUI is fully supported on Windows for **inspection** — pick a repo, browse the queue, read
logs and live traces, and edit settings. Task **execution** is gated by the sandbox: there is no
`nono` on Windows, so writes are confined by [Microsoft Execution
Containers](https://github.com/microsoft/mxc) (`wxc-exec`) instead — run `./visual-relay
provision-mxc` to install the signed runtime (write-confinement is verified against it). When no
sandbox is available, execution is **blocked** (never silently uncontained); set
`VR_WINDOWS_SANDBOX=builtin` to opt into swival's own degraded sandbox, or run execution inside
WSL2 with `nono` for the strongest isolation.

<!-- END install section -->

## What it does

- Runs `llm-tasks/` one task at a time through the staged Relay pipeline, writing ledger,
  manifest, seal, event, report, and trace artifacts.
- Presents a dense, dark command-center GUI: native root selection, queue/archive controls,
  per-stage status, structured run logs, and stage cards that double as log filters.
- Streams Swival trace events live into the GUI as assistant text, tool calls, tool results,
  and thinking records.
- Estimates time and rounded dollar cost per task and per stage from Swival reports.

See [docs/DESIGN.md](docs/DESIGN.md) for the full architecture and the 11-stage Relay mapping.

## Tests

```bash
./visual-relay check
```

Runs the file-size guard, format verification, build, the test suite, and the README
screenshot render.

## Learn more

- [docs/OPERATIONS.md](docs/OPERATIONS.md) — model backend (LiteLLM proxy lifecycle) and the nono sandbox.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — diagnosing the dev loop and test hangs.
- [.env.example](.env.example) — provider keys, locations, and resolution precedence.
- [AGENTS.md](AGENTS.md) — contributing, the control API, and dev-only tooling.
