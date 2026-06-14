# Harness: auto-format the worktree before each verify check

## Current state (researched)

### The format-tax problem

`guardCmd` in `.relay/config.json` (this repo) ends with:

```
dotnet format VisualRelay.slnx --verify-no-changes
```

When an agent writes unformatted code, stage 9 Verify turns red solely because of
whitespace/style. The harness then enters the ~12-minute Fix-verify loop (stage 10),
spending an entire LLM call and re-check just to run `dotnet format`. This happens on
nearly every task.

### The two verify sites where `guardCmd` runs

**Site 1 — stage 9 Verify (`RelayDriver.cs:213`):**

```csharp
var (guardFailed, guardOutput, guardTimedOut) = await IntegrateGuardAsync(
    rootPath, taskId, runId, config, ledger, cancellationToken);
```

`IntegrateGuardAsync` (`src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs:106–135`)
calls `RunGuardCheckAsync` (`RelayDriver.RepoGuards.cs:26–81`) which invokes
`testRunner.RunAsync(rootPath, guardCmd, ct)` at line 35.

**Site 2 — fix-verify re-verify (`RelayDriver.VerifyFix.cs:136–154`):**

```csharp
if (guardCmd is not null)
{
    var (newViolations, _, timedOut) = await RunGuardCheckAsync(
        rootPath, taskId, runId, _dependencies.TestRunner,
        guardCmd, config.BaselineVerify, cancellationToken);
    ...
}
```

`RunGuardCheckAsync` is called directly (not via `IntegrateGuardAsync`) here, at line 138.

**Both sites call `RunGuardCheckAsync`** — the one shared private method at
`RelayDriver.RepoGuards.cs:26`. The format hook must fire inside that method,
immediately before the `testRunner.RunAsync(rootPath, guardCmd, ct)` at line 35,
so it fires at both Verify and each fix-verify re-verify automatically.

### `RelayConfig` and loader patterns to follow

`src/VisualRelay.Domain/RelayConfig.cs`: optional fields use `string? FieldName = null`
default parameters (e.g. `GuardCommand = null` at line 55).

`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:153`: `GuardCommand` is loaded
via `OptionalStringOrNull(root, "guardCmd")`. The same helper handles absent-or-blank →
null. New field: `formatCmd`, JSON key `"formatCmd"`, same `OptionalStringOrNull` pattern.

### `TestRepository.WriteConfig` signature (for tests)

`tests/VisualRelay.Tests/TestDoubles.cs:53`:

```csharp
public void WriteConfig(string testCommand, string[] logSources,
    bool baselineVerify = true, int maxVerifyLoops = 0, bool archiveOnDone = true)
```

Extend with `string? formatCmd = null` (written into the JSON when non-null).

### Init detection pattern to follow

`src/VisualRelay.Core/Init/TestCommandDetector.cs`: `GuardCommandDetector.Detect` (line
120–156) detects guard scripts and appends `dotnet format ... --verify-no-changes` when a
.NET solution exists. The new `FormatCommandDetector.Detect` follows the same toolchain
detection order used in `TestCommandDetector.DetectCandidates` (lines 34–81):
`.slnx`/`.sln`/`.csproj` → `dotnet format`, `package.json` → `npm run format` (or
`prettier --write .`), `go.mod` → `gofmt -w .`, `Cargo.toml` → `cargo fmt`.

`src/VisualRelay.Core/Init/RelayConfigWriter.cs:28`: `Write` already calls
`GuardCommandDetector.Detect` and writes `guardCmd` when non-null. Add the analogous
`FormatCommandDetector.Detect` call and write `formatCmd` when non-null.

## What to build

### 1. Add `FormatCommand` to `RelayConfig`

In `src/VisualRelay.Domain/RelayConfig.cs`, add after the `GuardCommand` parameter:

```csharp
// Optional whole-project formatter run unconditionally before each guard check.
// When set, the harness auto-formats the working tree so format-only guard
// failures never trigger a Fix-verify loop. Absent (null) → no-op; the
// existing guard behavior is unchanged. Takes no filename arguments — the
// formatter must accept whole-project invocation (e.g. "dotnet format
// VisualRelay.slnx", "prettier --write .", "gofmt -w .", "cargo fmt").
string? FormatCommand = null,
```

In `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`, add one line in the
`config = defaults with { ... }` block alongside `GuardCommand`:

```csharp
FormatCommand = OptionalStringOrNull(root, "formatCmd"),
```

### 2. Run `formatCmd` inside `RunGuardCheckAsync` before the guard check

In `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs`, change the signature of
`RunGuardCheckAsync` to accept `string? formatCmd` alongside `string guardCmd`:

```csharp
private static async Task<(string? NewViolations, string? FullOutput, bool TimedOut)> RunGuardCheckAsync(
    string rootPath,
    string taskId,
    string runId,
    ITestRunner testRunner,
    string? formatCmd,   // NEW — null is a no-op
    string guardCmd,
    bool baselineVerify,
    CancellationToken ct)
```

At the very start of the method body, before the existing `var workingResult = ...`
call at line 35, insert:

```csharp
// Auto-format the working tree before the guard check so format-only
// violations never cause a Fix-verify loop.
if (!string.IsNullOrWhiteSpace(formatCmd))
    await testRunner.RunAsync(rootPath, formatCmd, ct);
```

The format runner's exit code and output are intentionally ignored — it is a
best-effort mutation, not a gate. The guard check immediately after it acts as the
real assertion.

Update the two call sites to pass `config.FormatCommand`:

- `IntegrateGuardAsync` (`RelayDriver.RepoGuards.cs:117`): add `config.FormatCommand` as
  the new argument after `_dependencies.TestRunner`.
- `RunVerifyFixLoopAsync` (`RelayDriver.VerifyFix.cs:138`): add `config.FormatCommand` as
  the new argument after `_dependencies.TestRunner`.

No other code changes in the driver are required. The format command fires once when
Verify passes first try, or once per fix-verify iteration — total of "once or twice" per
task, as designed.

### 3. Commit safety: auto-formatting lands in the commit

The committer at stage 11 is manifest-scoped (`GitCommitter` adds only manifest-listed
files and proof artifacts). In a correctly-formatted upstream repo `formatCmd` only
reformats the task's own changed files — those are already in the manifest — so the
formatted output is automatically included in the commit. No change to the committer is
required. Document this in the field comment added in step 1.

### 4. Update `TestRepository.WriteConfig` for tests

Add `string? formatCmd = null` parameter and write `"formatCmd": "..."` into the JSON
when non-null, alongside the existing `guardCmd` pattern in the repo guard tests.

### 5. Init detection: `FormatCommandDetector`

Add `FormatCommandDetector` to `src/VisualRelay.Core/Init/TestCommandDetector.cs`
(below the existing `GuardCommandDetector`). Detection priority mirrors
`TestCommandDetector.DetectCandidates`:

```csharp
public static class FormatCommandDetector
{
    public static string? Detect(string rootPath)
    {
        // .NET solution or project
        var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln  = Directory.EnumerateFiles(rootPath, "*.sln",  SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (slnx is not null || sln is not null)
            return $"dotnet format {Path.GetFileName(slnx ?? sln!)}";
        if (HasAnyFile(rootPath, "*.csproj"))
            return "dotnet format";

        // Bun / Node — look for a format script in package.json; fall back to prettier
        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            var fmt = ReadPackageJsonFormatScript(rootPath);
            return fmt ?? "prettier --write .";
        }

        // Go
        if (File.Exists(Path.Combine(rootPath, "go.mod")))
            return "gofmt -w .";

        // Rust
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
            return "cargo fmt";

        return null;
    }

    private static string? ReadPackageJsonFormatScript(string rootPath) { /* same pattern as ReadPackageJsonScriptsTest */ }
}
```

`HasAnyFile` is already a private static helper in the same file — make it
`internal static` (or move the detector into the same class) so `FormatCommandDetector`
can reuse it, or duplicate the one-liner.

In `src/VisualRelay.Core/Init/RelayConfigWriter.cs`, add below the `guardCmd` block
(lines 28–31):

```csharp
var formatCmd = FormatCommandDetector.Detect(rootPath);
if (formatCmd is not null)
{
    json["formatCmd"] = JsonValue.Create(formatCmd);
}
```

This repo's own `.relay/config.json` should have `"formatCmd"` added manually:
`"formatCmd": "dotnet format VisualRelay.slnx"`. The implementing agent should add it
as part of this task (it is a config file change, not a source change, and is safe to
commit as part of the task manifest).

### 6. Tests (write these first — TDD)

All in `tests/VisualRelay.Tests/`.

**a. `RelayDriverFormatBeforeVerifyTests.cs`**

These three tests exercise the core behavioral guarantee using `RecordingTestRunner`
(already in `TestDoubles.cs:143`) with a `CommandDispatchTestRunner`-style dispatch to
route the format command, guard command, and test command to separate scripted runners:

1. **`FormatCmd_Set_RunsBeforeGuardAtVerifyAndNoFixVerifyEntered`** — `formatCmd`
   is `"my-formatter"`, `guardCmd` is `"my-guard"`, `baselineVerify: false`,
   `maxVerifyLoops: 1`. Guard returns green on first call (after formatter fires).
   Assert: the `RecordingTestRunner.Calls` list contains `"my-formatter"` before
   `"my-guard"`, and stage 10 is never invoked (no fix-verify runner calls).

2. **`FormatCmd_Set_RunsBeforeGuardInFixVerifyIteration`** — guard returns red on first
   call (stage 9), then green on second call (fix-verify re-verify). Assert:
   `RecordingTestRunner.Calls` shows formatter fired before each guard call — twice
   total (once in stage 9, once in the fix-verify re-verify). Stage 10 is committed
   (outcome = Committed).

3. **`FormatCmd_Unset_NeitherFormatterNorBehaviorChanges`** — no `formatCmd` in config.
   Assert: `RecordingTestRunner.Calls` contains no format command, and the driver
   behaves identically to the existing guard tests (green guard → committed, no stage
   10).

**b. `RelayConfigLoaderFormatCmdTests.cs`**

Two focused tests:
- `FormatCmd_AbsentFromJson_IsNull` — load a config.json without `"formatCmd"`; assert
  `config.FormatCommand is null`.
- `FormatCmd_PresentInJson_IsRead` — load `{"testCmd":"t","logSources":[],"formatCmd":"cargo fmt"}`; assert `config.FormatCommand == "cargo fmt"`.

**c. `FormatCommandDetectorTests.cs`**

Four unit tests for the pure detector:
- `.slnx` present → returns `"dotnet format <name>.slnx"`.
- `Cargo.toml` present → returns `"cargo fmt"`.
- `go.mod` present → returns `"gofmt -w ."`.
- None present → returns `null`.

## Done when

- **`formatCmd` set, first-pass green:** with `formatCmd` configured, the formatter is
  called before `guardCmd` at stage 9 Verify; guard passes; outcome is Committed with no
  stage-10 invocation — asserted by test (a.1).
- **`formatCmd` set, fix-verify iteration:** formatter fires before guard at both the
  stage-9 check and the fix-verify re-verify — two formatter calls total, asserted by
  test (a.2) via `RecordingTestRunner.Calls` order.
- **`formatCmd` unset:** no formatter call, behavior unchanged from today — asserted by
  test (a.3).
- **Config loader:** `"formatCmd"` round-trips correctly; absent → null — asserted by
  test (b).
- **Init detector:** `FormatCommandDetector.Detect` returns the right command for .NET,
  Rust, Go, and returns null when no marker is found — asserted by test (c).
- **This repo's config:** `"formatCmd": "dotnet format VisualRelay.slnx"` added to
  `.relay/config.json` so VR's own self-hosting runs get the format applied
  automatically.
- **Backward-compatible:** projects with no `"formatCmd"` in their config see zero
  behavior change — the only code path added is behind a null check.
- **All tests pass:** `./visual-relay check` green. No changes to existing test
  assertions required.
- **Files under 300 lines each.** `RunGuardCheckAsync` gains ~4 lines; the config
  record gains one parameter line; the loader gains one mapping line; the detector is a
  new class under 60 lines; the three test files are each under 80 lines.
- **Conventional Commit subject candidates:**
  - `feat(driver): auto-format worktree before each guard check`
  - `feat(harness): run formatCmd before guardCmd to eliminate format-tax`
  - `feat(config): add formatCmd to auto-format before verify`
