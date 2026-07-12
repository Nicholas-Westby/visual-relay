# Replace the target-command env base instead of overlaying it

## Problem

Target-repo commands (testCmd, guardCmd, formatCmd, bootstrap-check, and the swival stage subprocess) run with a **mixed environment**: the user's pre-devshell snapshot is applied as *overrides on top of* Visual Relay's own nix-devshell process environment. Keys that exist only in the devshell survive the merge and leak into the child.

Real incident (2026-07-11, task `show-cost-per-llm-model`): the repo's `flake.nix` devShell exports

```nix
DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";
```

The user's snapshot has no `DOTNET_ROOT`, so the nix value (SDK 10.0.301) leaked into the guard command's environment, while the snapshot's `PATH` resolved the host `dotnet` (SDK 10.0.300). The guard's `dotnet format VisualRelay.slnx --verify-no-changes` then crashed at startup:

```
Unhandled exception: System.IO.FileNotFoundException: Could not load file or assembly
'System.Composition.AttributedModel, Version=10.0.0.9, ...'
```

(10.0.301's analyzers loaded into a 10.0.300 format host). Every verify went red with `guardCheck=red testCheck=green`, byte-identical across all fix-verify attempts, and the run was flagged. Either SDK alone works; only the mix crashes. Confirmed by running `DOTNET_ROOT=<nix sdk root> dotnet format VisualRelay.slnx --verify-no-changes` (crashes) vs the same command without `DOTNET_ROOT` (exit 0).

## Root cause

`BuildTargetCommandEnvironment` in `src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs` returns only an override dictionary:

```csharp
var merged = new Dictionary<string, string>(snapshot);
foreach (var kvp in BuildSandboxEnvironment(config))
    merged[kvp.Key] = kvp.Value;
return merged;
```

`ProcessCapture.RunAsync` (in `src/VisualRelay.Core/Execution/ProcessCapture.cs`) applies that dictionary onto `ProcessStartInfo.EnvironmentVariables`, which **inherits the full current process env** — so any devshell-only key (not present in the snapshot) survives. `StripLeakedNixSdkEnv` only removes `DEVELOPER_DIR`/`SDKROOT`, so it does not cover `DOTNET_ROOT` or any other devshell-only var.

`ProcessCapture.RunAsync` already has the needed mechanism — an `envRemove` parameter that deletes keys from the inherited env **before** the override dictionary is applied:

```csharp
if (envRemove is not null)
{
    foreach (var key in envRemove)
    {
        process.StartInfo.EnvironmentVariables.Remove(key);
    }
}
```

It is simply never used by the target-command call sites.

## Fix

Make the snapshot a true **replacement base**: when a valid snapshot exists, the child environment must be exactly `snapshot ∪ VR overrides` — nothing inherited from the devshell survives unless the snapshot or the overrides contain it.

### 1. Return a removal set alongside the overrides

In `src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs`, change `BuildTargetCommandEnvironment` to return a new record (declare it in the same file):

```csharp
internal sealed record TargetCommandEnvironment(
    IReadOnlyDictionary<string, string> Overrides,
    IReadOnlySet<string> Remove);
```

Semantics:

- **Valid snapshot** (non-null AND contains a `PATH` key — a snapshot without `PATH` is treated as invalid, same as absent):
  - `Overrides` = snapshot entries, then `BuildSandboxEnvironment(config)` entries applied on top (VR overrides win) — same merge as today.
  - `Remove` = every key present in the current process environment that is **not** a key of `Overrides`.
- **No/invalid snapshot**: `Overrides` = `BuildSandboxEnvironment(config)`, `Remove` = empty set. This preserves today's packaged/brew behavior exactly.

For testability, add an optional parameter for the process environment, defaulting to the real one:

```csharp
internal static TargetCommandEnvironment BuildTargetCommandEnvironment(
    RelayConfig config,
    IEnvironmentAccessor? accessor = null,
    IReadOnlyDictionary<string, string>? processEnv = null)
```

When `processEnv` is null, snapshot the real environment via `Environment.GetEnvironmentVariables()`. Do NOT widen `IEnvironmentAccessor` (it stays a single-key getter).

### 2. Thread `Remove` through both call sites

- `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`, in `RunAsync` at `var env = SwivalSubagentRunner.BuildTargetCommandEnvironment(config);` — destructure the record and pass the removal set through `RunWatchedAsync` (in `SandboxedTestRunner.Watched.cs`; add an `envRemove` parameter to it) down to its `ProcessCapture.RunAsync` call's existing `envRemove:` argument.
- `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`, at `var sandboxEnv = BuildTargetCommandEnvironment(_config);` — pass `envRemove:` on the `ProcessCapture.RunAsync(...)` call that currently passes `environment: sandboxEnv`.

Keep `StripLeakedNixSdkEnv` and its call in `ProcessCapture` unchanged — it remains the defense for the no-snapshot fallback path.

### 3. Why this cannot break process launching

Executable filename resolution (`nono`, `swival`) happens in the **parent** process against the parent's PATH before the child env table applies — the child's replaced `PATH` already ships today (snapshot `PATH` overwrites) and stages run fine. Removing devshell-only keys changes only the child's env table, not how the binary is found. Keys that appear in both `Remove` and `Overrides` cannot exist (Remove excludes Overrides keys by construction), and `ProcessCapture` removes before it applies, so ordering is safe.

## Rejected approach — do not do this

Do NOT fix this by growing `LeakedAppleSdkEnvNames` (or any other name-by-name strip list) with `DOTNET_ROOT` and friends. Chasing the devshell's env footprint variable-by-variable is exactly the fragile approach the snapshot design replaced; the snapshot already tells us the complete set of keys the user's environment legitimately has. Do NOT modify the `visual-relay` bootstrap script — the snapshot capture is correct as-is.

## Tests

Update `tests/VisualRelay.Tests/UserEnvSnapshotTests.cs` — existing `BuildTargetCommandEnvironment` tests adapt to the new record shape (preserve every existing assertion's intent; never delete coverage):

- Devshell-only key: `processEnv` contains `DOTNET_ROOT=/nix/store/x`, snapshot does not → `Remove` contains `DOTNET_ROOT`.
- Snapshot key: present in both → not in `Remove`; `Overrides` carries the snapshot value.
- VR override keys (e.g. `PYTHONDONTWRITEBYTECODE`) → in `Overrides`, never in `Remove`, even when absent from the snapshot.
- No snapshot → `Remove` empty, `Overrides` equals `BuildSandboxEnvironment` output.
- Snapshot without `PATH` → treated as invalid (fallback behavior, `Remove` empty).

Add a child-process integration test alongside the existing patterns in `tests/VisualRelay.Tests/SandboxEnvForwardingTests.cs`: set a marker variable in the test process, spawn a child via `ProcessCapture.RunAsync` with `envRemove` containing that marker, and assert the child does not see it (and that a key passed via `environment` still arrives).

## Constraints

- `dotnet build VisualRelay.slnx` must succeed; all existing tests must pass.
- Packaged/brew installs (no snapshot) must behave byte-for-byte as before.
- Do not change `BuildSandboxEnvironment`'s contents, `StripLeakedNixSdkEnv`, or the bootstrap script.
- If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove tests to satisfy it.
