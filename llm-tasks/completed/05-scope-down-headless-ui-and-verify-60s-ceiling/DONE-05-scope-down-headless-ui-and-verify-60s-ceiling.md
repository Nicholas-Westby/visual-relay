# Scope Down the Headless UI Chain, Guard the Convention, and Enforce the 60 s Suite Ceiling

Final task of the test-suite speed push. It has two jobs: (1) shrink the serialized
Avalonia UI chain — after the earlier queued tasks, the remaining critical path — and
(2) act as the acceptance gate for the whole effort: **the full suite must complete in
under 60 seconds on the host — hard ceiling — across three consecutive runs. 45
seconds is the aspirational target to pursue within this task's scope.**

Hard constraint (do not fight it): all Avalonia headless UI tests share ONE
process-global app/dispatcher, so every `[AvaloniaFact]` class carries
`[Collection("Headless")]` (`tests/VisualRelay.Tests/HeadlessCollectionDefinition.cs`)
and the whole collection runs strictly serially. Guards enforce membership
(`SplitGuardVerificationTests.Headless.cs`:
`HeadlessTestClasses_AllCarryHeadlessCollectionAttribute`,
`HeadlessTestClasses_MustNotContainPlainFactOrTheory`) and `BannedSymbols.txt` bans
hand-rolled headless sessions. Do NOT attempt to parallelize UI tests, add dispatchers,
or split test assemblies — the chain gets fast by each link getting cheap.

## Measured baseline (2026-07-08, host Mac — re-measure first; earlier tasks will have
shifted it)

Headless-only run (`dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj
--no-build --logger trx --filter "<OR-chain of FullyQualifiedName~ for every Headless
class>"`): 309 facts, 25.7 s serial chain. Composition: median fact 101 ms (healthy);
`ControlApiConfirmGatedTests` 8.7 s + `RewriteMutualExclusionTests` 8.7 s (six ~2.9 s
rewrite facts — a separate queued task fixes their real-git-worktree cost; expect these
gone), then `ActivityColumnTabsUiTests` 1.4 s, `SettingsPanelUiTests` 1.0 s, and a
long tail of 0.2–0.5 s classes, many of which boot a full 900×900 `MainWindow` + real
`MainWindowViewModel` + `LoadInitialAsync` to assert on one panel.

## What to build

1. **Re-measure and rank.** Regenerate the headless-classes list
   (`grep -rl '\[Collection("Headless")\]' tests/VisualRelay.Tests --include="*.cs"`),
   run the headless-only filter, rank classes by summed duration. Work list = every
   class over 0.5 s summed. Record the ranked table before and after.
2. **Scope down (preferred remedy).** Where assertions are panel-local, construct just
   the control/panel plus the minimal view-model slice it binds to — the cheap pattern
   already in-tree (see `ChevronAffordanceRenderTests`); no `MainWindow`, no
   `LoadInitialAsync`. Keep `[AvaloniaFact]` + the Headless collection.
3. **Share the boot (fallback remedy).** For facts genuinely about whole-app wiring
   (initial-load flows, cross-panel interaction, dialog plumbing), boot ONE window per
   class via an xUnit class fixture, constructed the same way current tests construct
   windows (marshaled onto the headless dispatcher — xUnit builds fixtures off that
   thread). Fixture-sharing contract: every fact leaves the window in the state it
   found it; a fact that cannot cheaply restore state gets scoped down instead, or
   stays unshared with a comment saying why.
4. **Delete static-pin UI facts.** A UI fact that only pins a fixed visual constant
   (literal label text, hardcoded dimension) with no binding, converter, or layout
   behavior involved is deleted, not scoped down. Conservative: when in doubt, keep.
   List every deletion in the summary.
5. **Install the convention + guard.** Document in `AGENTS.md`'s testing notes: *new
   UI tests instantiate the specific panel under test, not the whole app, unless the
   test is explicitly about whole-app wiring.* Enforce with a guard in the house
   guard-as-test idiom (see `SplitGuardVerificationTests.Headless.cs` for the
   enumeration pattern): flag `new MainWindow` in `tests/VisualRelay.Tests/**` outside
   an explicit, commented allowlist of justified whole-app classes. Prove it bites
   with a temporary offender, then remove the offender.
6. **Final acceptance for the speed push.** Run the repo's exact test gate three
   consecutive times on the host:
   `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none`
   (the `testCmd` in `.relay/config.json`). All three runs green. The test-phase
   `Duration:` must be under **60 s in every run — that is the hard ceiling**; under
   **45 s is the aspirational target**. Record all three durations, plus the headless
   chain before/after, in the summary.
   - Over 45 s: take a full-run trx, identify the top wall-clock classes, and keep
     applying THIS task's remedies (scope-down, fixture-sharing, static-pin deletion)
     to UI classes while they are the ones on top.
   - Still at or over 60 s with the UI remedies exhausted, or blocked by a
     non-headless family: do not chase it here — flag the task with the measured
     per-class breakdown so the follow-up is scoped from data. The 60 s ceiling is a
     hard requirement: missing it means the task flags; it never ships as "close
     enough".
   - Between 45 and 60 s with UI remedies exhausted: ship green (do not flag), and
     list the remaining top classes with measured costs in the summary as candidates
     for a follow-up task.

## Done when

- No Headless class exceeds 1 s summed in the headless-only run, and the whole chain
  is at or under ~8 s.
- Guard: green live, bites on out-of-allowlist `new MainWindow`, allowlist entries
  each carry a one-line justification.
- Scoped-down facts keep their assertions verbatim (construction changes only);
  deleted facts are enumerated with justifications.
- Full-gate acceptance: three consecutive green runs on the host, each with
  test-phase `Duration:` under 60 s (hard ceiling) — or the task is flagged with the
  measured breakdown showing the out-of-scope blocker. Whether the 45 s aspirational
  target was met is recorded either way.
- `./visual-relay check` passes.

## Guardrails

- Everything stays inside the `Headless` collection and the existing dispatcher model;
  no `xunit.runner.json` changes, no new test assemblies, no parallelization attempts.
- No assertion weakening in scope-downs or fixture conversions — same facts, same
  coverage; the diff is construction scoping, fixtures, the convention text, the
  guard, and the enumerated deletions.
- No skipping to hit the target: a slow UI fact gets scoped down, shared, or (if a
  static pin) deleted — never gated or skipped.
- With shared fixtures, a new flake in the class means a state-reset gap: fix the
  reset, don't revert the sharing blindly.
- Production code untouched. Conventional Commits; files under the size guard.
