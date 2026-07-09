# Migrate the Driver/Git Test Families onto GitSim; Gate the Real-Git Remainder Behind Opt-In

Part of the test-suite speed push (hard ceiling: full suite under 60 s; 45 s is the
aspirational target). Rule being enforced
here, set by the maintainer: **no always-on test may take more than 2 seconds solo.**
Anything over that is either converted to run against the in-memory git simulator, or
kept as a real-git integration test that is skipped by default behind an env opt-in —
with an always-on simulator-backed replacement covering the same logic.

Preflight: this task requires the `tests/VisualRelay.GitSim` project (a
`GitSim : IGitInvoker` in-memory git with per-root state, a real-filesystem working
tree, a `PreCommitHook` callback seam, and a seeding/inspection API —
`InitRepo`/`Seed`/`Commit`/`Head`/`BranchTip`/`CommitInfo`/`FilesInCommit`/…). Read its
source and its `GitSimTests*` before starting. If the project is absent, flag this task
immediately instead of building it ad hoc.

## Why (measured 2026-07-08, host Mac)

- The driver/git families (`RelayDriver*`, `GitCommitter*`, `Worktree*`,
  `NoCommitContamination`, `HistoryRewriter`, `RedGate*`, `PlanPhase*`,
  `TaskCompletionArchive*`, `DrainExecution*`, `AuthorshipClaimer`, `GitBootstrapper`,
  `PreCommitHook*`, …) are ~390 tests carrying ~71 % of all summed test time; run
  alone they take over 2 minutes of wall clock with system CPU dominating user CPU
  (process spawn + FS churn, not computation).
- Even git-free driver tests pay a real-git floor: `RelayDriverDependencies.ForTests`
  defaults `gitInvoker ?? new GitInvoker()`
  (`src/VisualRelay.Core/Execution/RelayDriverDependencies.cs`), so all eleven stages
  probe real git even on a non-repo `TestRepository` root. One-class solo timings for
  `RelayDriverTests` (FS-only!): typical full-pipeline facts 0.9–1.7 s each, and
  `RunTaskAsync_AllocatesNextAttemptIndexOnEachReRun` 4.75 s.
- Real-git setup is spawn-heavy: `RelayDriverTestHelpers.InitGitRepo` = 5 git
  processes per call (91 call sites); tests make 384 direct `TestGit.Run` calls for
  setup/asserts.
- Six headless UI facts cost ~2.9 s EACH (all six in `RewriteMutualExclusionTests` and
  `ControlApiConfirmGatedTests` that await `vm.WaitForRewriteToFinishForTests(...)`):
  the rewrite path runs `TaskRewriteRunner.RunAsync` (`src/VisualRelay.Core/Execution/TaskRewriteRunner.cs`),
  which does real `git worktree` add/remove plus a leftover-worktree reclaim scan.
  `TaskRewriteRunner.RunAsync` already accepts `IGitInvoker? git = null` — the tests
  simply never inject it.

## What to build

1. **Measurement harness for the 2 s rule.** Per test class in the families above, run
   `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build --filter "FullyQualifiedName~VisualRelay.Tests.<Class>." --logger trx`
   and read per-fact durations from the trx — that is the "solo" number (full-suite trx
   durations are 20–30× inflated by worker-pool contention; never use them for this
   rule). Keep a before/after table for the summary.
2. **Flip the default for driver tests via one test-side factory.** Add
   `RelayDriverTestHelpers.DepsFor(TestRepository repo, ISubagentRunner runner, ITestRunner testRunner, IRelayEventSink sink)`
   returning `RelayDriverDependencies.ForTests(runner, testRunner, sink, gitSimBoundTo(repo.Root))`,
   and sweep test call sites of `RelayDriverDependencies.ForTests(...)` to use it.
   Do NOT change the `ForTests` default in `VisualRelay.Core` itself (Core cannot
   reference the test-support project).
3. **Convert the git-asserting families.** Replace `InitGitRepo`/`TestGit.Run` setup
   with GitSim `InitRepo`/`Seed`/`Commit`, and `TestGit.Run` assert-reads
   (`log`, `rev-parse`, `show`, `status` shapes) with the corresponding GitSim
   inspection calls, keeping every assertion's semantics identical. Where a fact
   exercises `GitCommitter`'s hook-rejection path, model the hook with
   `GitSim.PreCommitHook` (commit-tree paths bypass it, as in real git).
4. **Curate the real-git integration set.** Create `RealGitIntegrationTests` (split
   into a few files if needed for the size guard) holding ~10–14 representative
   originals that genuinely need the real binary, each gated with the house opt-in
   idiom — `SlowIntegration.SkipIfNotOptedIn()` reading `VR_RUN_SLOW_INTEGRATION=1`,
   mirroring `NonoIntegration.SkipIfNotOptedIn()` (`tests/VisualRelay.Tests/NonoIntegration.cs`;
   keep the method name — the `RealBuildSubprocessGuardTests` AST scan recognizes it).
   The set must cover: pre-commit hook install + rejection end-to-end (`HookInstaller`
   writes a real `.git/hooks/pre-commit` that real git executes), one full
   `NoCommitContamination` plan+execute run, one squash end-to-end, one
   worktree-overlay end-to-end, one `FlaggedWorkStore` bundle capture/restore, and one
   `RedGate` stash cycle. `GitInvokerTests` (binary resolution, env sanitization)
   already test the real seam — leave them as they are.
5. **Fix the six ~2.9 s headless rewrite facts.** Add an internal seam on
   `MainWindowViewModel` next to the existing `RewriteRunnerFactory`
   (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs`):
   `internal IGitInvoker? RewriteGitInvokerForTests`, threaded into the
   `TaskRewriteRunner.RunAsync(...)` call via its existing `git:` parameter. Set it to
   the repo-bound GitSim in these tests. Each of the six facts must land well under
   1 s solo afterwards.
6. **Hermetic env for whatever still spawns real git** (the gated set, `ScratchRepo`,
   `TestGit`, `CliHarness`): set `GIT_CONFIG_GLOBAL=/dev/null`,
   `GIT_CONFIG_SYSTEM=/dev/null`, `GIT_TERMINAL_PROMPT=0` at those spawn seams —
   faster (no config scans) and host-independent.
7. **Anything still over 2 s solo after conversion** (measure again): gate it with
   `SlowIntegration.SkipIfNotOptedIn()` and add an always-on GitSim-backed fact
   covering the same decision logic. A fact whose >2 s cost is real waiting rather
   than git (there are known offenders in the watchdog families) is out of scope here
   — leave it and note it in the summary; a separate queued task owns wall-clock waits.

## Done when

- Per-class solo measurement shows **zero always-on facts over 2 s** across the
  families above (post-conversion table in the summary; borderline facts re-measured
  solo via single-fact filters).
- The combined family filter run
  (`--filter "FullyQualifiedName~VisualRelay.Tests.RelayDriver|…"` as measured before)
  completes in **under 30 s wall** on the host.
- Opted-in (`VR_RUN_SLOW_INTEGRATION=1`) run of `RealGitIntegrationTests` passes.
- No test asserts anything weaker than before its conversion (same failure modes must
  still fail: prove by spot-mutating — e.g. break the squash path locally, observe the
  GitSim-backed fact go red, revert).
- Full suite green 3× consecutively; `./visual-relay check` passes.

## Guardrails

- Production changes are limited to the ONE named seam
  (`RewriteGitInvokerForTests` threading into the existing `git:` parameter). No
  behavior changes; no other production file.
- If GitSim lacks a command a converted test needs, EXTEND GitSim (with a parity fact
  in its differential harness) — never try/catch around it or weaken the test.
- Only the `SlowIntegration.SkipIfNotOptedIn()` idiom may skip anything — no
  `[Fact(Skip = "…")]` string skips, no trait filtering, no deletions of coverage.
- Do not touch `xunit.runner.json`, the `Headless`/`Watchdog` collection definitions,
  or `.relay/config.json`.
- Conventional Commits; files under the size guard.
