# Run driver happy paths on a committed GitSim

The shared happy-path helpers (`RelayDriverTestHelpers.RunHappyPath` and its
duplicate in `RelayDriverResumeTestHelpers`) drive full 12-stage runs against
`DepsFor`'s UNREGISTERED `GitSim`, so every git probe answers
`fatal: not a git repository`. The driver therefore never exercises its real
machinery in these tests: stage 5's `WorktreeFilter`/red-gate stash short-circuit
and stages 9/10 fall back to the in-place gate instead of the isolated verify
worktree. Task 02 removed the 1.25s retry sleep on that path, but the fidelity
gap remains — the suite's core happy paths test the fallback, not the machinery.
With a registered, committed sim the full 12-stage run measured ~116ms and the
worktree overlay, dirty-set delta, and cleanup actually execute. The pattern is
already proven: `RelayDriverGitCommitTests` and `TaskCompletionArchiveNoBatchTests`
run committed sims to `Committed`, and `RedGateObservingTestRunner` is already
verify-snapshot-aware.

## Prescribed approach

Convert only the happy-path helpers. `DepsFor` keeps its documented
unregistered contract — the commit-gate resume tests
(`RelayDriverResumeCommitGateTests`, `RelayDriverResumeCommitGateVerifyTests`)
intentionally rely on the in-place fallback evaluating at `repo.Root`, and the
fallback path must itself stay covered.

### Steps

1. Move `InitTestRepo` (InitSim + seed `.gitignore` with `.relay/*` + commit)
   from `RelayDriverResumeTestHelpers` to `RelayDriverTestHelpers`, keeping the
   name and behavior. Update its existing consumers.
2. Consolidate the two duplicate `RunHappyPath` helpers into ONE on
   `RelayDriverTestHelpers` with the signature
   `RunHappyPath(TestRepository repo, GitSimEngine sim, string taskId)`, built
   on `RelayDriverDependencies.ForTests(runner, testRunner, sink, sim)` and
   `RelayDriverOptions.NoGitCommit`, asserting `Committed` as today. Delete the
   copy in `RelayDriverResumeTestHelpers`.
3. Update all call sites to create the sim once per test
   (`var sim = RelayDriverTestHelpers.InitTestRepo(repo);`) and pass it to every
   `RunHappyPath` call on that repo: `RelayDriverRerunTests` (3 calls),
   `RelayDriverResumeTests.RunTaskAsync_NormalRerun_StartsFromStage1` (2),
   `RelayDriverResumeReAddTests` (2), `RelayDriverResumeReAdd2Tests` (1),
   `RelayDriverNonResumeStaleStateTests` (2). Init once — never re-seed or
   re-commit between runs on the same root (re-init is a registry no-op, but a
   second seed commit would be noise).
4. Add the fidelity regression test, new file
   `RelayDriverVerifyIsolationTests.cs`: committed sim + a
   `RecordingTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green"))`
   + `ArtifactWritingSubagentRunner` happy path. Assert exactly two runner
   calls; call 1's root path equals `repo.Root` (stage-5 author gate runs
   in place by design); call 2's root path differs from `repo.Root`, contains
   the `visual-relay` worktree temp segment `/visual-relay/wt/`, and ends with
   `-verify-s10-a1`. This pins the isolated-verify machinery ON: if fixtures
   ever regress to the unregistered fallback, this fails loudly instead of the
   suite silently testing the wrong path.
5. Re-verify the converted tests' assertions still hold — they only inspect
   `.relay/<taskId>/` artifacts and seal counts. Seal `treeHash` is computed
   from manifest file contents, not git, and with `NoGitCommit` the snapshot
   writers and the whole stage-12 git block stay skipped, so no new files or
   seal changes are expected.
6. Run `./visual-relay audit test-side-effects` (task 01) and confirm the
   conversion introduced no real-process usage.

### Guardrails

- Do NOT modify `DepsFor` or its doc contract; do not convert commit-gate,
  flag-path, or fallback-behavior tests — the in-place fallback is product
  behavior for non-git roots and keeps its own coverage.
- Do NOT flip `CreateGitCommit` anywhere; happy-path helpers stay `NoGitCommit`.
- Keep the `.gitignore` seed with `.relay/*` in `InitTestRepo` — without it the
  worktree overlay and stage-5 filter would see `.relay/` run artifacts as
  untracked content.

## Done when

Full suite green; the new isolation test passes and fails if the sim argument
is swapped back to an unregistered `GitSimEngine`; converted tests re-measured
(see evidence sheet).

## Commit-message evidence

Measure at implementation time per the attached evidence sheet; numbers go in
the commit body, never in this file.
