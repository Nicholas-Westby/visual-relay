# Bootstrap's smoke run must leave the target tree as it found it

Bootstrap validates each detected test command by running it in the operator's
working tree: unsandboxed, in place, and with no accounting of what the run
writes. A suite that writes files when it fails (insta's `.snap.new` sidecars,
coverage output, regenerated fixtures) leaves them behind, and the first thing
the pipeline then meets is a dirty tree it did not create. On colored the
smoke's bare `cargo test` dropped two untracked snapshot files three seconds
before `config.json` was written. This task makes every validation run
accountable: whatever it adds or changes is reverted before bootstrap reports,
and the report says what was reverted.

## Evidence

- Validation of 2026-09-11 (three repos, nine tasks): on colored, bootstrap's
  `cargo test` was red without the crate's env vars and left two untracked
  `src/snapshots/*.snap.new` files three seconds before `.relay/config.json`
  appeared. The operator had to notice and delete them by hand.
- `/Users/admin/Dev/vr-work/colored.prep.md`: bare `cargo test` is red at
  baseline (13 tests assert ANSI output the crate suppresses off a tty), and
  `INSTA_UPDATE=no` is what stops insta writing `.snap.new`. The detector's
  candidate is the bare `cargo test` (`Init/TestCommandDetector.cs:74-76`), so
  on this repository the smoke always takes the red path.
- No test asserts that validation leaves the tree clean, and nothing under
  `src/VisualRelay.Core/Init/` runs `git status` before or after a smoke run.

## Current state (researched at 5b85640b)

- `Init/ProjectBootstrapper.cs:84` `BootstrapAsync(rootPath, gitInvoker,
  validationRunner, validationTimeoutMs, ct)`. Step 1 (`:96`) is
  `ResolveTestCommandAsync` (`:164-199`): for every candidate from
  `TestCommandDetector.DetectCandidates(rootPath)` (`:167`) it awaits
  `validator.ValidateAsync(rootPath, candidate, ct)` (`:177`); the first
  `Accepted` wins (`:180`); all rejected falls back to `PlaceholderTestCommand`
  with a `SetupCheckDiagnostic.FromFailedValidation` (`:193`). Step 2 (`:100`)
  writes the config (`RelayConfigWriter.Write`). `GitBootstrapper.EnsureRepositoryAsync`
  runs only at `:110`, after validation; the hook at `:113`.
- `Init/TestCommandValidator.cs:24-31` `ValidateAsync` executes the command
  through the injected `ITestRunner` and classifies the exit (`Classify`, `:43`):
  a red run with test-shaped output is accepted (`:73`), which is exactly the
  run whose side effects survive. The validator owns no cleanup and no timeout.
- The runner is `Execution/ShellTestRunner.cs:23`, built by
  `ProjectBootstrapper.CreateValidationRunner(timeout)` (`:77-78`,
  `loginShell: false`), cwd = the root (`SandboxedLaunch.StartIn`,
  `Execution/SandboxedLaunch.cs:28`), no nono. Timeouts: `InitValidationTimeout`
  60 s (`:64`), `CreateConfigValidationTimeout` 2 min (`:57`),
  `UpgradeValidationTimeout` 120 s (`:48`).
- Four surfaces validate: CLI init (`tools/VisualRelay.Init/Program.cs:14`), the
  GUI bootstrap button (`MainWindowViewModel.Bootstrap.cs:37`), the GUI
  create-config path (`MainWindowViewModel.Execution.cs:193-201`, which builds
  its own `TestCommandValidator`), and the placeholder upgrade
  (`MainWindowViewModel.RunnableGate.cs:29`,
  `ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync`).
- The before/after pattern already exists for the verify suite:
  `RelayDriver.VerifyWorktree.cs:18` `RunIsolatedVerifyAsync` captures
  `CaptureDirtySetAsync` (`:186`, `git ls-files --others --exclude-standard -z`)
  before and after the run and reports the delta. `Execution/WorktreeResetter.cs:30`
  restores tracked files and deletes untracked files against a snapshot.

## Prescribed approach

Keep the smoke in the real tree. That is where the caches are warm, where the
environment is the operator's, and where the command the pipeline will run is
actually tested. Make the run accountable instead of moving it.

1. `Init/TreeRestoringTestRunner.cs`: `internal sealed class
   TreeRestoringTestRunner(ITestRunner inner, IGitInvoker git,
   Action<TreeCleanup>? onCleanup = null) : ITestRunner`, with
   `internal sealed record TreeCleanup(string Command, IReadOnlyList<string>
   Removed, IReadOnlyList<string> Restored, bool Accounted)`. `RunAsync`:
   - before the run: `git -c core.quotePath=false status --porcelain -z
     --untracked-files=all`; the set of every path listed (tracked-modified,
     staged and untracked alike) is the baseline;
   - run `inner`;
   - after the run: the same status. For every path NOT in the baseline: an
     untracked path is deleted (then each now-empty parent directory up to the
     root), a path with an index change gets `git reset -q -- <paths>`, and a
     tracked path with a worktree change gets `git checkout -- <paths>` (one
     call each, all paths at once);
   - a path in the baseline is never touched, whatever the run did to it;
     `.relay/` and `.relay-scratch/` are never touched;
   - when the first status fails (no repository) the command still runs and
     the cleanup reports `Accounted: false` with empty lists;
   - the `TestRunResult` is returned unchanged: exit code, output and the
     timed-out flag are the inner runner's, so `Classify` sees what it sees today.
2. Bootstrap order: move `GitBootstrapper.EnsureRepositoryAsync` (`:110`) ahead
   of `ResolveTestCommandAsync` (`:96`) so the accounting always has a repository
   to ask. The empty HEAD commit it creates changes nothing else about the tree.
3. `ProjectBootstrapper.CreateValidationRunner(TimeSpan timeout, IGitInvoker
   git, Action<TreeCleanup>? onCleanup)` wraps the `ShellTestRunner` in the
   decorator. `ResolveTestCommandAsync` collects every cleanup and, when any
   path was removed or restored, the result carries a diagnostic the GUI status
   and the CLI print verbatim: `Validation of 'cargo test' left 2 files behind;
   removed: src/snapshots/a.snap.new, src/snapshots/b.snap.new`. The
   create-config path and the placeholder upgrade go through the same factory
   so all four surfaces behave alike.
4. Docs: one sentence in AGENTS.md's `bootstrap` entry (the outcome in
   `/state.statusText` now also names files the validation run created and
   bootstrap removed) and a TROUBLESHOOTING.md entry "Bootstrap says it removed
   files" explaining that a red smoke run may write sidecars and that the
   command in `testCmd` can be given the env vars the project's CI uses.

## Tests

- `TreeRestoringTestRunnerTests` (GitSim root, a scripted inner runner that
  writes files): a new untracked file is removed and named; a pre-existing
  untracked file survives; a tracked file the run modified is restored and
  named; a tracked file already dirty before the run is untouched even though
  the run changed it again; a write under `.relay/` is untouched; an empty
  directory the run created is removed; the inner result passes through
  unchanged (exit 101, output, timed-out); a non-repository root runs the
  command and reports `Accounted: false`.
- `ProjectBootstrapperTests`: `BootstrapAsync_ValidationSideEffects_AreRemovedAndNamed`
  (a fake runner drops a file; afterwards `git status --porcelain` shows only
  `.relay/`, and the result's diagnostic names the file);
  `BootstrapAsync_EnsuresTheRepositoryBeforeValidating` (the fake runner asserts
  `.git` exists when it is invoked); the existing empty-folder placeholder test
  still passes.
- `MainWindowViewModelInitTests`: the create-config path reports the removed
  file in `StatusText`; `MainWindowViewModelTests.Bootstrap`: the bootstrap
  status keeps the config note and adds the cleanup line.
- Every new test uses GitSim; the real-git and side-effects guards stay green.

## Verification (through the control API)

Clone colored as `/Users/admin/Dev/vr-work/colored.prep.md` describes, delete
its `.relay/`, launch the app, then:

    curl -s -X POST -d '{"path":"<clone>"}' http://127.0.0.1:8765/command/open-folder
    curl -s -X POST http://127.0.0.1:8765/command/bootstrap
    curl -s http://127.0.0.1:8765/state | jq -r .statusText
    git -C <clone> status --short

Expected: `statusText` names `cargo test` and the two removed
`src/snapshots/*.snap.new` files; `git status --short` shows nothing but
`?? llm-tasks/` (and `.relay/` if not excluded). Then patch `testCmd` as the
runbook says and run one task to confirm nothing regressed. Put the file
count and the bootstrap wall time in the commit body.

## Out of scope

Sandboxing the smoke run under nono (a fidelity question of its own: the
pipeline wraps the command, bootstrap runs it bare); the Windows box without a
usable distro (its own task, `close-the-windows-entry-point-gaps`); teaching the
detector the env vars a project's CI sets.

## Rejected alternatives

- A throwaway worktree (`PlanningWorktree.CreateAsync` or
  `CreateVerifyWorktreeAsync`). Build output is on
  `BuildOutputOverlaySkipNames` (`RelayDriver.VerifyWorktree.cs:158`), so cargo,
  SwiftPM and dotnet would rebuild from nothing inside a 60-second box, be
  rejected as timed out, and hand the project the placeholder. Worse than today.
- Dry-run flags (`cargo test --no-run`, `pytest --collect-only`): per-toolchain
  knowledge for a weaker proof; a suite that compiles but cannot run would pass.
- `git clean -fd` after the run: deletes the operator's own untracked files.
  The baseline set is what makes the cleanup safe.
