# Harness: upgrade the whole toolchain to latest stable (nono, swival, .NET, Avalonia/NuGet, litellm, nix)

Bring every dependency Visual Relay pins or provisions up to its **latest stable** release: the
nix flake inputs, the .NET SDK, every NuGet `PackageReference` (notably **Avalonia 12.0.4**), the
local dotnet tool, litellm/Python, and the runtime-provisioned `nono` (sandbox) and `swival`
(subagent runner). Look up the actual latest versions at implementation time — do **not** trust
any number written here. This touches only VR's **own** toolchain; VR remains a general-purpose
tool that drives arbitrary repos, so nothing here should hard-code VR-specific assumptions into the
engine.

## Pin locations (every surface to touch)

### Nix flake (the single source for dotnet/nono/uv/python on a dev host)
- `flake.nix:5` — sole input `nixpkgs.url = github:NixOS/nixpkgs/nixpkgs-unstable`.
- `flake.nix:20-31` — devShell packages: `dotnet-sdk_10`, `nono`, `uv`, `python313`, plus git/bash/
  icu/imagemagick/openssl/zlib. These float with the locked nixpkgs rev.
- `flake.lock:5-10` — nixpkgs pinned to rev `173d0ad7a974f8543a9ab01d2271b2e290341b33`. Bump with
  `nix flake update` (re-locks nixpkgs, which advances dotnet-sdk_10 / nono / uv / python313 together).

### nono (sandbox) — not version-pinned in-repo
- Provided either by nixpkgs (`flake.nix:28`) or `brew install nono` (`tools/VisualRelay.Cli/Gates/NonoGate.cs:45-47`).
  Observed ~v0.61.1. Upgrade = bump `flake.lock` (above) and/or `brew upgrade nono`.
- Keep the guard profile `packaging/nono/vr-guard.json` valid against the new nono, and re-confirm
  the `--silent` / `--allow-cwd` flags VR passes still exist (built in `ProcessRunners.BuildNonoPrefix`).

### swival (subagent runner) — not version-pinned in-repo; runtime-provisioned
- macOS/Linux install: `brew install swival/tap/swival` (`tools/VisualRelay.Cli/Gates/SwivalGate.cs:12`).
  Windows: `uv tool install swival` (`SwivalGate.cs:24,48`).
- Weekly consent-gated upgrade check: `tools/VisualRelay.Cli/Gates/SwivalUpgradeCheck.cs:15-17`
  (`brew outdated/upgrade swival/tap/swival`). Observed ~v1.0.33. Upgrade = `brew upgrade swival/tap/swival`
  (or `uv tool upgrade swival`). No file edit pins the version; just provision the latest.

### .NET SDK + local dotnet tool
- No `global.json` exists — the SDK floats with nix `dotnet-sdk_10` (`flake.nix:24,35`); all projects
  target `net10.0`. Observed 10.0.30x. Upgrade via the flake bump above; only add a `global.json` if a
  hard pin becomes necessary.
- `.config/dotnet-tools.json:5-7` — `jetbrains.resharper.globaltools` 2026.1.2 (the `jb` inspectcode
  tool used by `./visual-relay inspect`). Bump to latest.

### NuGet packages — pinned per-`.csproj` (there is **no** `Directory.Packages.props`; `Directory.Build.props` only sets analysis/version)
- `src/VisualRelay.App/VisualRelay.App.csproj:18-26` — Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent,
  Avalonia.Fonts.Inter (all **12.0.4**); AvaloniaUI.DiagnosticsSupport 2.2.1; CommunityToolkit.Mvvm 8.4.1.
- `tools/VisualRelay.Screenshots/VisualRelay.Screenshots.csproj:10-11` — Avalonia.Headless 12.0.4,
  Avalonia.Skia 12.0.4.
- `tools/VisualRelay.Guards/VisualRelay.Guards.csproj:8` — Microsoft.CodeAnalysis.CSharp 4.14.0.
- `tests/VisualRelay.Tests/VisualRelay.Tests.csproj:11-17` — coverlet.collector 6.0.4,
  Microsoft.NET.Test.Sdk 17.14.1, Avalonia.Headless 12.0.4, Avalonia.Headless.XUnit 12.0.4,
  xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.4, Microsoft.CodeAnalysis.BannedApiAnalyzers 3.3.4.
- Keep all four Avalonia surfaces (App, Screenshots, Tests.Headless, Headless.XUnit, Skia) on **one**
  matching version. Note `Directory.Build.props:5` sets `TreatWarningsAsErrors=true` and `AnalysisLevel=latest`,
  so a newer SDK/analyzer/Avalonia may surface new warnings that break the build — fix them, don't relax the gate.

### litellm / Python backend — not version-pinned; floats at venv build
- `src/VisualRelay.Core/Execution/BackendVenv.cs:51-52` — `uv venv --python 3.13` then
  `uv pip install --python <venv> litellm[proxy]` (no version constraint → latest at provision time).
  To force the upgrade, delete the cached venv (`$XDG_DATA_HOME/visual-relay/backend-venv`) so uv rebuilds.
- `src/VisualRelay.Core/Execution/BackendPaths.cs:18` — `PinnedPythonVersion = "3.13"` (uvloop crashes on
  3.14+). Only raise this if litellm+uvloop genuinely support the newer Python — empirically verify, don't assume.
- `tools/backend/litellm-config.yaml` is proxy config, not a version pin; leave its model aliases alone.

## Approach — incremental, one logical group per commit
Upgrade in groups, rebuilding and running the suite **under nono** after each risky group; never batch
everything into one commit. Suggested order (cheapest/safest first):
1. **nix inputs** (`nix flake update`) — re-enter the devshell; confirm dotnet/nono/uv/python resolve.
2. **dotnet tool** (`.config/dotnet-tools.json`) — `dotnet tool restore`; `./visual-relay inspect` still runs.
3. **NuGet non-Avalonia** (xunit.v3, Test.Sdk, coverlet, analyzers, CommunityToolkit.Mvvm) — rebuild + suite.
4. **Avalonia** (all five references in lockstep, + DiagnosticsSupport) — see the headless note below.
5. **litellm/Python** — rebuild the venv, boot the proxy, confirm `/health/readiness` and a real stage run.
6. **swival / nono** provisioning — `brew upgrade` (or `uv tool upgrade`); confirm a sandboxed run still enforces.

## Avalonia 12.0.4 → latest — re-test the headless font workaround
`tests/VisualRelay.Tests/HeadlessTestApp.cs:21-35` works around an **Avalonia 12** bug: an unresolved
`FontFamily` under the headless text platform drives the text formatter into an infinite empty-line loop
(`CreateEmptyTextLine`) that hangs the first layout on `Window.Show()`; the fix routes every unresolved
family to the embedded Inter font. A newer Avalonia may FIX this — after the bump, **re-test** whether the
fallback is still required and simplify/remove it **only if** the headless suite stays green without it
(empirically verify; do not delete on faith). This is the same deadlock family the verify/headless tasks
care about.

## Consent-gated / nix specifics
The launcher (`visual-relay:36`) bootstraps the toolchain by entering the nix devshell (or offering a
Determinate-Nix / native install on a TTY); nono/swival/uv installs are all **consent-gated** and must stay
so. flake.lock pins are bumped with `nix flake update`, never by hand-editing the lock hashes.

## Done when
- `./visual-relay check` is green; the GUI launches; the full suite is green **under nono**.
- The sandbox still enforces with the upgraded nono (a denied write/exec is still blocked — no regression);
  consent-gating for nono/swival/nix is unchanged.
- A real swival-backed stage completes against the rebuilt litellm proxy.
- The Avalonia headless font workaround has been re-tested (kept-with-reason or removed-and-still-green).
- One Conventional Commit per logical upgrade group; no VR-repo specifics leak into the engine.
