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
WSL2 distro. Visual Relay sets up the second half itself.

1. Install the .NET 10 SDK on Windows. (Run from a terminal, the launcher offers to
   install a per-user copy for you instead.)

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```

2. Clone the repo and run it through `visual-relay.cmd` (nix doesn't run on Windows, so
   dependencies are installed globally). Typing `./visual-relay` in PowerShell runs
   `visual-relay.ps1`, which PowerShell's default execution policy refuses; the `.cmd`
   passes the policy flag for you:

   ```powershell
   cd ~/repositories # or wherever you keep your repos
   git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git
   cd visual-relay
   .\visual-relay.cmd launch
   ```

3. The first launch checks the WSL side and offers to set up whatever is missing:

   - No WSL: it installs WSL itself. Windows asks you to approve that as an
     administrator, and a restart follows; launch again afterwards for the rest.
   - No distro: it installs Ubuntu, with a Linux user named after yours.
   - Then git, and nono 0.75.0, which Visual Relay pins and checks against a SHA-256
     before installing.

   Only the WSL install needs administrator rights; the rest takes about two minutes.
   `.\visual-relay.cmd setup-wsl` runs the same setup on its own. Set `VR_WSL_DISTRO=<name>`
   to use a distro other than the WSL default; setup installs Ubuntu under that name if it
   does not exist yet. To install WSL yourself instead, run `wsl --install -d Ubuntu` in an
   elevated PowerShell and reboot.

`--depth 1` does a shallow clone (latest commit only) for a faster, smaller
download; omit it to fetch the full history.

`./visual-relay` is a tiny launcher that provisions its own toolchain via
[Nix](https://nixos.org) (this avoids global installs).

You can then run `./visual-relay` in that folder the next time you want to launch it.

# Install (Windows)

Visual Relay runs on Windows, but the sandbox every command runs in is Linux: `nono`
enforcing the same profile through the WSL2 kernel, exactly as on macOS and Linux. So
there are two halves to install — Visual Relay on Windows, and the sandbox inside a
WSL2 distro. Visual Relay sets up the distro half itself; only WSL needs you.

1. Install WSL in an elevated PowerShell, then reboot. This is the one step that needs
   administrator rights:

   ```powershell
   wsl --install -d Ubuntu
   ```

   If Ubuntu opens after the reboot and asks for a user name, create one. If no distro
   appears (a first install can bring WSL without one), that is fine: step 3 sets one up.

2. Install the .NET 10 SDK on Windows. (Run from a terminal, the launcher offers to
   install a per-user copy for you instead.)

   ```powershell
   winget install Microsoft.DotNet.SDK.10
   ```

3. Clone the repo and run it through `visual-relay.cmd` (nix doesn't run on Windows, so
   dependencies are installed globally). Typing `./visual-relay` in PowerShell runs
   `visual-relay.ps1`, which PowerShell's default execution policy refuses; the `.cmd`
   passes the policy flag for you:

   ```powershell
   cd ~/repositories # or wherever you keep your repos
   git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git
   cd visual-relay
   .\visual-relay.cmd launch
   ```

   The first launch checks the WSL side and offers to set up whatever is missing: Ubuntu
   with a Linux user named after yours if there is no distro, then git, and nono 0.75.0,
   which Visual Relay pins and checks against a SHA-256 before installing. None of it needs
   administrator rights, and it takes about two minutes. `.\visual-relay.cmd setup-wsl`
   runs the same setup on its own. Set `VR_WSL_DISTRO=<name>` to use a distro other than
   the WSL default; setup installs Ubuntu under that name if it does not exist yet.

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
