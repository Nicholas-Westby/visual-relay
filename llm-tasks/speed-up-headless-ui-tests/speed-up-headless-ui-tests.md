# Speed Up the Headless UI Tests — Stop Booting the Whole App Per Test

All UI tests live in the `Headless` xUnit collection (`HeadlessCollectionDefinition.cs`),
serialized behind one process-global Avalonia dispatcher — a hard constraint of headless
Avalonia (do NOT attempt multiple dispatchers or parallelizing this collection). Serialization
means the collection's total time is a single-file chain, and thread oversubscription elsewhere
in the suite does nothing for it — as the rest of the suite gets faster, this chain becomes the
critical path.

The chain is expensive because many tests boot far more app than they assert on. Measured
(2026-06/07 `test-logs/*.trx`; 14 classes summed to ~52s in June and the collection has since
grown to ~51 classes — **re-measure from the newest trx first**): the worst single offender,
`KeySetupPanelUiTests`, spends ~22s running 7 facts, each constructing a full 1440×900
`MainWindow` + real `MainWindowViewModel` + `LoadInitialAsync` + opening the Settings dialog —
to assert on one panel. Meanwhile lightweight control-level render tests (the
`ChevronAffordanceRenderTests` style) cost milliseconds — the cheap pattern already exists
in-tree.

This is a **narrow-strikes task, not a 51-class refactor**: fix the measured worst offenders,
and install a convention so new tests don't regrow the problem.

## What to build

1. **Measure and pick targets.** From the newest full-suite `.trx`, rank Headless-collection
   classes by summed duration. Work list = the top offenders — every class over ~3s, expected
   to be roughly the top 8–10 classes covering most of the chain. Leave the long tail alone.
2. **Per class, apply the cheaper of two remedies:**
   - **Scope down (preferred).** If the assertions are panel-local, construct just the panel /
     control plus the minimal view-model slice it binds to, following the existing lightweight
     render-test pattern. No `MainWindow`, no `LoadInitialAsync`.
   - **Share the expensive resource.** If the facts genuinely need whole-app wiring
     (initial-load flows, cross-panel interaction, dialog plumbing), boot **one** window per
     class via an xUnit class fixture and reuse it across facts. Requirements: construct UI on
     the headless dispatcher thread exactly the way current tests do (inspect how the existing
     Headless tests create windows and mirror it in fixture init — xUnit constructs fixtures
     off that thread, so marshal via the established dispatcher helpers); and each fact must
     leave the window in the state it found it — reset any panel state, selection, or dialog
     the fact touched. A fact that can't cheaply restore state is a scope-down candidate
     instead, or stays unshared with a comment saying why.
3. **Install the going-forward rule.** Document the convention where test conventions live
   (e.g. the testing notes in `AGENTS.md`): *new UI tests instantiate the specific panel under
   test, not the whole app, unless the test is explicitly about whole-app wiring.* Then
   enforce it in the repo's established guard idiom (see how `SplitGuardVerificationTests`
   polices conventions): a guard that flags full-app construction (e.g. `new MainWindow`)
   in test files outside an explicit, commented allowlist of justified app-level integration
   classes — so the next full-app-per-test class fails the guard by default instead of quietly
   re-growing the chain.
4. **Prove the win and the safety.** Before/after per-class times and the collection's summed
   chain from `.trx`; 3 consecutive clean full-suite runs — with shared fixtures, a flake here
   means a state-reset gap in item 2, which must be fixed rather than the sharing reverted
   blindly.

## Done when

- No Headless class exceeds ~5s summed, and the collection's total chain is reduced by at
  least ~40% versus the fresh baseline (record both numbers in the summary).
- Zero assertion/behavior changes — same facts, same coverage; the diff is construction
  scoping, fixtures, the documented convention, and the guard.
- The guard demonstrably fails on an out-of-allowlist full-app boot (prove during development
  with a temporary offender, then remove it — mention the check in the summary).
- 3 consecutive full-suite runs green; `./visual-relay check` passes.

## Guardrails

- Everything stays inside the `Headless` collection; do not touch the dispatcher setup or try
  to parallelize UI tests.
- Do not weaken or delete any assertion; do not skip tests to hit the time target.
- Shared-fixture classes document their state-reset contract at the fixture; facts that mutate
  global app state without restoring it don't get to share.
- No production-code changes; Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`);
  files under the 300-line guard.
