# Hoist shared CS-file parse results for source-scanning guard tests

Five guard tests each re-parse every `.cs` file in the test project on every
run, repeating the same I/O and tree-walk for different code-pattern checks.
Individually they clock 9–10 s; together they are ~50 s of *serialized* PPM
(parse-per-method) work that balloons under thread contention. Each guard test
shares the same expensive preliminary step: glob all `.cs` files, read them,
and feed each through a Roslyn `CSharpSyntaxTree.ParseText`. A single
assembly-level class fixture can run that parse once and let every guard read
the cached tree list.

## Measured cost

From the baseline:
- `SyncOverAsyncGuardTests.AllTestProjectCsFiles_HaveNoSyncOverAsync` — 10.3 s
- `RealBuildSubprocessGuardTests.AllTestProjectCsFiles_AreSandboxBuildSafe` — 10.1 s
- `GateAsTestSandboxGuardTests.AllTestProjectCsFiles_AreGateAsTestSandboxSafe` — 10.1 s
- `RealSleepGuardTests.AllTestProjectCsFiles_AreSleepFree` — 9.3 s
- `DeadConfigFieldGuardTests.LiveTree_HasNoDeadConfigFields` — 9.6 s

Total serial parse cost: roughly 49 s (single-threaded). Under 2.0×
parallelism these four become a long tail of ~30 s.

(See `llm-tasks/completed/speed-up-automated-tests-july-17/timings-baseline.txt`.)

## Prescribed approach

Add an `IClassFixture<CachedSyntaxTreesFixture>` (or `AssemblyFixture` — same
scope) that, in its `InitializeAsync`, globs the test-project `.cs` files once,
parses each with `CSharpSyntaxTree.ParseText(fileContent, path: filePath)`, and
stores the `List<(string Path, SyntaxTree Tree)>`. Each guard test injects the
fixture via its constructor and reads the pre-parsed list instead of calling
its own private helper that re-globs and re-parses.

The fixture is read-only (immutable once constructed) — safe to share across
all parallel test workers.

### Steps

1. Create `CachedSyntaxTreesFixture : IAsyncLifetime` in a new file at
   `tests/VisualRelay.Tests/CachedSyntaxTreesFixture.cs`.
2. In `InitializeAsync`, find the test project's `.cs` files, read each one,
   parse with `CSharpSyntaxTree.ParseText`, and store the list.
3. Add `: IClassFixture<CachedSyntaxTreesFixture>` to each of the five guard
   test classes (or declare a single `[assembly: AssemblyFixture]`).
4. Replace each guard's private parse-everything helper with a read from the
   injected fixture.
5. Each guard's *analysis* pass stays the same — only the parse step is shared.
6. Run the full suite to green.

### Guardrails
- Coverage rules: no test assertion changes, no deletions, no disabled tests.
- The fixture must use `IAsyncLifetime` or `IClassFixture` (xUnit manages
  per-class construction; a single-instance assembly fixture is ideal).
- The parsed `SyntaxTree` list is immutable; never mutate it.
- If any guard mutates the tree (unlikely but check), provide a defensive copy.

## Expected savings

First-guard wall time stays ~1 s (parse cost), but the other four drop from
9–10 s to sub-second (no re-parse). Wall-time savings: roughly 25–30 s from the
full-suite tail.

## Commit-message evidence

Measure before and after while implementing, then put one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Never pre-fill that bullet — numbers are measured
at implementation time and go into the eventual commit message, nowhere else.
