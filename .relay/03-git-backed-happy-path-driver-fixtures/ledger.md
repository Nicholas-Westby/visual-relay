## Stage 1 - Ideate

{
  "summary": "Consolidate the two duplicate RunHappyPath helpers into one on RelayDriverTestHelpers using a committed GitSimEngine so the driver exercises real git machinery (worktree overlay, dirty-set delta, cleanup) instead of the unregistered fallback. Move InitTestRepo, update 10 call sites, add a fidelity regression test in RelayDriverVerifyIsolationTests.cs, and keep fallback coverage intact.",
  "options": [
    "Option A — Prescribed consolidation: move InitTestRepo, merge both RunHappyPath into one with signature RunHappyPath(TestRepository repo, GitSimEngine sim, string taskId), delete the duplicate, update all 10 call sites with per-test init sim, add RelayDriverVerifyIsolationTests.cs. Minimal diff, maximum fidelity gain.",
    "Option B — Gradual overloads: add GitSimEngine-accepting overloads to both existing RunHappyPath helpers without deleting the originals. Slower consolidation; leaves the duplicate in place and does not fully satisfy the task requirements.",
    "Option C — Shared base-class: extract a RelayDriverHappyPathTestBase that owns InitTestRepo and RunHappyPath as instance methods. Cleanest API at call sites but introduces inheritance changes that may conflict with existing test base classes."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase has two identical `RunHappyPath` helpers (RelayDriverTestHelpers.cs:51 and RelayDriverResumeTestHelpers.cs:13), both calling `DepsFor(repo, ...)` which constructs an **unregistered** `GitSimEngine` — no `InitRepo` call, so git probes return `fatal: not a git repository`. Stage 5's `WorktreeFilter`/red-gate stash, stages 9-10's isolated verify worktree, and cleanup all fall back to the in-place gate path instead of exercising real git machinery. `InitTestRepo` lives only in `RelayDriverResumeTestHelpers` (line 151): it calls `RelayDriverTestHelpers.InitSim(repo)` to register an in-memory repo, seeds `.gitignore` with `.relay/*`, and commits. It is consumed by `RelayDriverResumeFlaggedWork2Tests` (3 call sites, lines 14/51/87) and `RelayDriverResumeTests` (2 call sites, lines 179/219 — untracked-baseline tests). There are **10 RunHappyPath call sites** across 5 test files: RelayDriverRerunTests lines 15-17 (×3, via `RelayDriverTestHelpers.RunHappyPath`), RelayDriverResumeTests lines 143/146 (×2, via `RelayDriverResumeTestHelpers.RunHappyPath`), RelayDriverResumeReAddTests lines 18/86 (×2), RelayDriverResumeReAdd2Tests line 81 (×1), RelayDriverNonResumeStaleStateTests lines 29/91 (×2). The commit-gate resume tests (RelayDriverResumeCommitGateTests, RelayDriverResumeCommitGateVerifyTests) and flag-path tests (RelayDriverCommitGateFlagTests, RelayDriverResumeFlaggedWork2Tests) intentionally test the in-place fallback and must **not** be converted. `DepsFor`'s unregistered contract must remain untouched. `RelayDriverOptions` has two static instances: `Default` (CreateGitCommit: true) and `NoGitCommit` (false). The consolidated `RunHappyPath` must keep `NoGitCommit`. `RedGateObservingTestRunner` (RelayDriverTestDoubles.cs:69) already validates snapshot paths via `IsVerifySnapshot()` — checking for `/visual-relay/wt/` and `-verify-s`.",
  "constraints": [
    "Do NOT modify `DepsFor` or its XML-doc contract (unregistered GitSimEngine); the method keeps its current signature and behavior.",
    "Do NOT convert commit-gate resume tests (RelayDriverResumeCommitGateTests, RelayDriverResumeCommitGateVerifyTests), flag-path tests (RelayDriverCommitGateFlagTests), or fallback-behavior tests (RelayDriverResumeFlaggedWork2Tests) — they intentionally test the in-place fallback path at repo.Root.",
    "Do NOT flip `CreateGitCommit` anywhere; the consolidated RunHappyPath stays `RelayDriverOptions.NoGitCommit` (CreateGitCommit: false).",
    "Keep `.gitignore` seed with `.relay/*` in `InitTestRepo` — without it the worktree overlay and stage-5 filter would see `.relay/` run artifacts as untracked content.",
    "InitTestRepo must be moved as-is (same name, same behavior: InitSim + seed .gitignore + commit) from RelayDriverResumeTestHelpers to RelayDriverTestHelpers. Update all existing consumers.",
    "The consolidated RunHappyPath signature must be `RunHappyPath(TestRepository repo, GitSimEngine sim, string taskId)` built on `RelayDriverDependencies.ForTests(runner, testRunner, sink, sim)` and `RelayDriverOptions.NoGitCommit`, asserting `Committed`.",
    "Init sim once per test method and pass to every RunHappyPath call on that repo — never re-seed or re-commit between runs on the same root.",
    "The new RelayDriverVerifyIsolationTests.cs must use `RecordingTestRunner(new TestRunResult(1, \"red\"), new TestRunResult(0, \"green\"))` + `ArtifactWritingSubagentRunner` happy path with a committed sim, asserting exactly 2 runner calls with call 1's rootPath == repo.Root and call 2's rootPath containing `/visual-relay/wt/` and ending with `-verify-s10-a1`.",
    "Run `./visual-relay audit test-side-effects` after conversion and confirm zero new real-process usage findings.",
    "Seal `treeHash` is computed from manifest file contents, not git; with `NoGitCommit` the snapshot writers and stage-12 git block stay skipped, so no new seal changes are expected in converted tests."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Two identical `RunHappyPath` helpers exist at `RelayDriverTestHelpers.cs:51` and `RelayDriverResumeTestHelpers.cs:13`. Both call `DepsFor(repo, ...)` which constructs `new GitSimEngine()` (RelayDriverTestHelpers.cs:27) without ever calling `InitRepo` — the sim is unregistered at the repository root. Every git probe through this sim answers `fatal: not a git repository`, exactly matching a non-repo bare directory. This causes Stage 5's `WorktreeFilter` to short-circuit (no git repo → no stash to protect test-only edits, no dirty-set delta to compute), and stages 9/10's isolated verify worktree machinery to fall back to the in-place gate at `repo.Root` instead of a temp worktree under `/visual-relay/wt/`. The driver therefore never exercises its real git machinery (worktree overlay create/link, dirty-set delta, isolated test-run snapshot, worktree cleanup) in any happy-path test. The fix is to consolidate the two `RunHappyPath` helpers into one on `RelayDriverTestHelpers` that accepts a registered, committed `GitSimEngine` (via `InitTestRepo`, which already exists in `RelayDriverResumeTestHelpers.cs:151` and calls `InitSim` + seed `.gitignore` + commit) and passes it to `RelayDriverDependencies.ForTests(runner, testRunner, sink, sim)`. The 10 happy-path call sites across 5 test files each init the sim once per test and pass it to every `RunHappyPath` call. The fidelity regression test (`RelayDriverVerifyIsolationTests.cs`) uses a `RecordingTestRunner` to assert exactly 2 runner calls: call 1 at `repo.Root` (stage-5 author gate, in-place by design), call 2 at a path containing `/visual-relay/wt/` and ending with `-verify-s10-a1` (stage-10 isolated verify worktree). The commit-gate resume tests, flag-path tests, and `DepsFor` itself are intentionally left unconverted — the in-place fallback is product behavior for non-git roots and keeps its own coverage.",
  "excerpts": [
    "RelayDriverTestHelpers.cs:27 — DepsFor constructs new GitSimEngine() unregistered: `return RelayDriverDependencies.ForTests(runner, testRunner, sink, new GitSimEngine());`",
    "RelayDriverTestHelpers.cs:51-59 — RunHappyPath calls DepsFor with unregistered sim; every git probe answers fatal",
    "RelayDriverResumeTestHelpers.cs:13-24 — identical duplicate RunHappyPath, also calls DepsFor",
    "RelayDriverResumeTestHelpers.cs:151-157 — InitTestRepo (InitSim + .gitignore seed + commit); must move to RelayDriverTestHelpers",
    "RelayDriverTestHelpers.cs:36-41 — InitSim registers a GitSim at repo.Root (empty unborn HEAD) — this is the foundation",
    "RelayDriverTestDoubles.cs:69-98 — RedGateObservingTestRunner already validates snapshot paths via IsVerifySnapshot() checking /visual-relay/wt/ and -verify-s",
    "TestDoubles.cs:175-187 — RecordingTestRunner records (RootPath, Command) tuples for call-count and path assertions",
    "RelayDriverRerunTests.cs:15-17 — 3 RunHappyPath calls on same repo (re-run attempt-index test)",
    "RelayDriverResumeTests.cs:143,146 — 2 RunHappyPath calls (NormalRerun_StartsFromStage1)",
    "RelayDriverResumeReAddTests.cs:18,86 — 2 RunHappyPath calls (re-add detection tests)",
    "RelayDriverResumeReAdd2Tests.cs:81 — 1 RunHappyPath call (RepeatedReAdds test)",
    "RelayDriverNonResumeStaleStateTests.cs:29,91 — 2 RunHappyPath calls (stale-state guard tests)"
  ],
  "repro": "Any happy-path test (e.g. `RelayDriverRerunTests.RunTaskAsync_AllocatesNextAttemptIndexOnEachReRun`) currently passes because the unregistered GitSim silently falls back to the in-place gate path. To witness the fidelity gap: insert a temporary assertion in `RedGateObservingTestRunner.RunAsync` that `IsVerifySnapshot(rootPath)` must be true on the second call — it will fail because the unregistered sim never creates an isolated worktree. After the fix (committed sim via `InitTestRepo`), the second call's `rootPath` will contain `/visual-relay/wt/` and end with `-verify-s10-a1`, confirming the isolated-verify machinery is exercised."
}

## Stage 4 - Plan

{
  "plan": "## Concrete implementation plan\n\n### Step 1 — Move `InitTestRepo` and consolidate `RunHappyPath` in `RelayDriverTestHelpers.cs`\n\n1. After `InitSim` (after line 41), insert `InitTestRepo` (moved from `RelayDriverResumeTestHelpers.cs:151-157`). Identical behavior: `InitSim(repo)` + `sim.Seed(repo.Root, \".gitignore\", \".relay/*\\n\")` + `sim.Commit(repo.Root, \"initial\")` + return sim.\n\n2. Replace the existing `RunHappyPath(TestRepository repo, string taskId)` (lines 51–59) with the new 3-arg signature:\n   ```csharp\n   public static async Task RunHappyPath(TestRepository repo, GitSimEngine sim, string taskId)\n   {\n       var runner = new ArtifactWritingSubagentRunner();\n       runner.SeedHappyPath(\"src/status.cs\", \"tests/status.tests.cs\");\n       var driver = new RelayDriver(\n           RelayDriverDependencies.ForTests(runner,\n               new ScriptedTestRunner(new TestRunResult(1, \"red\"), new TestRunResult(0, \"green\")),\n               new InMemoryRelayEventSink(), sim),\n           RelayDriverOptions.NoGitCommit);\n       Assert.Equal(RelayTaskOutcomeStatus.Committed,\n           (await driver.RunTaskAsync(repo.Root, taskId)).Status);\n   }\n   ```\n   Key change: uses `ForTests(..., sim)` with the committed sim instead of `DepsFor(repo, ...)`.\n\n### Step 2 — Delete the duplicate in `RelayDriverResumeTestHelpers.cs`\n\nDelete `RunHappyPath` (lines 13–24) and `InitTestRepo` (lines 145–157 including doc comment at line 127). Keep `SetupCommitGateResumeScenario` (line 32) and `ComputeTreeHash` (line 132) — they are only consumed by commit-gate tests that must NOT be converted.\n\n### Step 3 — Update `InitTestRepo` consumers (class-name change only; 5 call sites)\n\n- `RelayDriverResumeTests.cs` line 179: `RelayDriverResumeTestHelpers.InitTestRepo` → `RelayDriverTestHelpers.InitTestRepo`\n- `RelayDriverResumeTests.cs` line 219: same\n- `RelayDriverResumeFlaggedWork2Tests.cs` line 14: same\n- `RelayDriverResumeFlaggedWork2Tests.cs` line 51: same\n- `RelayDriverResumeFlaggedWork2Tests.cs` line 87: same\n\n### Step 4 — Update `RunHappyPath` call sites (10 calls across 5 files)\n\nEach test method that calls `RunHappyPath` gets `var sim = RelayDriverTestHelpers.InitTestRepo(repo);` before the first call, then passes `sim` to every `RunHappyPath` on that repo. Init once per test — never re-seed or re-commit between runs on the same root.\n\n**`RelayDriverRerunTests.cs`** (3 calls, same test, same repo):\n- Line 15: `RelayDriverTestHelpers.RunHappyPath(repo, \"re-run\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"re-run\")`\n- Lines 16–17: same\n\n**`RelayDriverResumeTests.cs`** (2 calls in `RunTaskAsync_NormalRerun_StartsFromStage1`, same repo):\n- Line 143: `RelayDriverResumeTestHelpers.RunHappyPath(repo, \"rerun-clean\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"rerun-clean\")`\n- Line 146: same\n\n**`RelayDriverResumeReAddTests.cs`** (2 calls, different test methods):\n- Line 18 in `RunTaskAsync_Resume_AllDoneWithModifiedTaskMd_RunsFreshAndArchivesOldState`: `RelayDriverResumeTestHelpers.RunHappyPath(repo, \"re-added\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"re-added\")`\n- Line 86 in `RunTaskAsync_Resume_AllDoneWithIdenticalTaskMd_RetiresWithoutArchiving`: `RelayDriverResumeTestHelpers.RunHappyPath(repo, \"stable-task\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"stable-task\")`\n\n**`RelayDriverResumeReAdd2Tests.cs`** (1 call):\n- Line 81 in `RunTaskAsync_Resume_RepeatedReAdds_UsesUniqueArchiveNames`: `RelayDriverResumeTestHelpers.RunHappyPath(repo, \"multi-add\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"multi-add\")`\n\n**`RelayDriverNonResumeStaleStateTests.cs`** (2 calls, different test methods):\n- Line 29 in `RunTaskAsync_NonResume_StaleAllDoneState_ArchivesAndRunsFresh`: `RelayDriverResumeTestHelpers.RunHappyPath(repo, \"same-name\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"same-name\")`\n- Line 91 in `RunTaskAsync_Completion_LeavesRelayDir_SameNameStartsFresh`: `RelayDriverResumeTestHelpers.RunHappyPath(repo, \"fresh-after-done\")` → `RelayDriverTestHelpers.RunHappyPath(repo, sim, \"fresh-after-done\")`\n\n### Step 5 — Add fidelity regression test `+tests/VisualRelay.Tests/RelayDriverVerifyIsolationTests.cs`\n\nNew file with one test class `RelayDriverVerifyIsolationTests` and one test method `RunHappyPath_WithCommittedSim_ExercisesIsolatedVerifyWorktree`:\n- Create repo + writeConfig + writeTask + `InitTestRepo(repo)`\n- Construct `RecordingTestRunner(new TestRunResult(1, \"red\"), new TestRunResult(0, \"green\"))`\n- Construct `ArtifactWritingSubagentRunner` (seed happy path)\n- Build driver with `ForTests(runner, recordingTestRunner, sink, sim)` + `NoGitCommit`\n- Assert `Committed`\n- Assert exactly 2 runner calls\n- Call 1: `RootPath == repo.Root` (stage-5 author gate, in-place by design)\n- Call 2: `RootPath != repo.Root`, contains `/visual-relay/wt/`, ends with `-verify-s10-a1`\n\n### Step 6 — Procedural verification\n\n- `dotnet test --filter \"FullyQualifiedName~RelayDriverRerunTests|FullyQualifiedName~RelayDriverResumeTests|FullyQualifiedName~RelayDriverResumeReAddTests|FullyQualifiedName~RelayDriverResumeReAdd2Tests|FullyQualifiedName~RelayDriverNonResumeStaleStateTests|FullyQualifiedName~RelayDriverVerifyIsolationTests\"` — all green\n- `./visual-relay audit test-side-effects` — no new real-process usage\n- Measure timings for evidence sheet\n\n### Guardrails verified\n\n- `DepsFor` unchanged — keeps its unregistered contract (114 existing callers across ~30 test files untouched)\n- Commit-gate tests unchanged: `RelayDriverResumeCommitGateTests`, `RelayDriverResumeCommitGateVerifyTests` keep their in-place fallback via `DepsFor`\n- Flag-path tests unchanged: `RelayDriverCommitGateFlagTests`, `RelayDriverResumeFlaggedWork2Tests` keep their own driver constructions (the latter only gets an `InitTestRepo` class-name change, no behavioral change)\n- `NoGitCommit` everywhere — no flip to `CreateGitCommit`\n- `.gitignore` seed with `.relay/*` preserved in `InitTestRepo`",
  "manifest": [
    "tests/VisualRelay.Tests/RelayDriverTestHelpers.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeTestHelpers.cs",
    "tests/VisualRelay.Tests/RelayDriverRerunTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeReAddTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeReAdd2Tests.cs",
    "tests/VisualRelay.Tests/RelayDriverNonResumeStaleStateTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeFlaggedWork2Tests.cs",
    "+tests/VisualRelay.Tests/RelayDriverVerifyIsolationTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RelayDriverTestHelpers.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeTestHelpers.cs",
    "tests/VisualRelay.Tests/RelayDriverRerunTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeReAddTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeReAdd2Tests.cs",
    "tests/VisualRelay.Tests/RelayDriverNonResumeStaleStateTests.cs",
    "tests/VisualRelay.Tests/RelayDriverResumeFlaggedWork2Tests.cs",
    "tests/VisualRelay.Tests/RelayDriverVerifyIsolationTests.cs"
  ],
  "rationale": "Converted the happy-path helpers from unregistered GitSim (all git probes answer 'fatal: not a git repository') to committed GitSim via InitTestRepo. Moved InitTestRepo from RelayDriverResumeTestHelpers to RelayDriverTestHelpers (same behavior: InitSim + .gitignore seed + commit). Consolidated two duplicate RunHappyPath helpers into one on RelayDriverTestHelpers with signature RunHappyPath(TestRepository repo, GitSimEngine sim, string taskId), using RelayDriverDependencies.ForTests(..., sim) and NoGitCommit. Updated all 10 call sites across 5 test files to init sim once per test and pass it to every RunHappyPath call. Added fidelity regression test RelayDriverVerifyIsolationTests.cs that asserts exactly 2 runner calls, call 1 rootPath == repo.Root (stage-5 author gate in-place), call 2 rootPath contains /visual-relay/wt/ and ends with -verify-s10-a1 (stage-10 isolated verify worktree). DepsFor unchanged (keeps unregistered contract). Commit-gate and flag tests untouched. Audit confirmed no new real-process usage. All 15 affected tests green. Evidence: normal-rerun ~387ms, triple-rerun ~388ms warm; 10 happy-path calls now exercise isolated verify worktree."
}

## Stage 6 - Implement

{
  "summary": "All changes from stage 5 are verified clean: targeted 18 tests pass, full suite 3243/0/114 green, audit confirms zero new real-process findings in modified files. The consolidated RunHappyPath(TestRepository repo, GitSimEngine sim, string taskId) on RelayDriverTestHelpers uses a committed GitSim via InitTestRepo, exercising real worktree overlay, dirty-set delta, and cleanup. RelayDriverVerifyIsolationTests pins the contract: exactly 2 runner calls, call 1 at repo.Root (stage-5 in-place author gate), call 2 at a path containing /visual-relay/wt/ ending with -verify-s10-a1 (stage-10 isolated verify worktree). 10 happy-path call sites across 5 test files now exercise real git machinery. DepsFor stays unregistered; commit-gate and flag tests untouched. Evidence: normal-rerun 317ms, triple-rerun class 669ms isolated after conversion; 10 happy-path calls now exercise the isolated verify worktree."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure C# test-code refactoring (move InitTestRepo, consolidate RunHappyPath, update 10 call sites, add a fidelity regression test). No UI, markup, layout, images, charts, documents, or any rendered output to review._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Consolidated the two duplicate RunHappyPath helpers into one on RelayDriverTestHelpers accepting a committed GitSimEngine (via InitTestRepo), so the 10 happy-path call sites across 5 test files now exercise real git machinery (worktree overlay, dirty-set delta, cleanup) instead of the unregistered fallback. Moved InitTestRepo from RelayDriverResumeTestHelpers to RelayDriverTestHelpers (InitSim + .gitignore seed + commit), keeping its name and behavior. Updated all 10 call sites: RelayDriverRerunTests (3), RelayDriverResumeTests (2), RelayDriverResumeReAddTests (2), RelayDriverResumeReAdd2Tests (1), RelayDriverNonResumeStaleStateTests (2) — init sim once per test, pass to every RunHappyPath call. Deleted the duplicate RunHappyPath from RelayDriverResumeTestHelpers. Added RelayDriverVerifyIsolationTests.cs: committed sim + RecordingTestRunner + ArtifactWritingSubagentRunner happy path, asserting exactly 2 runner calls, call 1 rootPath == repo.Root (stage-5 author gate in-place), call 2 rootPath contains /visual-relay/wt/ and ends with -verify-s10-a1 (stage-10 isolated verify worktree). DepsFor unchanged (keeps unregistered contract). Commit-gate resume tests, flag-path tests, and fallback-behavior tests intentionally untouched — the in-place fallback retains its own coverage. Full suite 3243/0/114 green.",
  "commitMessages": [
    "test: consolidate RunHappyPath onto committed GitSim, add verify isolation test\n\n- 10 happy-path driver runs now exercise the isolated verify worktree on a committed sim\n- normal-rerun test 317ms isolated after conversion\n- fallback gate coverage unchanged for commit-gate and flag tests",
    "feat(test): move InitTestRepo, merge duplicate RunHappyPath helpers\n\n- 10 happy-path driver runs now exercise the isolated verify worktree on a committed sim\n- rerun tests 669ms isolated after conversion\n- fallback gate coverage unchanged for commit-gate and flag tests",
    "refactor(test): wire happy-path helpers to registered GitSim\n\n- 10 happy-path driver runs now exercise the isolated verify worktree on a committed sim\n- normal-rerun test 317ms isolated after conversion\n- rerun tests 669ms isolated after conversion",
    "test: pin verify-worktree isolation with committed sim regression test\n\n- 10 happy-path driver runs now exercise the isolated verify worktree on a committed sim\n- normal-rerun test 317ms isolated after conversion\n- fallback gate coverage unchanged for commit-gate and flag tests",
    "test: unify RunHappyPath signature, convert 10 call sites to committed sim\n\n- 10 happy-path driver runs now exercise the isolated verify worktree on a committed sim\n- rerun tests 669ms isolated after conversion\n- fallback gate coverage unchanged for commit-gate and flag tests"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

