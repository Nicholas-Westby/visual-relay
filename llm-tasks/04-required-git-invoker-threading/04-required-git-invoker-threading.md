# Make git invoker threading compiler-enforced in the execution layer

`EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync` defaults its
optional parameter to `gitInvoker ?? new GitInvoker()`, and BOTH driver call
sites omit the argument (the stage-4 site in RelayDriver.cs and the stage-5
recheck in RelayDriver.Stage5.cs). Result: every driver run spawns a real
`git rev-parse` process twice (~50-60ms each in the test VM) even in tests that
are nominally git-free — the exact hole the earlier "NullGitInvoker by default"
hermeticity commit tried to close. The root cause is the pattern, not the one
method: an optional parameter defaulting to a real process invoker makes
omission invisible. Remove the option; let the compiler enforce threading.

## Prescribed approach

Within src/VisualRelay.Core/Execution the `IGitInvoker` parameter becomes
REQUIRED everywhere. Composition boundaries outside the execution layer
(src/VisualRelay.Core/Init, the CLI, the App) keep constructing the real
`GitInvoker` and passing it down — production behavior is unchanged.

### Steps

1. Fix the bug: pass `_dependencies.GitInvoker` at both
   `EarlyImplementationDetector` call sites (RelayDriver.cs stage-4 block,
   `RecheckEarlyImplementationAsync` in RelayDriver.Stage5.cs).
2. Make the parameter required (drop `? = null` and the `?? new GitInvoker()`
   fallback) across the execution layer: `EarlyImplementationDetector`
   (reorder it before the optional `isTestFile`), `RedGate` (all overloads),
   `AuthorTestGate.RunAsync`, `WorktreeFilter.DiscardNonTestEditsAsync`,
   `GitCommitter` public entry points, and
   `PlanningWorktree.CreateAsync/RemoveAsync/PruneLeftoversAsync/PruneTaskLeftoversAsync`.
   Follow the compiler errors: driver-internal callers already pass
   `_dependencies.GitInvoker`; any caller outside the execution layer that
   relied on a default now constructs `new GitInvoker()` explicitly at its
   composition boundary and threads it through.
3. Add the guard: `RealGitFallbackGuard` matcher in tools/VisualRelay.Guards
   (both `(Path, Source)` and `(Path, SyntaxTree)` overloads) flagging, for
   files under src/VisualRelay.Core/Execution: any `new GitInvoker(` object
   creation, and any `IGitInvoker` parameter that declares a default value.
   Allowlist: empty — after step 2 there must be zero of either. Add
   `RealGitFallbackGuardTests`: inline-snippet unit tests for the matcher plus
   a live-tree test consuming the constructor-injected
   `CachedSyntaxTreesFixture` filtered to `src/`, in the exact
   `DeadConfigFieldGuardTests` shape (one parse for the whole assembly — keep
   the meta-test millisecond-fast).
4. Add the behavioral regression test: introduce a small `RecordingGitInvoker`
   test double (wraps an inner `IGitInvoker`, records argument vectors) if none
   exists, wrap a `GitSimEngine`, inject via
   `RelayDriverDependencies.ForTests(..., gitInvoker: recorder)`, drive one
   happy-path run, and assert the recorded calls include
   `rev-parse --is-inside-work-tree` — proving the stage-4 early-implementation
   probe flows through the INJECTED invoker rather than a private real one.
5. Confirm with `./visual-relay audit di-bypass` (task 01): the execution layer
   must report zero optional-collaborator defaults afterward.

### Guardrails

- Do NOT touch src/VisualRelay.Core/Init — its `gitInvoker ?? new GitInvoker()`
  defaults are genuine composition-boundary conveniences for CLI-driven
  bootstrap, out of this task's scope and excluded from the guard.
- Production wiring must not change: the App/CLI still use the real invoker;
  this task only makes the threading explicit.
- The guard stays a guard-as-test; do not add it to the `check` gate or the
  standalone guards CLI subcommand list.

## Done when

Both call sites thread the injected invoker; the execution layer compiles with
required `IGitInvoker` parameters everywhere; `RealGitFallbackGuard` is green
with an empty allowlist; the recording-invoker test passes; full suite green.

## Commit-message evidence

Measure at implementation time and put in the commit body (≤ 3 hyphen bullets,
≤ 20 words each, no file names or paths): real git process spawns per driver
happy-path run before vs after (count via the recording test or dtrace), and
the guard's violation count on the pre-fix tree.
