# Upgrade nono to 0.66.0

Visual Relay runs every Swival subagent under the `nono` OS-level sandbox (Seatbelt on macOS, Landlock on Linux). The running environment currently resolves nono **0.61.1**, and the upstream project has published **0.66.0** (its latest release) at `https://github.com/nolabs-ai/nono/releases/tag/v0.66.0`. Upgrade the repo's nono dependency and installation references to 0.66.0.

nono reaches a machine through **two independent channels**, and both carry a stale org reference that this task must fix:
- **Nix devshell** (`flake.nix`) — the `visual-relay` launcher re-execs into `nix develop` for source checkouts (`_ensure_devshell`), and the devshell provisions nono. This is the developer path.
- **Homebrew formula** (`packaging/visual-relay.rb`, `depends_on`) — for *published* brew installs, the launcher detects the shipped app payload (`HAS_PUBLISHED`) and **skips the devshell entirely**, so nix never runs; there nono is pulled in as a Homebrew dependency.

That is why the steps below touch both the flake override **and** the brew dependency — they are not redundant; each is the nono source for a different install path. (The `NonoGate.Decide` message is the manual-install fallback when neither channel put nono on PATH.)

The upstream repository has **moved GitHub orgs**: it now lives at `nolabs-ai/nono`. In fact the v0.66.0 release *is* that migration ("migrate GitHub org references from always-further to nolabs-ai"). Visual Relay's references still point at the original `jedisct1/nono` org, which is now doubly stale — retarget them straight to the current `nolabs-ai/nono` (do not route through the interim `always-further`).

**Do NOT change the `nono pull jedisct1/swival` reference** in `NonoGate.Provision`. The org rename deliberately left nono's *registry package namespaces* untouched (to avoid breaking existing installs), and the swival profile pack is a separate concern from the nono binary. That call — and the test that asserts it — are out of scope and must stay `jedisct1/swival`.

## Current state (researched)

- **Nix devShell** ships an unpinned `nono` from nixpkgs-unstable (`flake.nix`). The locked nixpkgs revision currently builds an older nono (0.61.1, predating 0.66.0):
  ```nix
  packages = with pkgs; [
    dotnet-sdk_10
    git
    bash
    icu
    imagemagick
    openssl
    zlib
    nono
    uv
    python313
  ];
  ```
- **Homebrew packaging** points at the stale org tap (`packaging/visual-relay.rb`):
  ```ruby
  depends_on "jedisct1/nono/nono"
  ```
- **CLI install hint** points users to the stale org (`tools/VisualRelay.Cli/Gates/NonoGate.cs`, in the `Decide` method):
  ```csharp
  brew install nono
  (or see https://github.com/jedisct1/nono for other platforms)
  ```
- **Test fixtures** are documented as modeling 0.61.1 output (`tests/VisualRelay.Tests/SandboxPathInspectorInheritedTests.cs`):
  ```csharp
  // ── Fixtures modelled on real nono 0.61.1 output ─────────────────────
  ```
- The `nono pull jedisct1/swival` call in `NonoGate.Provision` pulls the separate **swival profile pack**, not the nono binary. The 0.66.0 org rename intentionally did NOT migrate registry package namespaces, so this reference is correct as-is and is **out of scope**. A test asserts it verbatim (`CliNonoGateTests`, `Assert.Contains("pull jedisct1/swival", ...)`) — leave both unchanged.

## What to build

1. **Pin nono 0.66.0 in the Nix flake.** Since the current nixpkgs-unstable revision still ships 0.61.1, override the `nono` package in `flake.nix` to build 0.66.0 from the new org. Use `pkgs.nono.overrideAttrs` with the new `version`, `src` (`fetchFromGitHub { owner = "nolabs-ai"; repo = "nono"; tag = "v0.66.0"; ... }`), and `cargoHash`. Use `pkgs.lib.fakeSha256` to discover the new hashes, then replace them with the real values.
2. **Update the Homebrew dependency.** In `packaging/visual-relay.rb`, change `depends_on "jedisct1/nono/nono"` to a dependency that resolves nono 0.66.0 from the new source (`nolabs-ai/nono/nono`). If the upstream tap formula is not yet at 0.66.0, update the formula to fetch the v0.66.0 GitHub release artifacts from `https://github.com/nolabs-ai/nono` directly.
3. **Update the CLI install message.** In `tools/VisualRelay.Cli/Gates/NonoGate.cs` (`Decide`), replace `https://github.com/jedisct1/nono` with `https://github.com/nolabs-ai/nono`. Keep `brew install nono` as the macOS/Homebrew path. Do not touch the `nono pull jedisct1/swival` line in `Provision` (see the note above).
4. **Update test fixture documentation.** In `tests/VisualRelay.Tests/SandboxPathInspectorInheritedTests.cs`, update the comment from "real nono 0.61.1 output" to "real nono 0.66.0 output". The org rename was housekeeping-only and did not change CLI output, so the sample payloads (`SampleResolvedShowJson`, `SampleDenyCredentialsGroupJson`, `SampleMacKeychainsGroupJson`, etc.) are expected to still match — but verify against real `nono profile show <profile> --json` / `nono profile groups <name> --json` from 0.66.0 rather than assuming, and update any payload whose shape drifted.
5. **Verify the vr-guard profile still loads.** Run `./visual-relay launch --help` (or the equivalent inside `nix develop`) and confirm the nono gate passes with 0.66.0. Do not change `packaging/nono/vr-guard.json` unless 0.66.0 rejects the current schema.

## Done when

- `nono --version` inside `nix develop` reports `0.66.0`.
- `tools/VisualRelay.Cli/Gates/NonoGate.cs` points users to `https://github.com/nolabs-ai/nono`.
- `packaging/visual-relay.rb` depends on a nono 0.66.0 source from the `nolabs-ai` org.
- `tests/VisualRelay.Tests/SandboxPathInspectorInheritedTests.cs` no longer references 0.61.1, and all unit tests pass.
- The `nono pull jedisct1/swival` reference (and its `CliNonoGateTests` assertion) are unchanged.
- `./visual-relay test` (or `dotnet test`) passes with no new failures.
- The change is committed with a conventional message such as `chore(deps): upgrade nono to 0.66.0`.
