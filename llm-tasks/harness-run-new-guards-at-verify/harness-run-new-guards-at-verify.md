# Harness: execute newly-added guard scripts on the host before accepting Verify

On task `10-adopt-inspectcode-standards-repo-wide` (2026-06-13), the task's deliverable
was a new guard script (`tools/guards/inspect-code.sh`) wired into `./visual-relay check`.
Stage-9 Verify ran `config.guardCmd` (the value in `.relay/config.json`) — which did not
yet include the new guard because the task itself was adding it. The sandboxed stage agents
cannot self-verify end-to-end (nono blocks tool-restore and network). Result: the new gate
was committed unverified and shipped 448 InspectCode findings into `check`.

## Current state (researched)

### Stage-9 guard execution path today

`src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs:106–135`, `IntegrateGuardAsync`:
runs `config.GuardCommand` if non-null. This is the value of `"guardCmd"` in
`.relay/config.json` — it is static per-repo config set before the run starts. A task that
adds a NEW guard script cannot update this config from inside the sandbox, and even if it
could, the updated value would not be re-read mid-run.

### Unsandboxed runner precedent

The driver already calls `_dependencies.TestRunner.RunAsync` (unsandboxed, on the host) for
bootstrap checks (`RelayDriver.cs:196`) and for the main test command (`RelayDriver.cs:219`).
`IntegrateGuardAsync` also calls `_dependencies.TestRunner` (line 117). The host-side
`ITestRunner` is fully available at stage 9 — no sandbox boundary to cross.

### Detecting newly-added or modified guard scripts

At stage 9, the working tree has been modified by stages 5–8. The `manifest` list (in-memory,
`RelayDriver.cs:39`) enumerates every file the task touched. A configurable detection rule
can match manifest entries against a glob or directory pattern.

The simplest general default: any manifest entry whose path matches `tools/guards/**` or
ends in `.sh` (or is otherwise declared in a new `NewGuardPatterns` config list) is a
candidate. The exact pattern must be configurable — different repos put guards in different
places.

### Overlap with DONE tasks

`DONE-verify-enforce-repo-guards.md` established `config.guardCmd` for EXISTING guards.
This task extends the Verify stage to also probe NEWLY-ADDED guards found in the manifest.
The two are complementary: existing guards run via `IntegrateGuardAsync`; new guards run
via the new `NewGuardProbeAsync` step described below.

## What to build

Write the failing tests first. All changes are in the VR harness.

### 1. Add `NewGuardPatterns` to `RelayConfig` and loader

Add to `src/VisualRelay.Domain/RelayConfig.cs`:

```csharp
// Glob patterns (relative to targetRoot) that identify guard/gate scripts.
// When a manifest entry matches any pattern, the harness executes it once
// unsandboxed after Fix (stage 8), before accepting Verify.  A non-zero exit
// feeds output into the Fix-verify loop instead of letting an unverified guard
// through.  Default: ["tools/guards/**/*.sh"] — repos that store guards
// elsewhere should override.  Set to [] to disable.
IReadOnlyList<string> NewGuardPatterns = ["tools/guards/**/*.sh"],
```

Add to `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`: read optional JSON
array `"newGuardPatterns"` with the above default when absent. Use the existing
`OptionalStringArray` helper (add a default-value overload or provide the default inline).

### 2. Add `NewGuardProbeAsync` to `RelayDriver.RepoGuards.cs`

```csharp
/// <summary>
/// Detects manifest entries that match the configured new-guard patterns, runs
/// each once unsandboxed on the host, and returns a combined failure output
/// string (non-null) when any guard exits non-zero, or null when all pass.
/// </summary>
private async Task<string?> NewGuardProbeAsync(
    string rootPath,
    IReadOnlyList<string> manifest,
    IReadOnlyList<string> patterns,
    CancellationToken ct)
{
    if (patterns.Count == 0)
        return null;

    var candidates = manifest
        .Where(entry => patterns.Any(p => Glob.IsMatch(entry, p)))
        .Select(entry => Path.Combine(rootPath, entry))
        .Where(File.Exists)
        .ToList();

    if (candidates.Count == 0)
        return null;

    var failures = new List<string>();
    foreach (var scriptPath in candidates)
    {
        var result = await _dependencies.TestRunner.RunAsync(rootPath, scriptPath, ct);
        if (result.TimedOut || result.ExitCode != 0)
        {
            failures.Add($"--- New guard failed: {scriptPath} (exit {result.ExitCode}) ---\n{result.Output}");
        }
    }

    return failures.Count > 0 ? string.Join("\n\n", failures) : null;
}
```

Use `Microsoft.Extensions.FileSystemGlobbing` (already a transitive dependency in .NET
projects) or a simple suffix/prefix match for the default `tools/guards/**/*.sh` case.
Keep the implementation general — the pattern set is caller-provided.

### 3. Wire `NewGuardProbeAsync` into stage 9 of `RelayDriver.cs`

In the stage-9 block (`RelayDriver.cs:188–277`), after the existing bootstrap check and
before `IntegrateGuardAsync`, call `NewGuardProbeAsync`:

```csharp
// New-guard probe: run any guard scripts the task itself added to the manifest.
var newGuardOutput = await NewGuardProbeAsync(
    rootPath, manifest, config.NewGuardPatterns, cancellationToken);
if (newGuardOutput is not null)
{
    // Treat the same as a failing guard: fold into check and fix-verify input.
    bootstrapFailed = true; // reuse the "something non-test failed" flag
    bootstrapFailureOutput = "--- New guard probe ---\n" + newGuardOutput;
}
```

Alternatively, introduce a dedicated `newGuardFailed` / `newGuardOutput` pair parallel to
`bootstrapFailed` / `bootstrapFailureOutput`. The key requirement is that a non-zero exit
from a new guard reaches the Fix-verify loop with real output, not a synthetic pass.

Run `NewGuardProbeAsync` BEFORE `IntegrateGuardAsync`. If the new guard is what
`config.GuardCommand` will later run, running it first catches the failure with better
context (the exact failing script's output) before the composite guard also fails.

### 4. Design note: harness-probe vs sandbox grant (trade-off)

The alternative to this task would be granting the nono sandbox additional write
permissions so agents can self-run tool-restore and execute the new guard during stages
5–8. That approach is harder to contain (tool-restore can pull arbitrary network packages),
profile-specific (VR's nono profile is per-repo), and requires sandbox changes for every
new class of tool. The harness-probe is a single general mechanism that runs at the
harness level (already unsandboxed), requires no profile changes, and gives the operator
real failure output in the Fix-verify loop.

### 5. Note on `config.guardCmd` coverage (related gap)

Tasks that add a new guard to `./visual-relay check` should also update `config.guardCmd`
so future runs benefit. The new-guard probe does NOT make this update automatic — it is a
verification-time probe, not a config writer. If the new guard passes the probe but
`config.guardCmd` is not updated, future tasks won't run it. Consider surfacing a
ledger warning when a newly-detected guard is not present in `config.guardCmd`, prompting
the operator to update their config manually. This warning is advisory and does not block
the run.

### 6. Tests (TDD — write first)

All in `tests/VisualRelay.Tests/`.

**`RelayDriverNewGuardProbeTests.cs`** (driver integration via `CapturingTestRunner`):

- `Stage9_NoMatchingGuardsInManifest_VerifyProceeds` — manifest has no `tools/guards/`
  entries → `NewGuardProbeAsync` returns null, stage 9 continues normally.
- `Stage9_NewGuardPassesProbe_VerifySucceeds` — manifest includes `tools/guards/new.sh`,
  test runner returns exit 0 → stage 9 passes.
- `Stage9_NewGuardFailsProbe_EntersFixVerifyLoop` — manifest includes `tools/guards/new.sh`,
  test runner returns exit 1 with output → stage 9 is red, fix-verify loop receives the
  guard output.
- `Stage9_EmptyNewGuardPatterns_NeverProbes` — `config.NewGuardPatterns = []` → runner
  receives zero calls for guard scripts, stage proceeds.
- `Stage9_NewGuardTimesOut_Flags` — test runner returns `TimedOut = true` → stage is
  flagged (not silently passed).

**`RelayConfigLoaderNewGuardPatternsTests.cs`**:

- Default when field absent: `["tools/guards/**/*.sh"]`.
- Parses `"newGuardPatterns": ["custom/guards/*.sh"]` from JSON.
- Empty array `"newGuardPatterns": []` → disabled (no probes).

## Done when

- **New guard failures surface before Verify accepts:** a task that adds a failing guard
  script under `tools/guards/` causes stage 9 to be red; the fix-verify loop receives the
  guard's actual output. Asserted by `RelayDriverNewGuardProbeTests`.
- **Configurable pattern:** `newGuardPatterns` in `.relay/config.json` controls which
  manifest entries are probed; default is `["tools/guards/**/*.sh"]`; empty disables.
  Asserted by `RelayConfigLoaderNewGuardPatternsTests`.
- **No overhead when no new guards:** stage 9 runs identically to today when no manifest
  entry matches the configured patterns.
- **Real output in fix-verify:** the failing guard's stdout/stderr (not a synthetic error
  string) is what the stage-10 agent receives.
- **`./visual-relay check` green** after all changes.
- **Files under 300 lines each:**
  - `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs` (extended by <40 lines)
  - `RelayDriver.cs` stage-9 block extended by <15 lines
  - `src/VisualRelay.Domain/RelayConfig.cs` (+2 lines)
  - `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (+3 lines)
  - `tests/VisualRelay.Tests/RelayDriverNewGuardProbeTests.cs` (new, <150 lines)
  - `tests/VisualRelay.Tests/RelayConfigLoaderNewGuardPatternsTests.cs` (new, <60 lines)
- **Conventional Commit subject candidates:**
  - `feat(driver): probe newly-added guard scripts unsandboxed before accepting Verify`
  - `feat(harness): run task-added guards at stage-9 gate to catch unverified new checks`
