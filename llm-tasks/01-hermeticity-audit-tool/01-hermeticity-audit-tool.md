# Build the hermeticity audit tool

A slow-test investigation found the driver test family burning ~90% of its wall
clock asleep: `PlanningWorktree.RunGitAsync` retries the deterministic exit-128
`fatal: not a git repository` failure 3x with 250ms + 1s real-time backoff, and
`EarlyImplementationDetector` spawns a real `git` process because its call sites
omit the injected `IGitInvoker`. The full suite ran at ~17% CPU — sleep-bound,
not work-bound. The repo's guard system (matchers in `tools/VisualRelay.Guards`,
guard-as-tests over `CachedSyntaxTreesFixture`) gives fast pass/fail gates; this
task adds the complementary occasional-use diagnostic: a CLI that EXPLAINS
findings in depth (file:line, snippet, why it matters, suggested direction)
instead of failing a build. Tasks 02-04 use it to confirm they missed no sites.

## Design (locked)

- Analysis rules are pure matchers in `tools/VisualRelay.Guards`, one file per
  rule, each exposing both `FindViolations(IEnumerable<(string Path, string Source)>)`
  and the `(string Path, SyntaxTree Tree)` overload — exactly the
  `RealSleepGuard` / `DeadConfigFieldGuard` shape. No I/O in matchers.
- New thin console project `tools/VisualRelay.Audit` references the Guards
  library, parses `src/`, `tests/`, `tools/` once (skip `bin`/`obj`, mirror
  `CachedSyntaxTreesFixture.IsBuildArtifact`), and renders detailed findings to
  STDOUT: `path:line: <rule-id>: <message>`, then indented explanation +
  suggested-direction lines, then a per-rule summary table. Exit 0 whenever the
  audit ran (it is informational, never a gate); exit 2 on unknown subcommand,
  mirroring the Guards CLI.
- Wire `./visual-relay audit [rule-id ...]` (no args = all rules) in
  `tools/VisualRelay.Cli` exactly like `guards`: a
  `PassthroughCommand.ForwardToTool(paths, "VisualRelay.Audit", args)` entry, a
  `CommandRouter.KnownCommands` row, and a `Program.cs` switch arm. Do NOT touch
  `CheckCommand` — the audit must never run in `./visual-relay check` or tests.

## Rules

1. `retry-delay-loops` — a `for`/`while` body containing both a delay call
   (`Task.Delay`/`Thread.Sleep`) and an invocation whose receiver or arguments
   involve `IGitInvoker`/`RunAsync`/process types. Report the attempt/backoff
   constants found and whether any identifier in the loop suggests failure
   classification (e.g. a `*Classifier` or `isSuccess` reference). This is the
   `RunGitAsync` bug shape: retrying failures that can never succeed.
2. `di-bypass` — a method parameter defaulting to a real collaborator
   (`?? new GitInvoker()`, `?? TimeProvider.System` on an optional param), plus
   every call site of that method which omits the argument while the enclosing
   class holds `RelayDriverDependencies`. This is the
   `EarlyImplementationDetector` bug shape: an injection seam silently bypassed.
3. `real-waits` — every `RealSleepGuard` violation across all three roots, PLUS
   an inventory of every `// vr-allow-sleep:` suppression with its reason, so
   stale exemptions get re-reviewed instead of living forever.
4. `test-side-effects` — test sources referencing real-side-effect constructs:
   `new GitInvoker(`, `Process.Start`, `ProcessStartInfo`,
   `Environment.SetEnvironmentVariable`. Honor `RealSleepGuard`'s
   slow-integration exemption list; explain per finding which hermetic double to
   prefer (`GitSim`, `NullGitInvoker`, `ScriptedTestRunner`,
   `DictionaryEnvironmentAccessor`).

### Steps

1. Author the four matchers in `tools/VisualRelay.Guards` with XML docs stating
   the anti-pattern each detects and why it makes tests slow or unhermetic.
2. Author unit tests in `tests/VisualRelay.Tests` in the `RealSleepGuardTests`
   style: small inline source snippets per matcher, positive and negative cases,
   no fixture needed. Keep them millisecond-fast.
3. Add one smoke test that runs all four matchers over
   `CachedSyntaxTreesFixture.AllTrees` (constructor-injected assembly fixture)
   and asserts they complete without throwing. Assert nothing about counts —
   findings are informational and must not turn the suite red.
4. Build the `VisualRelay.Audit` console (thin `Program.cs` dispatcher on
   rule-id args + one renderer file), then the CLI wiring.
5. Run `./visual-relay audit` and read the output end to end; fix false
   positives it shows on the live tree before finishing.

### Guardrails

- The audit is diagnostic-only: no exit-code failure on findings, no wiring
  into `check`, `test`, or hooks. The guard-as-tests added by tasks 02-04 are
  the enforcement layer; this tool is the troubleshooting lens.
- Matchers stay pure and each new C# file stays under the 300-line guard.
- Do not name the subcommand `inspect` — that is the InspectCode lint gate.

## Done when

`./visual-relay audit` prints a detailed, accurate report for all four rules on
the live tree; each matcher has green unit tests; `./visual-relay check` passes.

## Commit-message evidence

Measure at implementation time and put in the commit body (≤ 3 hyphen bullets,
≤ 20 words each, no file names or paths): per-rule finding counts on the live
tree, and total audit runtime in seconds.
