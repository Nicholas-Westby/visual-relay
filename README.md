# Visual Relay

Visual Relay is a cross-platform desktop app that helps you build software by processing tasks with
LLMs using a relay-like pipeline, with each stage passing its output to the following stage.
You create markdown files as specs, and Visual Relay implements them via the pipeline.

- Mistakes are avoided by enforcing a strict set of steps the LLM can't bypass (e.g., red/green TDD).
- Costs are optimized by choosing an appropriate LLM model tier per stage (and by enforcing a budget).
- It also gives you easy ways to observe each step via the activity panel.
- All LLM interactions are sandboxed ([nono](https://nono.sh/) on macOS, Linux and Windows (inside WSL2)) to avoid destructive file system changes.

![Visual Relay main window](docs/images/visual-relay-main.png)

# Install (macOS)

<!-- BEGIN install section (self-contained; sibling tasks may shorten the README) -->

The recommended way to run Visual Relay is to **clone the repo and launch it with the
`./visual-relay` wrapper** (one command bootstraps everything else):

```bash
cd ~/repositories # or wherever you keep your repos
git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git
cd visual-relay
./visual-relay launch
```

`--depth 1` does a shallow clone (latest commit only) for a faster, smaller
download; omit it to fetch the full history.

`./visual-relay` is a tiny launcher that provisions its own toolchain via
[Nix](https://nixos.org) (this avoids global installs).

You can then run `./visual-relay` in that folder the next time you want to launch it.

# Install (Windows)

Visual Relay runs on Windows, but the sandbox every command runs in is Linux: `nono`
enforcing the same profile through the WSL2 kernel, exactly as on macOS and Linux. So
there are two halves to install — Visual Relay on Windows, and the sandbox inside a
WSL2 distro.

1. Install WSL2 and a distro in an elevated PowerShell, then reboot. On a PC that has
   never had WSL, this first run installs WSL itself but not the distro, so check with
   `wsl -l -v` after the reboot and run the install again if no distro is listed (no
   elevation needed). Then open the distro once so it creates your Linux user:

   ```powershell
   wsl --install -d Ubuntu
   wsl -d Ubuntu
   ```

2. Install git and nono 0.75.0 **inside the distro** (not on Windows). Visual Relay pins
   that nono version, so name it; the installer otherwise takes the latest release:

   ```bash
   sudo apt update && sudo apt install -y git curl
   curl -fsSL https://nono.sh/install.sh | NONO_VERSION=v0.75.0 sh
   ```

   The `.deb` from the [v0.75.0 release](https://github.com/nolabs-ai/nono/releases/tag/v0.75.0)
   works too; install it with `sudo apt install --no-install-recommends ./nono-cli_0.75.0_amd64.deb`,
   because without that flag apt also pulls in gnome-keyring and about 80 MB of desktop
   packages. Set `VR_WSL_DISTRO=<name>` if the distro you want is not the WSL default.

3. Install the .NET 10 SDK on Windows. (Run from a terminal, the launcher offers to
   install a per-user copy for you instead.)

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```

4. Clone the repo and run it through `visual-relay.cmd` (nix doesn't run on Windows, so
   dependencies are installed globally). Typing `./visual-relay` in PowerShell runs
   `visual-relay.ps1`, which PowerShell's default execution policy refuses; the `.cmd`
   passes the policy flag for you:

   ```powershell
   cd ~/repositories # or wherever you keep your repos
   git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git
   cd visual-relay
   .\visual-relay.cmd launch
   ```

`--depth 1` does a shallow clone (latest commit only) for a faster, smaller
download; omit it to fetch the full history.

You can then run `.\visual-relay.cmd` in that folder the next time you want to launch it.

Provider keys go in `%APPDATA%\visual-relay\.env`, or use the key panel in the app. When
`HOME` is set, as it is in Git Bash, Visual Relay reads `%USERPROFILE%\.config\visual-relay\.env`
instead.

**Keep the repositories you work on inside the distro** and open them as
`\\wsl.localhost\<distro>\home\<user>\...`. A `C:\` path is refused: the Windows drives
WSL mounts under `/mnt` are roughly an order of magnitude slower for the many small
files a build touches, and their permission model is not the one Landlock was designed
against.

**Your project's build and test commands run inside WSL**, so the toolchain they need
has to be installed in the distro. Per-user installs such as rustup and nvm are found,
because Visual Relay runs commands with the PATH your login shell builds. A Windows-only
toolchain — MSBuild against .NET Framework, Visual Studio build tools, Unity on Windows,
anything that needs an `.exe` — is not supported. If anything is missing, the launch gate
names it and how to fix it; see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

<!-- END install section -->

# What Visual Relay Does

- Runs `llm-tasks/` one task at a time through the staged relay pipeline, writing ledger,
  manifest, seal, event, report, and trace artifacts. These help keep the pipeline honest.
- Presents a command center GUI: select your project folder, queue/archive controls,
  per-stage status, structured run logs, and stage cards that double as log filters.
- Streams model output live into the GUI as assistant text, tool calls, tool
  results, and thinking records, while the stage is still running.
- Reports time and measured token cost per task and per stage.

# Tests

To run the main test suite:

```bash
./visual-relay test
```

Or for the more involved checks (runs the file-size guard, format verification, build,
test suite, and the screenshot determinism check):

```bash
./visual-relay check
```

# Tech Stack

The code is mostly C# and the UI is built with [Avalonia](https://avaloniaui.net).

# Commands

- `./visual-relay` - launches the app.
- `./visual-relay launch` also launches the app.
- `./visual-relay build` - builds the app (the launcher also does this).
- `./visual-relay install-hooks` - installs pre-commit hooks.
- `./visual-relay test` - runs the test suite.
- `./visual-relay test serial` - runs the test suite, one test at a time.
- `./visual-relay check` - runs the file-size guard, format verification, build, the test suite, and the screenshot determinism check.

## Learn more

- [docs/OPERATIONS.md](docs/OPERATIONS.md) - provider keys, model routing, the nono sandbox,
  and the `authorTests` / `testPaths` config keys that decide how test files are gated.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - diagnosing the dev loop and test hangs.
- [AGENTS.md](AGENTS.md) - contributing, the control API, and dev-only tooling.
- [docs/DESIGN.md](docs/DESIGN.md) for the full architecture and the 12-stage mapping.
