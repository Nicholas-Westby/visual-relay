# Split the Slow Integration-Test Classes So They Parallelize

xUnit's unit of parallelism here is the test class: classes run concurrently, but facts inside
one class run strictly one-at-a-time. A handful of git-integration classes bundle dozens of
slow end-to-end facts — each spins up its own throwaway `TestRepository` (real temp git repo,
real hooks, fake-swival bash children) and runs `RunTaskAsync`-style pipelines — so those
classes form 30–60-second single-file serial chains. The facts share nothing (per-test repos;
~800 `TestRepository.Create` call sites repo-wide), so the class boundary is the only
serializer. Splitting the classes removes the bottleneck without touching what any test does.

Measured chains (summed per-class duration from `test-logs/*.trx`, 2026-06-17 Mac /
2026-06-23 VM runs — **re-measure from the newest trx first**, the suite has grown since):

| Class | Chain | Notes |
|---|---|---|
| RelayDriverVerifyFixTests | ~56s | end-to-end verify/fix pipelines |
| GitCommitTests | ~50s | |
| GitCommitRetirementTests | ~49s | |
| GitCommitterTests | 37–58s | |
| GitCommitterAutoIncludeTests | 30–45s | |
| WorktreeFilterTests | 24–43s | |
| RelayDriverTests | ~41s | |
| NoCommitContaminationTests | 22–35s | only 3 facts, each runs two full pipelines |
| RelayDriverResumeTests | ~32s | |

**Out of scope:** anything in `[Collection("Watchdog")]` and the timing-sensitive
`ActivityWatchdogSocketWedgeTests*` — those are deliberately serialized (contention would skew
their timing asserts) and are owned by the separate pending virtualization work. Do not split
or move them here.

## What to build

1. **Re-measure and pick targets.** From the newest full-suite `.trx` in `test-logs/`, list
   every non-Watchdog class whose summed duration exceeds ~20s. That's the work list (expect
   roughly the table above).
2. **Split along conceptual seams, sized by the math.** The goal is no serial chain over
   ~15–20s. For a 56s class that means 3–4 sibling classes; a 30s class needs only 2. Split by
   scenario family, not by arbitrary halves — e.g. a driver test class might divide into
   verify-pass paths / verify-fail-then-fix paths / escalation-and-timeout paths; a committer
   class into message-formatting / retirement / include-rules. Two mechanical starting points:
   - Many of these classes already span multiple **partial files** (the 300-line guard drives
     that layout): where a partial file is already a coherent scenario family, promote it to
     its own independent class.
   - Where one file still holds a >20s mix, split further by family. More, smaller classes are
     fine (ten 6-second classes beat one 60-second class) as long as each class name states a
     real concept — never `FooTests2`.
3. **Handle shared partial-class state correctly.** Partial files share fields and private
   helpers; promotion breaks that. Hoist shared helpers into a small common base class or an
   internal static helper in its own file — and while doing so, **verify the facts are truly
   independent**: no shared mutable fields, no `[ClassFixture]`, no static state, no
   order-dependence between facts that previously ran sequentially. Any fact pair that genuinely
   depends on shared state stays together in one class (and gets a comment saying why).
4. **Keep file/class naming aligned with tooling.** One class per file, file named after the
   class (the existing `GitCommitterAutoIncludeTests` suffix style is the precedent). Check
   `tools/dotnet-test-files.sh`'s filter derivation and confirm renamed files/classes still
   resolve to working `--filter` expressions.
5. **Update convention guards legitimately.** The suite self-polices (see
   `SplitGuardVerificationTests` and its baselines/conventions). Run it; update baselines to
   reflect the new class inventory — never weaken a guard's assertion to make it pass.
6. **Prove the win.** Before/after evidence from `.trx`: per-class chain times for every split
   class, and full-suite wall time. Three consecutive clean full-suite runs (split classes now
   run concurrently — this is exactly where hidden shared-state bugs would surface as flakes;
   a flake here means item 3 was missed, not that the split should be reverted blindly).

## Done when

- No class outside the Watchdog collection has a summed chain over ~20s in a fresh `.trx`.
- Full-suite wall time is measurably lower (record before/after numbers in the summary).
- Zero test-behavior changes: same facts, same assertions, same per-test isolation — the diff
  is class/file organization, shared-helper hoisting, and guard baselines only.
- 3 consecutive full-suite runs green; `./visual-relay check` passes.

## Coordination with pending sibling tasks

- `speed-up-test-suite-parallelization` (xunit.runner.json oversubscription) multiplies this
  task's benefit; whichever lands second should re-measure and report the combined wall time.
- `virtualize-watchdog-test-waits` owns the Watchdog collection — hands off entirely here.

## Guardrails

- Do not change test logic, assertions, timeouts, or delete/skip any fact; do not touch
  production code.
- Do not move anything into or out of `[Collection("Watchdog")]`.
- Class names must describe scenario families; file name matches class name; files stay under
  the 300-line guard.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs beyond the
  mechanical moves.
