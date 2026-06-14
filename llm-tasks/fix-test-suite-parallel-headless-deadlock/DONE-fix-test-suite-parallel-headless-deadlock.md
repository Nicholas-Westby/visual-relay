# Fix test-suite parallel headless deadlock: commit a `xunit.runner.json` that serializes test collections

## Current state (researched)

### Measured timings (2026-06-13, drain-free window)

Test command from `.relay/config.json`:
```
dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false
```

| Run | Mode | Wall time | Test duration | Pass | Deadlock? |
|-----|------|-----------|---------------|------|-----------|
| 1 | Default (parallelism ON) | 1 m 56 s | 1 m 40 s | 725/725 | No |
| 2 | Default (parallelism ON) | 1 m 43 s | 1 m 38 s | 725/725 | No |
| 1 | Serialized (`-- xunit.parallelizeTestCollections=false`) | 1 m 48 s | 1 m 42 s | 725/725 | No |

**The deadlock did not reproduce in these runs.** Both modes completed with 725 passing. The
delta between parallel and serialized is within noise (~8 s wall), confirming the serialized
mode carries negligible overhead on a healthy run.

**The deadlock is intermittent**, not deterministic — it is a race condition between headless UI
test classes that share one process-global Avalonia dispatcher but live in separate xUnit
collections (and thus run concurrently by default). Prior evidence is strong: `DONE-08` records
a 34-minute stall observed on this machine; `TROUBLESHOOTING.md:5-27` documents the exact
failure signature (`Testing (NNNs)` climbing, nothing completing); and the `DONE-migrate-
headless-tests-to-avaloniafact.md` history shows the suite has deadlocked xUnit runs repeatedly.

### Root cause: four Avalonia UI test classes run in separate, un-coordinated parallel collections

xUnit v3 (in use: `xunit.v3` 3.2.2) assigns each test class to its own collection by default
and runs them in parallel. Avalonia headless uses **one process-global app/dispatcher per
process**: `[AvaloniaFact]` (from `Avalonia.Headless.XUnit`) serializes tests *within* a
single collection on that dispatcher, but does NOT prevent *two different collections* from
starting an `[AvaloniaFact]` concurrently.

Four UI test classes use `[AvaloniaFact]` / `[AvaloniaTheory]` and carry **no `[Collection]`
attribute**, so each lands in its own default collection and can race the others:

- `ActivityColumnItemsPanelTests`
- `AddAttachmentsTests`
- `ConfigInitEmptyStateUiTests`
- `NewTaskAuthoringTests`

Two more UI test classes (`KeySetupPanelUiTests`, `SettingsPanelUiTests`) already carry
`[Collection("Environment")]`, which serializes them relative to each other — but NOT relative
to the four uncollected classes above.

**Prior partial fixes already in place:**

- `[AvaloniaFact]` / `[AvaloniaTheory]` on all headless tests (serializes *within* a
  collection); `HeadlessUnitTestSession` banned via BannedApiAnalyzers.
- Named `[Collection]` groups for shared global state: `"GitCommitter"`, `"Environment"`,
  `"Watchdog"`, `"GitInvoker"` — serializes those clusters relative to each other.
- Watchdog timeout in `./visual-relay test` (60 s cap) and in `./visual-relay check` (landed
  in task 08) so a wedge is bounded; `VISUAL_RELAY_TEST_TIMEOUT` controls it.
- The `SplitGuardVerificationTests.Conventions.cs` enforces that new classes in named groups
  continue to declare `[Collection]`.

**What is missing:** a mechanism that ensures the four uncollected Avalonia headless classes
cannot run concurrently with each other (or with the `"Environment"` group's headless tests).
The narrowest correct fix is to either place all six headless classes in a single shared
collection, or — simpler and more general — disable cross-collection parallelism for the whole
suite via `xunit.runner.json`.

### Why `xunit.runner.json` (not `[assembly: CollectionBehavior]`)

The project uses xunit v3 (`xunit.v3` 3.2.2) run via VSTest (`Microsoft.NET.Test.Sdk`
17.14.1 + `xunit.runner.visualstudio` 3.1.4). In xunit v3 the JSON config file is the
canonical way to set runner options; `[assembly: CollectionBehavior]` is a v2 attribute.
A committed `xunit.runner.json` at the test project root is:

- **Self-contained** — no attribute import needed, works identically under `dotnet test` and
  any IDE runner.
- **One file, zero code changes** — does not touch any `.cs` file or the `.csproj`.
- **General** — prevents any future headless test class from accidentally joining the race even
  if a developer forgets to add a `[Collection]` attribute.
- **Quantified tradeoff:** per the measurements above, serializing collections adds ≤ 10 s on a
  healthy suite run. The watchdog timeout (default 60 s for `test`, higher for `check`) remains
  as a safety net for any future wedge that somehow still occurs.

`xunit.runner.json` with `"parallelizeTestCollections": false` is the canonical xunit v3 way
to disable inter-collection parallelism (equivalent to the CLI flag
`-- xunit.parallelizeTestCollections=false` used in the measurement above, which confirmed
it works and completes at the same speed).

## What to build

### 1. Commit `xunit.runner.json` to the test project (the fix)

Create `tests/VisualRelay.Tests/xunit.runner.json` with content:

```json
{
  "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json",
  "parallelizeTestCollections": false
}
```

Include the `$schema` line for tooling completeness; `parallelizeTestCollections: false` is
the key setting. This is the full fix — no other file changes are required.

Verify the file is picked up by the runner: `dotnet test` must still discover and run all 725
tests, and must no longer show inter-collection parallelism (no two test classes running
simultaneously in `--blame-hang` output). If needed, add the file to the `.csproj` as
`<None Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />` so it is copied
alongside the test DLL (xunit looks for it next to the assembly).

### 2. Write the failing test first (TDD)

In `SplitGuardVerificationTests.Conventions.cs` (or a new companion file), add a test that
asserts `xunit.runner.json` exists at the test project root and contains
`"parallelizeTestCollections": false`. Write it as a `[Fact]`, run it first against the current
tree (it must fail with "file not found"), then create the JSON file to make it pass.

Example skeleton:
```csharp
[Fact]
public void XunitRunnerJson_DisablesTestCollectionParallelism()
{
    var configPath = Path.Combine(TestsDir, "xunit.runner.json");
    Assert.True(File.Exists(configPath), "xunit.runner.json must exist in the test project");
    var json = File.ReadAllText(configPath);
    Assert.Contains("\"parallelizeTestCollections\": false", json, StringComparison.Ordinal);
}
```

(`TestsDir` is already defined in `RepoSetup.cs` / the existing `SplitGuardVerificationTests`.)

### 3. No changes to existing `[Collection]` groups or Conventions guards

The named-collection groups (`"GitCommitter"`, `"Environment"`, `"Watchdog"`, `"GitInvoker"`)
remain intact — they still enforce class-level serialization within each group, and
`SplitGuardVerificationTests.Conventions` guards continue to require them on new classes in
those groups. The `xunit.runner.json` change is additive and does not contradict any existing
convention.

## Done when

- `xunit.runner.json` is committed at `tests/VisualRelay.Tests/xunit.runner.json` with
  `"parallelizeTestCollections": false`.
- The new `[Fact]` in `SplitGuardVerificationTests` (or companion) asserting the file exists
  and contains the setting was written first, failed against the pre-fix tree, and passes after.
- `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false`
  passes all 725 tests and completes in under 4 minutes across repeated runs (no stall, no
  `Testing (NNNs)` climbing forever).
- The existing watchdog timeout (in `./visual-relay test` and `./visual-relay check`) is
  preserved as a safety net for any future edge-case wedge — this task does not remove it.
- `./visual-relay check` is green.
- `git status` is clean; changed files are under 300 lines total.
- Conventional Commit subject: `fix(tests): disable test-collection parallelism to prevent headless Avalonia deadlock`
