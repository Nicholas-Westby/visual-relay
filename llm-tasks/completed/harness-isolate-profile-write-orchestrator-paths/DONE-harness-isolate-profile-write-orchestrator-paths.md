# Harness: isolate the vr-guard profile write in orchestrator-constructed driver tests

## Problem

The DONE task `harness-isolate-profile-write-in-driver-tests` (commit `037f6be`) stopped
`RelayDriver` integration tests built via `RelayDriverDependencies.ForTests(...)` from writing
the real `~/.config/visual-relay/vr-guard.json`. It explicitly scoped out a residual: tests that
build a `RelayDriver` indirectly, through **production constructors** of the orchestrator types,
are NOT covered by the `ForTests` default and still resolve the **real** profile.

Those tests pass on a host where the on-disk profile already matches the embedded one (so
`NonoProfileEnsurer.EnsureAsync` skips the write), but they FAIL under the `vr-guard` nono
sandbox whenever the embedded profile **diverges** from the on-disk one — i.e. exactly when a
task edits `packaging/nono/vr-guard.json` (e.g. `add-cloak-browser-to-allowed-paths`). The
divergence makes `EnsureAsync` attempt the write, the sandbox denies it, the task flags at
stage 1, and ~20 tests fail as `Expected: … Actual: ReviewNeeded` / `Failed to write the vr-guard
sandbox profile`:

- `NoCommitContaminationTests`
- `RelayQueueControllerTwoPhaseTests`, `RelayQueueControllerDrainTests`
- `DrainQueueToolTests`, `DrainExecutionLoggingTests`
- `PlanningWorktreeConfigCopyTests`, `PlanPhaseRunnerTests`

This blocks VR from verifying ANY task that changes the sandbox profile.

## Current state (researched 2026-06-26 — re-grep every anchor; trust the symbol, not line numbers)

- The seam already exists from `037f6be`: `IEnvironmentAccessor`
  (`src/VisualRelay.Core/Configuration/IEnvironmentAccessor.cs`), `ProcessEnvironmentAccessor`
  (real env), `RelayDriverDependencies.EnvironmentAccessor` (defaults null → real env), the
  internal `TempXdgEnvironmentAccessor` (a shared seeded temp `XDG_CONFIG_HOME`), and the
  `ForTests(...)` default that hands every `ForTests`-built driver a temp-XDG accessor.
  `RelayDriver` already threads `_dependencies.EnvironmentAccessor` into
  `NonoProfileEnsurer.EnsureAsync(accessor, ct)`.
- The GAP is the orchestrator construction path. Re-grep where `RelayDriverDependencies` is
  built WITHOUT `ForTests` inside production code: `PlanPhaseRunner`, `RelayQueueController`,
  and the DrainQueue tool / `ConsoleTaskRunner` (`src/VisualRelay.Core/Execution/…`). They
  construct dependencies with the production default (real env), so their internally-built
  `RelayDriver`s write the real `~/.config`. ~20 integration tests drive `RunTaskAsync` through
  these orchestrators via production constructors and so are not isolated.

## What to build

Thread an `IEnvironmentAccessor` from each orchestrator that constructs `RelayDriverDependencies`
(`PlanPhaseRunner` → `RelayQueueController` → `ConsoleTaskRunner`/DrainQueue tool) into the
dependencies it builds, defaulting on the production path to `ProcessEnvironmentAccessor` (real
env, byte-for-byte unchanged behavior). Give the orchestrators' test-facing factories/ctors an
optional accessor that the ~20 affected tests set to a hermetic temp-XDG accessor (reuse the
`TempXdgEnvironmentAccessor` / `TempXdg` pattern, never a bare/empty accessor — it must SET
`XDG_CONFIG_HOME` or it falls through to the real env). Prefer a single default seam (mirroring
`ForTests`) so the ~20 call sites get isolation without per-site edits where possible. Do NOT
weaken any assertion; the tests must genuinely reach their real scenario now that stage 1 succeeds.

## Tests / validation

- Reproduce first: make the embedded profile diverge from the on-disk one (e.g. delete the real
  `~/.config/visual-relay/vr-guard.json` or edit `packaging/nono/vr-guard.json`), run the
  orchestrator test classes UNDER the sandbox the gate uses
  (`nono run --profile <vr-guard.json path> --allow-cwd -- dotnet test …`), and confirm the ~20
  failures. After the fix, with the profile still diverged, those classes pass.
- Add a guard test: an orchestrator built via its test factory resolves the profile under a temp
  dir, never the real `$HOME/.config`. No test mutates the real `~/.config/visual-relay/vr-guard.json`.
- Production unchanged: with the real accessor, `ResolveProfilePath` still returns
  `${XDG_CONFIG_HOME:-$HOME/.config}/visual-relay/vr-guard.json`.

## Done when

- The full suite under `nono -p vr-guard` has ZERO `Failed to write the vr-guard sandbox profile`
  / `…/.config/visual-relay/vr-guard.json' is denied` / stage-1 `Actual: ReviewNeeded` failures
  across the orchestrator classes above, EVEN when the embedded profile diverges from on-disk.
- No test mutates the real `~/.config`. Production behavior byte-for-byte unchanged.
- Bare suite green; changed files < 300 lines; Conventional Commit subject (e.g.
  `test: isolate vr-guard profile write in orchestrator-constructed driver tests`).

## Coordination

Completes the residual deliberately left by `harness-isolate-profile-write-in-driver-tests`
(`037f6be`). Same mechanism, different construction path. Independent of the UI tasks.
