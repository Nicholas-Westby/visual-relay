# visual-relay.ps1 - the Windows pre-dotnet bootstrap (sibling of the bash
# `visual-relay`). Nix has no native Windows story, so instead of entering a nix
# devshell this provisions the same toolchain natively and consent-gated, then
# execs the C# CLI. Like the bash launcher it stays tiny: EVERY subcommand's logic
# lives in C# (tools/VisualRelay.Cli) - new behavior belongs in the CLI, not here.
$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
$cmd = if ($args.Count -ge 1) { $args[0] } else { 'launch' }
# Force an array: a single trailing arg slices to a scalar string, which the call
# below would char-enumerate. @(...) keeps "arg with spaces" one token.
$rest = if ($args.Count -ge 2) { @($args[1..($args.Count - 1)]) } else { @() }

# Telemetry opt-outs + the repo root / caller cwd the CLI reads, matching the bash
# launcher's exports so both platforms hand the CLI the same environment.
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:VISUAL_RELAY_SCRIPT_DIR = $ScriptDir
if (-not $env:ORIGINAL_CWD) { $env:ORIGINAL_CWD = (Get-Location).Path }

# Non-interactive when stdin/stdout is redirected (the analogue of the bash
# `[[ -t 0 && -t 1 ]]` guard): never prompt, print the manual one-liner, and stop.
$interactive = -not ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected)

function Test-DotnetSdk10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    try { $sdks = & dotnet --list-sdks } catch { return $false }
    foreach ($line in $sdks) {
        if ($line -match '^(\d+)\.' -and [int]$Matches[1] -ge 10) { return $true }
    }
    return $false
}

function Install-Dotnet {
    # Honor an install-command override (used by tests, like the bash launcher's
    # VISUAL_RELAY_NIX_INSTALLER); otherwise run Microsoft's official script into
    # a per-user dir and prepend it to PATH for this session only.
    if ($env:VISUAL_RELAY_DOTNET_INSTALLER) { & $env:VISUAL_RELAY_DOTNET_INSTALLER; return }
    $dir = Join-Path $env:LOCALAPPDATA 'visual-relay\dotnet'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $installer = Join-Path $env:TEMP 'vr-dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel 10.0 -InstallDir $dir
    $env:DOTNET_ROOT = $dir
    $env:PATH = "$dir;$env:PATH"
}

# .NET 10 SDK - required to run anything. Detect; if absent, consent-install on a
# TTY, else print the install one-liner and stop (no surprise global installs).
if (-not (Test-DotnetSdk10)) {
    $hint = "  irm https://dot.net/v1/dotnet-install.ps1 | iex    (or: winget install Microsoft.DotNet.SDK.10)"
    if (-not $interactive) {
        [Console]::Error.WriteLine("visual-relay: the .NET 10 SDK was not found. Install it:`n$hint")
        exit 1
    }
    [Console]::Error.Write('visual-relay: the .NET 10 SDK was not found. Install it now? [y/N] ')
    $answer = [Console]::ReadLine()
    if ($answer -match '^\s*(y|yes)\s*$') { Install-Dotnet }
    else { [Console]::Error.WriteLine("visual-relay: install the .NET 10 SDK manually:`n$hint"); exit 1 }
    if (-not (Test-DotnetSdk10)) {
        [Console]::Error.WriteLine('visual-relay: the .NET 10 SDK is still not on PATH after install'); exit 1
    }
}

# uv - a soft want for `launch` (it provisions Python + litellm for the model
# backend, Phase 2); a missing uv only warns and never blocks opening the GUI.
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('visual-relay: uv was not found (needed to run the model backend, not to open the GUI). Install: irm https://astral.sh/uv/install.ps1 | iex    (or: winget install astral-sh.uv)')
}

# git - required; without it print the install one-liner and stop.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    [Console]::Error.WriteLine('visual-relay: git was not found. Install it: winget install Git.Git')
    exit 1
}

$cli = Join-Path $ScriptDir 'tools\VisualRelay.Cli\VisualRelay.Cli.csproj'
# Pass $rest (not @rest): for a native command PowerShell expands an array into
# separate args while keeping each element's spaces intact.
& dotnet run --project $cli -- $cmd $rest
exit $LASTEXITCODE
