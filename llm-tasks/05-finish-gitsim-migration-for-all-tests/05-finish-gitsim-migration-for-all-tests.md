# Task: Finish the GitSim migration — zero real git in the default test suite

Earlier waves built the in-memory simulator and migrated the driver family
(completed tasks `02-in-memory-git-simulator`, `03-migrate-git-tests-to-simulator`,
`eliminate-real-git-default-for-tests`, `04-required-git-invoker-threading`),
but 18 test files still shell out through `TestGit.Run` and 28 still construct
a real `new GitInvoker()`. Policy for this task: the DEFAULT suite runs zero
real git processes. Where GitSim can't express something a test needs, that is
a GitSim gap — extend GitSim, and pin the new behavior against real git in the
opt-in parity suite.

### Evidence (2026-07-19 slow-test investigation)

Remaining real-git families, with real-git process spawns per test and solo
measured times (VM, filtered runs; reported times from the host's parallel run
were 5-37s for these same tests):

| Family | Spawns/test | Solo | Notes |
|---|---|---|---|
| HistoryRewriterTests | 16-17 | 0.57-0.93s | export/replay, per-commit `diff-tree` |
| RewriteHistoryRunnerTests | ~33 | 0.96s | export runs twice, replay via `commit-tree` |
| AuthorshipClaimerTests | 8-14 | 0.4-0.8s | trailer rewrite chain |
| CompletionTimeResolverTests | 8-10 | ~0.25s | tier 3 = `log --follow` |
| RelayTaskRepositoryCompletionTimeTests | ~11 | 0.29s | two tier-3 probes |
| EarlyImplementationDetectorTests | 8-9 | 0.24-0.28s | detector diff/status calls |
| CommitLintRunnerTests | 5-9 | 0.1-0.22s | git only in fixture setup |
| HookInstallerTests | 4-7 | 0.15s | hook never executes |
| SourceEnumerationGuardTests | ~7 | 0.20s | guard runs `ls-files` twice |
| TaskRewriteRunnerTests (+Cancellation) | ~9 | 0.23-0.26s | real `worktree add/prune/remove` |
| ProjectBootstrapperTests | ~14 | 0.37-0.39s | bootstrap chain EXECUTES the installed bash pre-commit hook |
| SetupCheckResultsTests | 12-14 | 0.41-0.46s | via `BootstrapAsync` defaulting `new GitInvoker()` (`ProjectBootstrapper.cs:67`) |
| MainWindowViewModelTests (bootstrap/ensure/archive) | ~12 | 0.6-2.84s | same chain + toolchain probe |
| RelayQueueControllerTwoPhaseTests / DrainTests | 15-20 | 0.4-1.6s | controller defaults `_gitInvoker ?? new GitInvoker()` (`RelayQueueController.cs:162`, `RelayQueueController.PrivateHelpers.cs:40`) |
| RelayDriverResumeFlaggedWorkTests / …3Tests | varies | 0.5-1.4s | real `git bundle` create/verify/restore |

- `tests/VisualRelay.Tests/TestGit.cs` is the real-git shell-out helper (18
  files). `tests/VisualRelay.GitSim` already models add/commit/worktree/
  bundle/cherry-pick/log/ls-files/status/hooks (`Commands/`, `GitSimHooks.cs`).
- Guard precedent: `tools/VisualRelay.Guards/RealGitFallbackGuard.cs` bans
  `new GitInvoker(` and defaulted `IGitInvoker` parameters, but only under
  `src/VisualRelay.Core/Execution/` (`:34`).
- Parity anchor: `tests/VisualRelay.Tests/RealGitIntegrationTests.cs` is
  opt-in via `VR_RUN_SLOW_INTEGRATION=1` (skipped by default), with
  `ParityHarness.cs` — this suite is the real-git oracle that keeps GitSim
  honest, and after this task it is the ONLY place real git runs.

### What to build

Work family-by-family, one commit per family, suite green after each:

1. **Inventory first.** Regenerate the two lists (`TestGit\.Run` and
   `new GitInvoker\(` under `tests/VisualRelay.Tests`) and keep them in the
   run summary as the migration checklist. Counts above are as of 2026-07-19.
2. **Migrate each family to GitSim.** Replace `TestGit.Run` setup with
   `GitSim` seeding (patterns: `RelayDriverTestHelpers.InitTestRepo`,
   `GitSimTestHelpers.cs`) and inject the sim wherever a real invoker was
   constructed. Production seams that silently default to real git get the
   `04-required-git-invoker-threading` treatment: make the invoker required
   and thread it — starting with `RelayQueueController.cs:162` and
   `RelayQueueController.PrivateHelpers.cs:40` (composition roots — the App
   and the drain tool — construct the real invoker explicitly), and
   `ProjectBootstrapper.cs:67`.
3. **Close GitSim gaps as they surface.** Known/likely gaps to expect:
   `log --follow` rename tracking (CompletionTimeResolver tier 3), `bundle`
   create/verify/unbundle fidelity (FlaggedWork round-trips), `worktree
   prune`, `commit-tree`/`diff-tree` shapes used by HistoryRewriter, and
   installed-hook semantics (model the hook FILE plus the existing
   `GitSimHooks` verdict delegates — GitSim never executes real scripts).
   Every gap closed gets (a) a GitSim unit test and (b) a parity case added
   to the opt-in real-git suite asserting GitSim's output matches real git
   for that command shape.
4. **Retire and guard.** Delete `TestGit.cs` once it has zero users. Add a
   test-side guard (new `TestRealGitGuard` in `tools/VisualRelay.Guards`,
   mirroring `RealGitFallbackGuard`, run from a guard-as-test over
   `CachedSyntaxTreesFixture`) that flags, under `tests/VisualRelay.Tests`:
   `new GitInvoker(` and any `ProcessStartInfo` whose file name is `git`.
   Exemption list: exactly `RealGitIntegrationTests.cs` and
   `ParityHarness.cs`.

### Constraints

- Coverage is non-negotiable: no test deleted, skipped, or weakened. Every
  migrated test carries a name-by-name mapping (old name → new location) in
  the run summary. A scenario that genuinely asserts REAL git behavior moves
  into the opt-in parity suite — gated is fine, gone is not.
- GitSim stays in-memory: no subprocesses, no real clock, no script
  execution.
- The default `./visual-relay test` run must end with zero real git spawns;
  the parity suite stays opt-in (`VR_RUN_SLOW_INTEGRATION=1`) and is the only
  exemption.
- One family per commit; keep files under the 300-line guard.

### Tests (red first)

- Per family: migrate the test, watch it fail if GitSim lacks the command,
  extend GitSim with its own failing-first unit test, then green the family.
- Parity: each new/changed GitSim command gets a `VR_RUN_SLOW_INTEGRATION`
  case comparing GitSim output to real git output for the same operations.
- Guard: synthetic source with `new GitInvoker(` in a test file yields one
  violation; the migrated repo tree yields zero.

### Commit-message evidence

Measure before and after while implementing (per-family solo class times, and
a final full-suite number for the batch), then put one filled-in evidence
bullet in each commit's message body, following the attached
`commit-message-evidence.md`. Never pre-fill those bullets — numbers are
measured at implementation time and go into the eventual commit messages,
nowhere else.
