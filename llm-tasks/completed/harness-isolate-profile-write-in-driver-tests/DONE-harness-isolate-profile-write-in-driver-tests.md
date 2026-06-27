# Harness: isolate the vr-guard profile write in RelayDriver integration tests (stop writing real ~/.config)

## Problem

The fix-verify / stage-9 gate runs the project's test suite **inside the `vr-guard`
nono sandbox** (`nono run -p vr-guard --allow-cwd -- dotnet test …`). When VR's own
suite runs that way, **135 of ~165 failures** share a single root cause:

`RelayDriver.RunTaskAsync` calls `NonoProfileEnsurer.EnsureAsync()` **with no
environment accessor**, so it resolves the profile path to the **real**
`~/.config/visual-relay/vr-guard.json` and writes it. Under the sandbox that write is
denied:

```
System.InvalidOperationException : Failed to write the vr-guard sandbox profile to
'/Users/<you>/.config/visual-relay/vr-guard.json'. VR will not run a sandboxed stage
with a missing or stale profile. (Access to the path '…' is denied.)
---- System.UnauthorizedAccessException : Access to the path
'/Users/<you>/.config/visual-relay/vr-guard.json' is denied.
```

The task flags at **stage 1**, so every integration test that runs a real
`RunTaskAsync` fails — as `Expected: Committed, Actual: Flagged` (101), `Failed to
write the vr-guard sandbox profile` (33), or a downstream assertion that never
reached its scenario (concurrency peak 0, missing flag-reason substring, resume
commit absent, …).

Just as bad on a **bare host**: these tests only "pass" by **overwriting your real
`~/.config/visual-relay/vr-guard.json` on every run** — a latent hygiene bug.
Isolating the write fixes both.

> This is NOT a keychain/credential problem — that "keychain access required" headline
> is nono advisory noise mis-surfaced as the failure reason (separate task
> `harness-verify-reason-ignore-nono-advisory`). And it is NOT an `XDG_CONFIG_HOME`
> leak — the denied path is consistently `$HOME/.config`, i.e. `XDG_CONFIG_HOME` is
> unset for these runs and the profile resolves through `HOME`.

## Goal

`RelayDriver` integration tests must cause the vr-guard profile write to land in a
**test-controlled temp directory** (sandbox-writable), never the real `~/.config`.
**Production behavior is byte-for-byte unchanged** (real env → real `~/.config`).
After this, the 135 profile-write failures pass under nono, and the stage-1 cascade
failures (tests that flagged before reaching their assertion) pass too.

## Current state (researched 2026-06-26 — re-grep every anchor before editing; trust the symbol, not any line number)

- **The seam already exists and is the house idiom**, established by the DONE task
  `harness-inject-seams-not-global-statics` (it deleted the old
  `KeyEnvFile.EnvironmentAccessorOverride` static in favor of passing an accessor):
  - `IEnvironmentAccessor` — `src/VisualRelay.Core/Configuration/IEnvironmentAccessor.cs`.
  - Real impl `ProcessEnvironmentAccessor` (re-grep its exact name/location) and the
    test double `DictionaryEnvironmentAccessor` — `tests/VisualRelay.Tests/TestDoubles.cs`.
  - Profile resolution already accepts an accessor:
    `NonoProfileEnsurer.EnsureAsync(IEnvironmentAccessor? accessor = null, CancellationToken …)`
    and `NonoProfileEnsurer.ResolveProfilePath(IEnvironmentAccessor? accessor = null)`
    (`src/VisualRelay.Core/Execution/NonoProfileEnsurer.cs`) →
    `XdgConfig.ResolveConfigDir(accessor)` → `KeyEnvFile.GetEnv("XDG_CONFIG_HOME"/"HOME", accessor)`.
- **The gap is the one call site.** In `src/VisualRelay.Core/Execution/RelayDriver.cs`
  (re-grep `EnsureAsync(`) the driver calls
  `await NonoProfileEnsurer.EnsureAsync(cancellationToken: cancellationToken)` — **no
  accessor** — so it always reads the real process env and writes real `~/.config`.
- **`RelayDriver` is built from `RelayDriverDependencies`**
  (`src/VisualRelay.Core/Execution/RelayDriverDependencies.cs`), a
  `sealed record(ISubagentRunner, ITestRunner, IRelayEventSink, IGitInvoker)` with a
  `ForTests(...)` factory. Tests do
  `new RelayDriver(RelayDriverDependencies.ForTests(subagent, testRunner, eventSink), options)`.
  There is **no shared base class**; ~143 `RunTaskAsync` call sites across ~45 files.
- **A temp-XDG helper already exists**: `TempXdg` in
  `tests/VisualRelay.Tests/NonoProfileEnsurerTests.cs` builds a
  `DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = <unique temp> }` and cleans up
  on dispose. Promote/reuse it.
- **Gotcha — the accessor must SET the key.** `KeyEnvFile.GetEnv(name, accessor)` is
  `accessor?.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name)` —
  a key the accessor does not define **falls through to the real env**. So the injected
  test accessor MUST set `XDG_CONFIG_HOME` to the temp dir (TempXdg already does); a
  bare/empty accessor would still leak to the real `~/.config`.

## What to build

1. **Thread an `IEnvironmentAccessor` from the driver into the profile write**
   (production default = real env):
   - Add an `IEnvironmentAccessor` to `RelayDriverDependencies` (beside `IGitInvoker`),
     defaulting on the production path to `ProcessEnvironmentAccessor` (real env).
     `ForTests(...)` gains an optional `IEnvironmentAccessor? environmentAccessor = null`.
   - `RelayDriver` reads `_dependencies.EnvironmentAccessor` and passes it to the
     `NonoProfileEnsurer.EnsureAsync(accessor, ct)` call site.
   - Production behavior must be unchanged (real env → real `~/.config`).
2. **Make every integration test's profile write land in a temp dir without editing
   143 call sites individually.** Preferred: have `RelayDriverDependencies.ForTests`
   **default** its accessor to a hermetic temp-XDG accessor (a reusable `TempXdg`-style
   fixture with a unique per-test temp dir + cleanup), so all current `ForTests(...)`
   callers get isolation for free. The few tests that assert on the profile path itself
   (e.g. `SwivalProfileSessionPinningTests`, which edits the profile mid-run) pass their
   own temp accessor and assert against the temp path.
3. Do **not** weaken any assertion to pass — the tasks must genuinely reach
   `Committed`/their real scenario now that stage 1 succeeds. This is a wiring change,
   not a behavior change.

## Tests / validation

- Validate **under the sandbox the gate uses**: `nono run -p vr-guard --allow-cwd --
  dotnet test …` (nono lives at the nix-store path; the gate runs exactly this form).
  The 135 profile-write failures and the stage-1 cascade must pass.
- **Guard against real-home writes**: add a test that fails if a `RelayDriver` built via
  `ForTests` resolves the profile under the real `$HOME/.config` (it must resolve under
  the test temp dir). No test may touch the real `~/.config/visual-relay/vr-guard.json`.
- **Production unchanged**: with the real accessor, `ResolveProfilePath` still returns
  `${XDG_CONFIG_HOME:-$HOME/.config}/visual-relay/vr-guard.json`.

## Done when

- The `EnsureAsync` call in `RelayDriver.RunTaskAsync` receives a test-controllable
  accessor; production defaults to real env.
- The **full suite under `nono -p vr-guard`** produces no `Failed to write the vr-guard
  sandbox profile` / `Access to the path '…/.config/visual-relay/vr-guard.json' is
  denied` failures, and the `Expected: Committed/Planned, Actual: Flagged` cascade from
  the stage-1 profile write is gone.
- No test mutates the real `~/.config`.
- Production behavior unchanged; `./visual-relay check` green; changed files < 300 lines;
  Conventional Commit subject (e.g. `test: isolate vr-guard profile write in driver tests`).

## Coordination / residual (implementer reads one task at a time — this matters)

This fix clears the **135 profile-write failures plus the stage-1 cascade** (most of the
other ~25). Two things are explicitly **out of scope** and tracked separately, so do not
gate this task on a 100%-green suite:

- The 5 `Installer5DocsTests.Readme_*` failures (stale README assertions after the brew
  section was removed) → **`harness-fix-stale-readme-brew-tests`**.
- The misleading "keychain" verify reason → **`harness-verify-reason-ignore-nono-advisory`**.

After landing this, **re-run the full suite under nono** and triage whatever remains;
expect only the README set plus possibly a few genuine flaky/baseline failures. This
task's success is scoped to the profile-write bucket.
