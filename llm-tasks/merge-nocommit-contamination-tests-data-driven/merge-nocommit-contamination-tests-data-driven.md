## Task: Merge near-duplicate NoCommitContaminationTests into one data-driven test

Convert the three `NoCommitContaminationTests` facts into a single `[Theory]` with data rows, eliminating per-test setup overhead while keeping every original assertion intact.

### Baseline measurements

From `llm-tasks/speed-up-automated-tests/timings-baseline.txt`:

| Test | Duration |
|---|---|
| `TwoTasks_ManifestAuthority_EnforcedAcrossPlanExecuteSplit` | 12.96 s |
| `TwoTasks_PlanThenExecute_EachCommitContainsOnlyItsOwnFiles` | 11.50 s |
| `TwoTasks_FirstCommitDoesNotIncludeSecondTasksUntrackedFiles` | 10.12 s |
| **File total** | **~35 s** |

All three tests share the same expensive arrange: `TestRepository.Create()`, `WriteConfig`, `WriteTask`, `InitSim`, `Seed`, `PlanPhaseRunner.RunPlanPhaseAsync` with two tasks, then two `RelayDriver.RunTaskAsync` calls in serial. They differ only in task naming, runner type, execution order, and which files are asserted on in which commit. The shared setup is recreated from scratch three times.

### Prescribed approach

Merge the three facts into a single `[Theory]` with `[MemberData]` that provides:

1. **Task identifiers**: `(taskIdA, taskIdB)` — e.g. `("task-a","task-b")`, `("first","second")`, `("clean","mixed")`.
2. **Runner factories**: `Func<ISubagentRunner>` for each task. The ManifestAuthority test uses `FileWritingSubagentRunner` wrapping `BadManifestSubagentRunner` + `ScriptedSubagentRunner`; the other two use `DualTaskSubagentRunner`. Wrap these in `Func` delegates so they're created fresh inside the test body.
3. **Execution order**: `(runFirst, runSecond)` — which task's RelayDriver runs first.
4. **Assertion delegate**: `Action<string, GitSimEngine, string, string>` that receives the repo root, the sim, the seed hash, and verifies commit contents. Each row supplies its own assertion block.

The merged test body:

```
TestRepository.Create → WriteConfig → WriteTask x2 → InitSim → Seed → PlanPhaseRunner(2 tasks) → RelayDriver(runFirst) → RelayDriver(runSecond) → row.Assert(repo, sim, seedHash)
```

Keep the original three tests as separate files (they currently live in `NoCommitContaminationTests.cs` and `NoCommitContaminationTests.ManifestAuthority.cs`) by adding the `[Theory]` and `[MemberData]` to a new method in the same partial class, then deleting the three `[Fact]` methods. The partial-class split stays: the theory and its data source can live in either file.

### Name-by-name coverage mapping

| Original test | → Destination |
|---|---|
| `TwoTasks_PlanThenExecute_EachCommitContainsOnlyItsOwnFiles` | Data row 0 in the new `[Theory]` |
| `TwoTasks_FirstCommitDoesNotIncludeSecondTasksUntrackedFiles` | Data row 1 in the new `[Theory]` |
| `TwoTasks_ManifestAuthority_EnforcedAcrossPlanExecuteSplit` | Data row 2 in the new `[Theory]` |

Every assertion from each original test must appear verbatim in its row's assertion delegate. No assertion may be weakened or removed.

### Expected saving

The three tests currently cost ~35 s total (~12 s each). A single data-driven test pays the setup cost once instead of three times, bringing the file to ~15 s. Expected saving: **~20 s** from full-suite wall time.

### Pitfalls and guardrails

- **Never weaken an assertion.** Every `Assert` from every original test must appear in the corresponding data row's assertion delegate.
- **Runner creation must be per-row.** Do not share a single subagent-runner instance across rows — each row creates its own via the factory delegate, matching the original isolation.
- **The ManifestAuthority test uses `BadManifestSubagentRunner`.** That runner type must be constructed fresh per test run; do not cache or reuse it.
- **Test count changes from 3 to 1.** The coverage mapping above accounts for all three removed names.
- **Do not change the `[Collection("Headless")]` attribute or any file/class-level attributes.** This file is not in the headless collection.
- **The existing `SplitGuardVerificationTests` convention guard requires each test class to be in a file matching its name.** The `NoCommitContaminationTests` partial class already spans two files; adding a theory to either file is fine as long as the class name stays consistent.

### Commit-message evidence

```
- test time dropped from 35s to 15s, saving 20s (NoCommitContaminationTests file total)
```
