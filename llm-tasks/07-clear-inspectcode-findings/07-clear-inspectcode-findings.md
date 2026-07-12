# Clear the 61 InspectCode findings so `./visual-relay check` passes again

## Problem

`./visual-relay check` is currently red at the inspect-code step:

```
inspect-code: 61 finding(s) at or above SUGGESTION floor.
SARIF: ~/.cache/visual-relay/inspectcode/inspectcode.sarif.json
```

The gate (`tools/VisualRelay.Cli/Gates/InspectCodeGate.cs`, method `Gate`) passes only
on **zero** SARIF results — there is no threshold to duck under. Every finding must be
either fixed in code or suppressed in `.editorconfig` with a documented rationale, per
the gate's own instruction: "Fix real defects in code; only suppress via .editorconfig
with a documented rationale. Never carve out a real defect."

To regenerate the finding list at any time:

```bash
./visual-relay check   # re-runs InspectCode, rewrites the SARIF
jq -r '.runs[0].results[] | [.ruleId, .locations[0].physicalLocation.artifactLocation.uri, (.locations[0].physicalLocation.region.startLine|tostring), .message.text] | @tsv' \
  ~/.cache/visual-relay/inspectcode/inspectcode.sarif.json | sort
```

This spec resolves all 61 findings. Each fix below is anchored by file + symbol +
snippet (SARIF line numbers drift; the snippets are the ground truth). Apply exactly
these directions — do not substitute alternatives.

## Ground rules

- **Two test files sit exactly at the 300-line `FileSizeGuard` limit**:
  `tests/VisualRelay.Tests/ControlServerTests.cs` (300) and
  `tests/VisualRelay.Tests/TaskRowViewModelTests.cs` (300). The fixes below are
  net-zero or net-negative in those files. Do not add net lines to them.
- **Empty-catch findings are silenced by a rationale comment inside the catch body** —
  this is the repo's established pattern. Proof: in the same file
  (`RelayDriver.VerifyWorktreeRecursive.cs`) the per-entry catch containing
  `// Per-entry IO error — skip it, never abort the walk.` is NOT flagged, while the
  three comment-less `catch { }` blocks are.
- New `.editorconfig` suppressions go in a new section appended at the end of the file,
  following the existing comment-block style (see the "Durable carve-outs" section
  already in `.editorconfig` for the voice to match).

---

## 1. Real defect: cross-partial static initializer ordering (2 findings, one `error`-level)

**Findings:** `CSharpWarnings::CS8604` (error) + `StaticMemberInitializerReferesToMemberBelow`
in `src/VisualRelay.Core/Configuration/BackendConfigGenerator.TierResolution.cs`.

The property initializer

```csharp
public static IReadOnlyDictionary<string, string> DefaultTierResolution { get; } =
    Chains.ToDictionary(
```

runs `Chains.ToDictionary(...)` at type-initialization time, but `Chains` is a
`static readonly` **field** whose initializer lives in a *different partial file*
(`BackendConfigGenerator.cs`). C# does not define initializer order across partial
files — it follows compiler input file order. If that order ever flips (file rename,
SDK change), `Chains` is null when this runs and the type initializer throws. That is
what both findings point at.

**Fix — make the property lazily initialized on first access** (first access always
happens after type initialization completes, so `Chains` is guaranteed non-null):

```csharp
private static IReadOnlyDictionary<string, string>? _defaultTierResolution;

/// <summary>Default tier-alias → concrete model resolution (head of each
/// chain; the "fallback" pseudo-model maps to the HF floor). Used to price
/// reports whose recorded model is a tier alias. Lazily built on first access:
/// a field initializer here would read <see cref="Chains"/>, whose own
/// initializer lives in another partial file, and cross-file static
/// initializer order is unspecified. The benign data race (two threads may
/// build identical dictionaries) is acceptable.</summary>
public static IReadOnlyDictionary<string, string> DefaultTierResolution =>
    _defaultTierResolution ??= Chains.ToDictionary(
        kv => kv.Key,
        kv => kv.Value[0].Model == FallbackTier ? FallbackFloorModel : kv.Value[0].Model,
        StringComparer.Ordinal);
```

Do NOT instead move the property into `BackendConfigGenerator.cs` (that file is at 293
lines, nearly at the 300 guard) and do NOT add a static constructor (changes
`beforefieldinit` semantics for the whole class). The existing behavior pins in
`BackendConfigGeneratorAliasConsistencyTests` must stay green.

## 2. Float equality in the cost panel (1 finding)

**Finding:** `CompareOfFloatsByEqualityOperator` in
`src/VisualRelay.App/ViewModels/MainWindowViewModel.CostPerModel.cs`, method
`FormatRateRelativeToInput`, on `effective == input`.

The comparison decides whether to append `" (same as input)"`. Make the displayed
strings the comparison source — that is the honest display semantic (the label claims
"same as input" exactly when the two rendered rates are identical) and removes the
float `==` entirely:

```csharp
private static string FormatRateRelativeToInput(double effective, double input)
{
    var formatted = FormatRate(effective);
    return formatted == FormatRate(input)
        ? formatted + " (same as input)"
        : formatted;
}
```

Behavior is unchanged for every current pricing entry (the "(same as input)" cases —
e.g. deepseek cache-write 0.14 vs input 0.14 — compare equal both ways). Existing
`CostPerModelTests` assertions must stay green unmodified. Do NOT use an epsilon
comparison and do NOT suppress the rule.

## 3. Paired findings in `RelayDriver.cs`: redundant qualifier + "redundant" using (2 findings)

**Findings:** `RedundantNameQualifier` on the call
`Init.RelayGitignoreWriter.EnsureWritten(rootPath);` inside `ExecuteAsync`, and
`RedundantUsingDirective` on `using VisualRelay.Core.Init;` at the top of
`src/VisualRelay.Core/Execution/RelayDriver.cs`.

These are one problem: the `Init.` prefix resolves through the parent namespace, which
makes the `using` unused; the `using` makes the prefix redundant. **Fix both by
dropping the qualifier and KEEPING the using:**

```csharp
RelayGitignoreWriter.EnsureWritten(rootPath);
```

Do NOT remove both the qualifier and the using — that breaks the build. Do NOT remove
only the using (leaves the qualifier finding live).

## 4. Empty general catch clauses (4 findings)

Add a rationale comment inside each empty catch body (see Ground rules for why this
silences the inspection). Behavior unchanged.

`src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktreeRecursive.cs`, method
`OverlayIgnoredDirRecursive` — three sites, each `try { Directory.CreateSymbolicLink(dstDir, srcDir); } catch { }`:

1. Under `if (depth > MaxOverlayRecursionDepth)` →
   `catch { /* best-effort fallback symlink — the skip advisory below still fires */ }`
2. Under `if (depth > 0 && copiedBytes >= thresholdBytes)` →
   `catch { /* best-effort fallback symlink — the skip advisory below still fires */ }`
3. Under `if (depth > 0 && NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold(srcDir, thresholdBytes))` →
   `catch { /* best-effort share of a large child — skipping it never aborts the walk */ }`

`tests/VisualRelay.Tests/UserEnvSnapshotTests.cs`, method `Dispose`:

```csharp
foreach (var f in _tempFiles) { try { File.Delete(f); } catch { /* best-effort temp-file cleanup */ } }
```

## 5. `TryPersistVerifyChecksJson` shape (2 findings)

**Findings:** `MemberCanBePrivate.Global` + `UnusedMethodReturnValue.Global` on
`internal static string? TryPersistVerifyChecksJson(...)` in
`src/VisualRelay.Core/Execution/RelayDriver.VerifyObservability.cs`.

Nothing outside this file calls it (verified repo-wide) and its sole caller — the
`if (setupChecks is not null)` statement inside `PublishVerifyResultAsync` — discards
the return. Make it `private static void`, drop the `return path;` / `return null;`,
and put a comment in the catch so it doesn't become a new empty-catch finding:

```csharp
/// <summary>
/// Writes the structured per-check breakdown to
/// <c>stage{N}-attempt{M}.verify-checks.json</c> under the task directory.
/// Best-effort mirror of <see cref="TryPersistVerifyOutput"/>: the path is not
/// consumed by anything (unlike the verify-output path, which rides the
/// verify_result event), so a persistence failure is swallowed, never failing
/// the verify.
/// </summary>
private static void TryPersistVerifyChecksJson(
    string taskDirectory, int stageNum, int attempt, SetupCheckResults setupChecks)
{
    try
    {
        var path = Path.GetFullPath(
            Path.Combine(taskDirectory, $"stage{stageNum}-attempt{attempt}.verify-checks.json"));
        var json = JsonSerializer.Serialize(setupChecks,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        File.WriteAllText(path, json);
    }
    catch
    {
        // Best-effort artifact — a write failure must not fail the verify.
    }
}
```

The call site needs no change. Do NOT touch `TryPersistVerifyOutput` (its return IS
used).

## 6. Dead locals and redundant assignment (6 findings)

- `src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs` — `UnusedVariable`:
  in the deconstruction `var (_, check, _, reason) = await PublishVerifyResultAsync(`,
  `check` is never read. Change to `var (_, _, _, reason) = ...`.
- `src/VisualRelay.Core/Execution/RelayDriver.ReviewPair.cs` — `RedundantAssignment` +
  `TooWideLocalVariableScope` on `siblingResult` in the `if (reviewResult.Check == "red")`
  block. Replace

  ```csharp
  StageRunResult? siblingResult = null;
  if (visualTask is not null)
  {
      siblingResult = await visualTask;
  ```

  with the declaration inside the `if`:

  ```csharp
  if (visualTask is not null)
  {
      var siblingResult = await visualTask;
  ```

  (the two lines using `siblingResult` stay as they are; nothing after the `if` reads it).
- `tests/VisualRelay.Tests/ControlServerTests.cs` — `UnusedVariable` ×2: in
  `KestrelSmokeTest_BindsOnPort0_AndServesHealth`, `var (vm, window, api) = NewServerDeps();`
  uses only `api` → `var (_, _, api) = NewServerDeps();`. (The objects are still
  constructed and kept alive by `ControlApi`.) Do NOT touch the deconstruction in
  `BindConflict_WithoutExplicitPort_DoesNotThrow_AndIsAvailableIsFalse` — its `vm` is used.
- `tests/VisualRelay.Tests/TaskRowViewModelTests.cs` — `UnusedVariable`: in
  `ProgressFraction_UsesLiveCountWhenRunning`, delete the unused line
  `var d = (double)RelayStages.All.Count;` (that test asserts only literals).
- `tests/VisualRelay.Tests/RelayDriverBaselineVerifyTests.cs` — `UnusedMember.Local`:
  delete `private const string JestNotFound = "sh: line 1: jest: command not found\n";`
  (`MochaNotFound` stays — it is used).

## 7. Integer division feeding a double (1 finding)

**Finding:** `PossibleLossOfFraction` in `tests/VisualRelay.Tests/TaskRowViewModelTests.cs`,
method `ProgressFraction_UsesRelayStagesDenominator`, on
`(RelayStages.All.Count / 2) / d`. The truncation is intentional (stage counts are
ints) — make that explicit by hoisting the int division:

```csharp
var half = RelayStages.All.Count / 2;
Assert.Equal(1.0, new TaskRowViewModel(NewTask(RelayStages.All.Count)).ProgressFraction, precision: 6);
Assert.Equal(half / d, new TaskRowViewModel(NewTask(half)).ProgressFraction, precision: 6);
```

(+1 line here, −1 line from the deletion in section 6 → the file stays at 300.)

## 8. Test-helper parameter naming (4 findings)

**Findings:** `InconsistentNaming` ×4 in `tests/VisualRelay.Tests/ControlServerTests.cs`
on the PascalCase parameters of the private helper `NewTestOptions`. Rename to
camelCase; the `ControlServerOptions(...)` record construction inside keeps ITS
PascalCase named arguments (those are the record's positional parameters):

```csharp
private static ControlServerOptions NewTestOptions(
    int port = 0, string? token = null, bool portWasExplicitlySet = false,
    string? instanceId = null)
{
    return new ControlServerOptions(Enabled: true, Port: port, Token: token,
        PortWasExplicitlySet: portWasExplicitlySet, InstanceId: instanceId,
        Pid: Environment.ProcessId, StartedUtc: DateTime.UtcNow.ToString("o"),
        Version: "0.0-test");
}
```

Then sweep **every** `NewTestOptions(` call site in this file and lowercase the named
arguments (e.g. `NewTestOptions(Port: 0, InstanceId: ...)` → `NewTestOptions(port: 0,
instanceId: ...)`, `NewTestOptions(Port: occupiedPort, PortWasExplicitlySet: true)` →
`NewTestOptions(port: occupiedPort, portWasExplicitlySet: true)`). The helper is
private, so all call sites are in this one file.

## 9. `async` methods without `await` (3 findings)

Each has a fully synchronous body — drop `async Task` for `void` (xunit v3 and
`[AvaloniaFact]` both accept `void` tests):

- `tests/VisualRelay.Tests/ControlServerTests.cs` → `BindConflict_WithExplicitPort_Throws`
  and `BindConflict_WithoutExplicitPort_DoesNotThrow_AndIsAvailableIsFalse`:
  `public async Task ...()` → `public void ...()`.
- `tests/VisualRelay.Tests/SetupCommitHelperTests.cs` →
  `EnsureGitignore_DriverRunPrep_WritesButDoesNotCommit`: same change.

## 10. Closures → method groups (2 findings)

`tests/VisualRelay.Tests/ControlServerTests.cs`, in the two BindConflict tests:

- `Assert.ThrowsAny<Exception>(() => server.Start())` → `Assert.ThrowsAny<Exception>(server.Start)`
- `Record.Exception(() => server.Start())` → `Record.Exception(server.Start)`

## 11. Primary constructor (1 finding)

`tests/VisualRelay.Tests/DelayedSubagentRunner.cs` — convert to a primary constructor
(the prior inspectcode-clearing commit already adopted this style repo-wide). Keep the
XML doc comment; delete the two fields and the explicit constructor:

```csharp
internal sealed class DelayedSubagentRunner(
    ISubagentRunner inner, Dictionary<int, int> stageDelaysMs) : ISubagentRunner
{
    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (stageDelaysMs.TryGetValue(invocation.Stage.Number, out var delayMs))
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), TimeProvider.System, cancellationToken);
        return await inner.RunAsync(invocation, cancellationToken);
    }
}
```

## 12. Merge into pattern (1 finding)

`src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs`, method
`SnapshotProcessEnv`:

```csharp
if (entry.Key is string key && entry.Value is string value)
```

becomes

```csharp
if (entry is { Key: string key, Value: string value })
```

(The `.editorconfig` MergeIntoPattern carve-out covers `tests/**.cs` only, with an
assertion-readability rationale that does not apply here — comply in src code.)

## 13. Collection expressions (2 findings)

`tests/VisualRelay.Tests/TaskRowViewModelTests.cs` — `MarkRunning`'s third parameter is
`HashSet<int>?`, which collection expressions construct fine:

- `RunningStepLabel_SingleStage`: `row.MarkRunning(7, "Review", new HashSet<int> { 7 });`
  → `row.MarkRunning(7, "Review", [7]);`
- `RunningStepLabel_ConcurrentPair`: `row.MarkRunning(8, "Visual-review", new HashSet<int> { 7, 8 });`
  → `row.MarkRunning(8, "Visual-review", [7, 8]);`

## 14. Redundant argument default values (3 findings)

- `tests/VisualRelay.Tests/TaskRowViewModelTests.cs` ×2: `NewTask(0)` → `NewTask()` in
  `ProgressFraction_UsesLiveCountWhenRunning` and
  `RecordStageCompleted_RaisesPropertyChangedForProgressFraction` (the parameter
  defaults to 0; semantics identical).
- `tests/VisualRelay.Tests/CostPerModelTests.cs`, method
  `PopulateModelCostRows_Parameterless_UsesDefaultResolution`: here the explicit
  `vm2.PopulateModelCostRows(null);` **is the point of the test** (explicit null must
  behave like the parameterless call). Do NOT delete the argument — route it through a
  typed local so the intent survives and the inspection is satisfied:

  ```csharp
  // Explicit null must take the same path as the parameterless call.
  IReadOnlyDictionary<string, string>? explicitNull = null;
  vm2.PopulateModelCostRows(explicitNull);
  ```

## 15. Invalid XML-doc crefs (3 findings)

- `tests/VisualRelay.Tests/BackendConfigGeneratorAliasConsistencyTests.cs`, doc comment
  on `TierAliasNames_AreConsistentAcrossBackendConfigPricingAndSwivalProfile`: the line
  `/// (2) Every <see cref="DefaultTierResolution"/> value has a` uses an unqualified
  member cref. Qualify it: `<see cref="BackendConfigGenerator.DefaultTierResolution"/>`
  (matching the already-qualified cref two lines above).
- `tests/VisualRelay.Tests/DesignDataTests.cs`, doc comment on
  `Main_LeavesSelectedTaskNull_SoPreviewsDoNoDiskIo`:
  - `<see cref="DesignData.Main.SelectedTask"/>` is not a resolvable cref (you cannot
    cref a member through a property's value). Replace the phrase with
    `<see cref="DesignData.Main"/>'s <c>SelectedTask</c>`.
  - `<see cref="MainWindowViewModel.StatusText"/>` cannot resolve in this file (no
    `using VisualRelay.App.ViewModels;`, and `StatusText` is generator-produced).
    Replace with `<c>MainWindowViewModel.StatusText</c>`. Do not add a using just for
    a doc comment.

## 16. Redundant using directives (15 findings; 14 removals — the 15th is section 3)

Remove exactly these directives, then build. Every one was semantically verified
redundant by InspectCode; `dotnet build` green after removal is the acceptance.

| File | Remove |
|---|---|
| `src/VisualRelay.App/ViewModels/MainWindowViewModel.Bootstrap.cs` | `using VisualRelay.App.Services;` |
| `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs` | `using System.Linq;` |
| `src/VisualRelay.App/ViewModels/MainWindowViewModel.Properties.cs` | `using VisualRelay.Core.Init;` |
| `src/VisualRelay.Core/Execution/RelayDriver.ReviewPair.cs` | `using System.Text.Json;` |
| `tests/VisualRelay.Tests/CostPerModelTests.cs` | `using VisualRelay.Core.Costs;` |
| `tests/VisualRelay.Tests/CostPerModelTests.Display.cs` | `using System.Text.RegularExpressions;` **and** `using VisualRelay.Core.Configuration;` |
| `tests/VisualRelay.Tests/SetupCheckResultsTests.cs` | `using VisualRelay.Core.Execution;` |
| `tests/VisualRelay.Tests/SetupCommitHelperTests.cs` | `using GitSimEngine = VisualRelay.GitSim.GitSim;` |
| `tests/VisualRelay.Tests/UserEnvSnapshotTests.cs` | `using VisualRelay.Core.Configuration;` |
| `tests/VisualRelay.Tests/VerifyWorktreeIgnoredOverlayCopyTests.Links.cs` | `using VisualRelay.Core.Execution;` **and** `using GitSimEngine = VisualRelay.GitSim.GitSim;` |
| `tests/VisualRelay.Tests/VerifyWorktreeIgnoredOverlayCopyTests.Recursive.cs` | `using VisualRelay.Core.Execution;` **and** `using GitSimEngine = VisualRelay.GitSim.GitSim;` |

`src/VisualRelay.Core/Execution/RelayDriver.cs`'s flagged using is handled in
section 3 (keep it; unqualify the call instead).

## 17. `.editorconfig` suppressions — the only three allowed (9 findings)

Append this section at the end of `.editorconfig` (after the existing
`[tests/**.cs]` block). These are tool blind spots, not defects:

```ini
# ── Scoped carve-outs (tool blind spots at specific sites) ─────────────────

# JetBrains' Avalonia XAML support misreads the attribute form of the
# Design.DataContext attached property with an {x:Static} value
# (Design.DataContext="{x:Static dt:DesignData.Main}") as a member-type
# mismatch ("expected AttachedProperty<object?>"), ×4 repo-wide: MainWindow,
# QueuePanel, QueueFooter, TaskCard. The idiom is Avalonia-canonical and
# runtime-proven (previewer + DesignDataTests); real XAML type errors still
# fail the Avalonia XAML compiler at build.
[*.axaml]
resharper_xaml_invalid_member_type_highlighting = none

# "selectionRail" is a marker class with deliberately NO style — it exists so
# TaskCardRenderTests can locate the rail Border via Classes.Contains(...).
# Two commits titled "preserve selection-rail marker class used by a render
# test" guard it; do not remove the class.
[src/VisualRelay.App/Views/Controls/TaskCard.axaml]
resharper_xaml_style_class_not_found_highlighting = none

# SetupCheckResults' *Output/*Command positional properties are consumed via
# System.Text.Json reflection — serialized into the per-attempt
# verify-checks.json artifact (and the record rides the control-API /state
# payload). InspectCode cannot see reflection-driven reads.
# SetupCheckResultsTests pins the serialized shape.
[src/VisualRelay.Core/Execution/RelayDriver.SetupChecks.cs]
resharper_not_accessed_positional_property_global_highlighting = none
```

This covers: `Xaml.InvalidMemberType` ×4, `Xaml.StyleClassNotFound` ×1,
`NotAccessedPositionalProperty.Global` ×4.

## Rejected approaches — do not do these

- **Deleting the `selectionRail` class from TaskCard.axaml** — breaks
  `TaskCardRenderTests` (it locates the rail by that class). Marker class stays;
  suppression is scoped to that one file.
- **Deleting the four "unaccessed" `SetupCheckResults` properties** — they are the
  payload of the `verify-checks.json` diagnostic artifact (reflection-serialized);
  removing them silently guts failure autopsies.
- **Removing `null` from `PopulateModelCostRows(null)`** — turns that test into a
  tautology; use the typed local from section 14.
- **Global rule disables to zero the count** (e.g. turning off
  `RedundantUsingDirective` or `EmptyGeneralCatchClause` wholesale) — the gate text
  forbids carving out real defects; only the three scoped blocks in section 17 are
  permitted.
- **Epsilon comparison** in `FormatRateRelativeToInput` — arbitrary tolerance where the
  actual contract is "the displayed strings match"; use the string comparison.
- **Static constructor or file move** for `DefaultTierResolution` — see section 1.

## Tests

No new test files. All existing tests must pass unmodified except the mechanical edits
prescribed above (sections 6–10, 13–15), none of which changes what any test asserts.

## Verification

1. `dotnet build` — green.
2. `dotnet test tests/VisualRelay.Tests --filter "FullyQualifiedName~ControlServerTests|FullyQualifiedName~TaskRowViewModelTests|FullyQualifiedName~CostPerModelTests|FullyQualifiedName~SetupCommitHelperTests|FullyQualifiedName~UserEnvSnapshotTests|FullyQualifiedName~DesignDataTests|FullyQualifiedName~BackendConfigGeneratorAliasConsistencyTests|FullyQualifiedName~RelayDriverBaselineVerifyTests|FullyQualifiedName~SetupCheckResultsTests|FullyQualifiedName~VerifyWorktreeIgnoredOverlayCopyTests|FullyQualifiedName~TaskCardRenderTests"` — green.
3. `./visual-relay check` — the inspect-code step must print
   `inspect-code: 0 findings — gate passed.` and the whole check must exit 0
   (this also re-runs the 300-line guard over the touched files).

## Constraints

- Touch ONLY: the files named in sections 1–16 plus `.editorconfig` (section 17).
- No new packages, no new files.
- Behavior-neutral throughout, with exactly two deliberate shape changes:
  `FormatRateRelativeToInput` compares formatted strings (section 2), and
  `TryPersistVerifyChecksJson` becomes `private void` (section 5).
- Do not modify `InspectCodeGate.cs`, `InspectCodeGateZeroFindingsTests.cs`, or the
  existing `.editorconfig` sections — only append the new block.
- `ControlServerTests.cs` and `TaskRowViewModelTests.cs` must not exceed 300 lines.
