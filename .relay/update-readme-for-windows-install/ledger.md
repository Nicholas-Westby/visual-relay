## Stage 1 - Ideate

{
  "summary": "Update the README's Windows installation section, which is currently a placeholder that ignores all Windows-specific prerequisites (Git, .NET 10 SDK, uv), sandboxing (MXC/provision-mxc), state locations, and PowerShell execution-policy caveats. Replace it with accurate, actionable guidance reflecting the real visual-relay.ps1 bootstrap flow and the Windows sandbox setup.",
  "options": [
    "Option A — Comprehensive step-by-step walkthrough covering prerequisites (Git, .NET 10 SDK, uv) with install commands, clone+launch flow, MXC sandbox setup, state locations, execution-policy tip, and cross-references to TROUBLESHOOTING.md.",
    "Option B — Concise paragraph explaining what the PowerShell launcher auto-provisions and what remains manual, keeping the section high-level and deferring all detail to TROUBLESHOOTING.md and AGENTS.md.",
    "Option C — Structured prerequisites table with one-liner install commands and a 'handled by launcher?' column, followed by the clone-and-run flow and a state-location summary, for scannability."
  ]
}

## Stage 2 - Research

{
  "findings": "The README's Windows install section (lines 33–43) is a stub that says \"nix doesn't run on Windows, so dependencies are installed globally\" and shows only `git clone …; cd visual-relay; .\\visual-relay launch` — it omits every Windows-specific prereq, sandbox step, and caveat. The real bootstrap is `visual-relay.ps1` (37 logic lines + comments), which detects and consent-installs the .NET 10 SDK (via Microsoft's dotnet-install.ps1 into %LOCALAPPDATA%\\visual-relay\\dotnet), warns about missing uv (soft want), requires git (hard), and injects Git's bundled Unix tools + uv's tool bin onto PATH before `dotnet run`-ing the C# CLI. A `visual-relay.cmd` shim (thin .bat) passes `-ExecutionPolicy Bypass`, so `visual-relay` works as a single cross-platform stem (the gradlew pattern). Windows sandboxing uses MXC (Microsoft Execution Containers): `visual-relay provision-mxc` downloads the pinned, Microsoft-signed wxc-exec v0.7.0-rc1 into %LOCALAPPDATA%\\visual-relay\\mxc\\. Three modes exist: Mxc (default when available), Builtin (swival's degraded `--sandbox builtin`, explicit opt-in via VR_WINDOWS_SANDBOX=builtin), and Blocked (no sandbox — execution refused). State locations on Windows: %APPDATA%\\visual-relay\\ (UI state, .env, sandbox policy), %LOCALAPPDATA%\\visual-relay\\ (LiteLLM venv, scratch, pidfile, log), %LOCALAPPDATA%\\visual-relay\\dotnet\\ (launcher-provisioned SDK), %LOCALAPPDATA%\\visual-relay\\mxc\\ (wxc-exec). TROUBLESHOOTING.md already has a thorough Windows section (execution policy, dotnet PATH, MXC, git hooks) that the README should cross-reference. The macOS install section (lines 14–31) is detailed (clone path, Nix provisioning, the `./visual-relay launch` flow) — the Windows section should achieve comparable depth. The install section is self-contained within HTML comments and may be shortened by sibling tasks.",
  "constraints": [
    "The install section is delimited by <!-- BEGIN install section --> / <!-- END install section --> HTML comments (self-contained; sibling tasks may shorten the README)",
    "The macOS install section (lines 14–31) serves as the tone/depth baseline for the Windows section — they should feel like peer platforms",
    "visual-relay.ps1 must stay ≤20 logic lines (enforced by ShellSizeGuard); new behavior belongs in C# (tools/VisualRelay.Cli), not in the launcher",
    "The repo uses the single-stem `visual-relay` entry point on all platforms (visual-relay / visual-relay.cmd / visual-relay.ps1); the README should reflect this unified naming",
    "TROUBLESHOOTING.md already has detailed Windows guidance (lines 66–102) — the README install section should be self-sufficient for getting started but cross-reference TROUBLESHOOTING.md rather than duplicate its depth",
    "Conventional Commits with the hook-enforced ruleset (72-char subject, lowercase after prefix, no trailing period, no em dashes, ≤3 hyphen-bullet body, each ≤20 words)",
    "README.md is a source file under 300 lines (currently ~90); the expanded Windows section must stay within that budget"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The README.md Windows install section (lines 33–43) is a stub that makes two false claims and omits every Windows-specific prerequisite, sandbox step, and caveat. Claim 1: \"nix doesn't run on Windows, so dependencies are installed globally\" — but visual-relay.ps1 provisions the .NET 10 SDK into %LOCALAPPDATA%\\visual-relay\\dotnet\\ (a per-user, non-global location) and sets PATH for that session only (visual-relay.ps1:41-47). Claim 2: \".\\visual-relay launch\" as the sole bootstrap — but visual-relay is actually a three-piece chain: visual-relay.cmd (the .bat shim that passes -ExecutionPolicy Bypass) → visual-relay.ps1 (detects/consent-installs .NET 10 SDK, warns about missing uv, requires git, injects Git's usr/bin + uv's tool bin onto PATH) → dotnet run on tools/VisualRelay.Cli. Omitted entirely: (a) Git is a hard prerequisite — visual-relay.ps1:74-78 exits with 'git was not found'; (b) .NET 10 SDK detection and consent-gated auto-install via Microsoft's dotnet-install.ps1 (visual-relay.ps1:27-65); (c) uv as a soft want for the LiteLLM backend (visual-relay.ps1:67-71); (d) the MXC sandbox — Windows uses Microsoft Execution Containers (wxc-exec v0.7.0-rc1, MxcInstaller.cs:18) with three modes: Mxc (default when available), Builtin (VR_WINDOWS_SANDBOX=builtin opt-in), Blocked (no sandbox — execution refused, WindowsSandbox.cs:4-15); users must run 'visual-relay provision-mxc' post-clone; (e) Windows state locations: %APPDATA%\\visual-relay\\ (UI state, .env, sandbox policy — XdgConfig.cs:14-22), %LOCALAPPDATA%\\visual-relay\\ (LiteLLM venv, scratch, pidfile, log — BackendPaths.cs:76-81), %LOCALAPPDATA%\\visual-relay\\dotnet\\ (launcher-provisioned SDK), %LOCALAPPDATA%\\visual-relay\\mxc\\ (wxc-exec — MxcProvisioner.cs:14-17); (f) PowerShell execution-policy caveat — visual-relay.cmd passes -ExecutionPolicy Bypass (line 7), but running the .ps1 directly requires explicit bypass; (g) cross-reference to TROUBLESHOOTING.md which already has a thorough Windows section (lines 66–102) covering all of the above. The macOS install section (lines 14–31) is detailed — clone path, Nix provisioning explanation, the ./visual-relay launch flow — and the Windows section should be a comparable peer, not a misleading stub.",
  "excerpts": [
    "README.md:33-43 — \"nix doesn't run on Windows, so dependencies are installed globally\" is false; shows only `git clone …; .\\visual-relay launch` with no prereqs, sandbox, or caveats.",
    "visual-relay.ps1:41-47 — Provisions .NET SDK into %LOCALAPPDATA%\\visual-relay\\dotnet\\ (per-user, not global); PATH set for session only.",
    "visual-relay.ps1:74-78 — Git is a hard prerequisite; exits with 'git was not found' if missing.",
    "visual-relay.ps1:27-65 — .NET 10 SDK detection with consent-gated auto-install via dotnet-install.ps1.",
    "visual-relay.ps1:67-71 — uv is a soft want; warns if missing but never blocks.",
    "visual-relay.cmd:7 — The .cmd shim passes -ExecutionPolicy Bypass; this is how `visual-relay` works on Windows.",
    "WindowsSandbox.cs:4-15 — Three sandbox modes: Mxc (default when available), Builtin (explicit opt-in), Blocked (execution refused — no silent unsandboxed mode).",
    "MxcInstaller.cs:18 — Pinned wxc-exec v0.7.0-rc1 from microsoft/mxc.",
    "MxcProvisioner.cs:14-17 — wxc-exec cached at %LOCALAPPDATA%\\visual-relay\\mxc\\wxc-exec.exe.",
    "XdgConfig.cs:14-22 — Windows config dir falls back to %APPDATA% (for UI state, .env, sandbox policy).",
    "BackendPaths.cs:76-81 — Windows data dir falls back to %LOCALAPPDATA%\\visual-relay\\ (LiteLLM venv, scratch).",
    "TROUBLESHOOTING.md:66-102 — Already has comprehensive Windows section (state locations, execution policy, MXC provisioning, dotnet PATH, git hooks).",
    "macOS install section README.md:14-31 — Detailed: clone path, Nix provisioning, ./visual-relay launch flow; Windows section should be a comparable peer."
  ],
  "repro": "Compare README.md lines 33-43 (Windows install) against README.md lines 14-31 (macOS install), visual-relay.ps1 (actual bootstrap logic), visual-relay.cmd (shim with -ExecutionPolicy Bypass), WindowsSandbox.cs (three sandbox modes), MxcInstaller.cs (wxc-exec v0.7.0-rc1), MxcProvisioner.cs (wxc-exec cache path), XdgConfig.cs (APPDATA fallback), BackendPaths.cs (LOCALAPPDATA fallback), and TROUBLESHOOTING.md lines 66-102 (existing comprehensive Windows guidance). The Windows section claims global installs, shows no prerequisites, no sandbox setup, no state locations, no execution-policy note, and no TROUBLESHOOTING.md cross-reference — every one of these is covered by the actual source files listed."
}

## Stage 4 - Plan

{
  "plan": "Replace the Windows install stub (README.md lines 33–43) with an accurate section matching the macOS section's depth and tone. The new section: (1) states that the launcher provisions per-user (not globally), (2) lists Git as the one hard prerequisite with `winget install Git.Git`, (3) shows the clone-and-launch flow via `visual-relay.cmd`→`visual-relay.ps1`→`dotnet run`, (4) explains the launcher consent-prompts for .NET 10 SDK and warns about uv, (5) documents the MXC sandbox with `visual-relay provision-mxc`, the three modes (Mxc/Builtin/Blocked), and the `VR_WINDOWS_SANDBOX=builtin` opt-in, (6) notes that the `.cmd` shim handles execution policy automatically, (7) briefly lists state locations (%APPDATA% and %LOCALAPPDATA%), and (8) cross-references TROUBLESHOOTING.md. The install section HTML comment delimiters remain intact. Commit follows Conventional Commits with enforced rules.",
  "manifest": ["README.md"]
}

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/Installer5DocsTests.cs"],
  "rationale": "Added 7 Windows install section tests to the existing Installer5DocsTests.cs. Two tests pass against the current stub (heading exists, git clone present). Five tests correctly fail on the old misleading content: the stub claims global install, omits the PowerShell launcher chain, omits the MXC sandbox/provisioning, omits the TROUBLESHOOTING.md cross-reference, and never lists Git as a prerequisite. The implementation will make all 7 pass."
}

## Stage 6 - Implement

{ "summary": "Replaced the Windows install stub in README.md (lines 33–43) with a comprehensive section matching the macOS section's depth. The new section: uses %USERPROFILE% as the clone-path hint (Windows equivalent of ~), explains the .cmd→.ps1→dotnet run launcher chain, notes per-user provisioning into %LOCALAPPDATA% (replacing the false 'installed globally' claim), lists Git as the hard prerequisite, documents MXC sandbox provisioning and the three sandbox modes, lists state locations, and cross-references TROUBLESHOOTING.md. All 7 Windows install tests and all 2,451 total tests pass." }

## Stage 7 - Review

{ "verdict": "changes", "issues": ["README.md Windows install block: `cd %USERPROFILE%` uses cmd.exe `%VAR%` syntax, which PowerShell does not expand — pasting it into the ```powershell`-fenced block fails with \"Cannot find path '%USERPROFILE%'\". The whole section targets PowerShell (the .ps1 launcher), and the project's own visual-relay.ps1:85 uses `$env:USERPROFILE`. Replace with a PowerShell-valid home reference: `cd ~` (closest parallel to the macOS `cd ~/repositories`, and valid in PowerShell) or `cd $env:USERPROFILE`. No test asserts on this token, so the change is safe."] }

## Stage 8 - Fix

{
  "summary": "Fixed the Windows install code block by replacing cmd.exe `%USERPROFILE%` with PowerShell-valid `~`, matching the macOS section's pattern. All 2,450 tests pass; the single failure (SettingsPanelUiTests.ToggleCommitProofArtifacts_WritesConfig) is a pre-existing flaky UI test unrelated to this change."
}

## Stage 9 - Verify

{
  "summary": "Replaced the Windows install stub (README.md lines 33–43) with an accurate, comprehensive section matching the macOS section's depth. The new section uses `cd ~` (PowerShell home path), explains the .cmd→.ps1→dotnet run launcher chain with per-user provisioning, lists Git as the hard prerequisite with a winget command, documents MXC sandbox provisioning and the three sandbox modes, lists state locations, and cross-references TROUBLESHOOTING.md. All 2,451 tests pass (0 failures).",
  "commitMessages": [
    "docs: replace Windows install stub with accurate per-user guidance",
    "docs: add MXC sandbox provisioning and prerequisite steps for Windows install",
    "docs: bring Windows install section to parity with macOS depth",
    "docs: update Windows install to use PowerShell-valid home path and launcher chain",
    "docs: document Windows launcher chain, sandbox modes, and state locations"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

