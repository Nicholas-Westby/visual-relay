# Visual Relay

Visual Relay is a cross-platform desktop app that helps you build software by processing tasks with
LLMs using a relay-like pipeline, with each stage passing its output to the following stage.
You create markdown files as specs, and Visual Relay implements them via the pipeline.

- Mistakes are avoided by enforcing a strict set of steps the LLM can't bypass (e.g., red/green TDD).
- Costs are optimized by choosing an appropriate LLM model tier per stage (and by enforcing a budget).
- It also gives you easy ways to observe each step via the activity panel.
- All LLM interactions are sandboxed ([nono](https://nono.sh/) on macOS and [mxc](https://github.com/microsoft/mxc) on Windows) to avoid destructive file system changes.

![Visual Relay main window](docs/images/visual-relay-main.png)

## Install

<!-- BEGIN install section (self-contained; sibling tasks may shorten the README) -->

The recommended way to run Visual Relay is to **clone the repo and launch it with the
`./visual-relay` wrapper** (one command bootstraps everything else):

```bash
cd ~/repositories # or wherever you keep your repos
git clone https://github.com/Nicholas-Westby/visual-relay.git
cd visual-relay
./visual-relay launch
```

`./visual-relay` is a tiny launcher that provisions its own toolchain via
[Nix](https://nixos.org) / [Determinate Nix](https://determinate.systems/posts/determinate-nix-installer)
(this avoids global installs). The bootstrap provides `uv` (LiteLLM backend) and
`nono` (sandbox) as prerequisites.

You can then run `./visual-relay` in that folder the next time you want to launch it.

## Install (Windows)

Clone the repo and run (nix doesn't run on Windows, so dependencies are installed globally):

```powershell
git clone https://github.com/Nicholas-Westby/visual-relay.git
cd visual-relay
.\visual-relay launch
```

You can then run `./visual-relay` in that folder the next time you want to launch it.

<!-- END install section -->

# What Visual Relay Does

- Runs `llm-tasks/` one task at a time through the staged relay pipeline, writing ledger,
  manifest, seal, event, report, and trace artifacts. These help keep the pipeline honest.
- Presents a command center GUI: select your project folder, queue/archive controls,
  per-stage status, structured run logs, and stage cards that double as log filters.
- Streams [Swival](https://swival.dev/) trace events live into the GUI as assistant text,
  tool calls, tool results, and thinking records.
- Estimates time and monetary cost per task and per stage from Swival reports.

# Tests

To run the main test suite:

```bash
./visual-relay test
```

Or for the more involved checks (runs the file-size guard, format verification, build,
test suite, and the README screenshot render):

```bash
./visual-relay check
```

# Tech Stack

The code is mostly C# and the UI is built with [Avalonia](https://avaloniaui.net).

# Commands

- `./visual-relay init` - bootstraps a new project with the relay config.
- `./visual-relay` - launches the app.
- `./visual-relay launch` also launches the app.
- `./visual-relay build` - builds the app (the launcher also does this).
- `./visual-relay install-hooks` - installs pre-commit hooks.
- `./visual-relay test` - runs the test suite.
- `./visual-relay check` - runs the file-size guard, format verification, build, the test suite, and the README screenshot render.

## Learn more

- [docs/OPERATIONS.md](docs/OPERATIONS.md) - model backend (LiteLLM proxy lifecycle) and the nono sandbox.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - diagnosing the dev loop and test hangs.
- [AGENTS.md](AGENTS.md) - contributing, the control API, and dev-only tooling.
- [docs/DESIGN.md](docs/DESIGN.md) for the full architecture and the 11-stage mapping.
