# Eliminate the Process-HOME Mutation Race That Flakes the NonoWhy Oracle Tests

A host run on 2026-07-09 failed `NonoWhy_ProvisioningProfiles_DeniedWrite` with
`EXPECTED DENIED but was ALLOWED … Reason: granted_path … Granted by:
/private/tmp/nix-shell.qqzQrM`. The probed path was under a fake home
(`$TMPDIR/vr-backend-status/<guid>/Library/…`), not the real one. Root cause is a
test-parallelism race, fully diagnosed; every link below was reproduced.

## Diagnosed root cause (verified, do not re-derive)

- `BackendLifecycleStatusTests.Start_LoadsUserLevelKeys_ButNotRepoRootKeys`
  swaps the **process-wide** `HOME` to its temp home and nulls
  `XDG_CONFIG_HOME` inside try/finally
  (`tests/VisualRelay.Tests/BackendLifecycleStatusTests.cs:222-258`) so that
  `BackendLifecycle.LoadProviderKeys()` → `KeyEnvFile.ResolvePathForCurrentUser()`
  (`src/VisualRelay.Core/Execution/BackendLifecycle.Start.cs:194`) resolves the
  user-level `.env` from the test's temp home. The window spans an async backend
  start — seconds long.
- The `"ProcessEnv"` collection does not protect readers in other collections
  (no `DisableParallelization`; xUnit parallelizes across collections), and
  `NonoWhyOracleTests` reads live `HOME` via `GetFolderPath(UserProfile)` at
  fact time. A fact that lands in the window probes a path under `$TMPDIR`,
  which `packaging/nono/vr-guard.json` allows read+write → nono correctly
  answers ALLOWED → the denied-expectation fact fails.
- Deterministic repro of the verdict flip (nono 0.66.0, the flake pin):
  `HOME=$TMPDIR/x nono why -p packaging/nono/vr-guard.json --op write --path
  "$TMPDIR/x/Library/Developer/Xcode/UserData/Provisioning Profiles/f"` →
  ALLOWED; same probe under the real home → DENIED (insufficient_access).
- Only environments with `nono` on PATH (the nix dev shell) execute the oracle
  facts, so VM gate runs (nono absent, facts skip) never see the flake.
- The `XDG_CONFIG_HOME` null-out is a second face of the same race: a
  concurrent test reading it mid-window sees null and falls back toward the
  real `~/.config` instead of the module-initializer temp dir.

## What to build

The env seam already exists — `KeyEnvFile.ResolvePathForCurrentUser(IEnvironmentAccessor?)`
(`src/VisualRelay.Core/Configuration/KeyEnvFile.cs:51`) and internal
`GetUnsetKeys(string, IEnvironmentAccessor?)` (`KeyEnvFile.cs:231`) — the
backend lifecycle just never threads it. Fix at the seam; no serialization
band-aids.

1. **Tighten the guard first (red).** Remove the `BackendLifecycleStatusTests.cs`
   exemption from `NoTestFile_CallsEnvironmentSetEnvironmentVariable`
   (`tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs:113-116`).
   The guard must fail against the current tree — that failure is this task's
   red state, and it is what prevents regression afterward.
2. **Thread the accessor through `BackendLifecycle`.** Add an optional
   `IEnvironmentAccessor? env = null` constructor parameter (matches the
   existing optional-dependency ctor style, `BackendLifecycle.cs:34-39`);
   `LoadProviderKeys()` passes it to `ResolvePathForCurrentUser(...)` and to
   the unset-keys call (widen `GetUnsetKeysPublic` with the accessor parameter
   in the existing wrapper style, or route through the internal overload).
   Null accessor keeps today's process-env behavior for all product callers.
3. **Rework the test to inject, not mutate.** In
   `Start_LoadsUserLevelKeys_ButNotRepoRootKeys`, delete the HOME/XDG swap and
   pass a `DictionaryEnvironmentAccessor { ["HOME"] = _home }` (authoritative,
   no real-env fallback — `TestDoubles.cs:13`; XDG_CONFIG_HOME absent →
   resolution falls to HOME, replacing the old null-out). Assertions stay
   verbatim: user-level key loads, repo-root key does not.
4. **Dissolve the `ProcessEnv` collection.** It existed to manage this
   mutation, and the flake proved membership-based protection does not work.
   Remove `ProcessEnvCollectionDefinition.cs` and the `[Collection("ProcessEnv")]`
   attributes on `BackendLifecycleStatusTests` and
   `SandboxExtraAllowPathsConfigTests`; update both class doc comments (the
   paragraphs describing the override/race). First verify by enumeration that
   no runtime process-env mutator remains outside the guard's documented
   infrastructure exemptions (module init, app boot, unique-key hermeticity
   facts) — if one exists, stop and flag rather than dissolve.
5. **Prove the guard bites.** House idiom: add a temporary offender calling
   `Environment.SetEnvironmentVariable` in a test file, watch the guard fail,
   remove the offender. Record this in the summary.

## Done when

- `grep -c "SetEnvironmentVariable" tests/VisualRelay.Tests/BackendLifecycleStatusTests.cs`
  is 0; the guard has no exemption for the file and is green.
- `Start_LoadsUserLevelKeys_ButNotRepoRootKeys` passes via the injected
  accessor with the same assertions.
- Full gate green: repo `testCmd`
  (`dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1
  -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 120s
  --blame-hang-dump-type none`) — once on the default PATH, and once inside
  `nix develop` so the NonoWhy oracle facts actually execute (skip count drops
  ~18; this mirrors the environment that flaked).
- `./visual-relay check` green.
- Production callers unchanged: default construction still reads the real
  process environment (no call-site churn outside the lifecycle ctor plumbing).

## Guardrails

- No `xunit.runner.json` changes, no `DisableParallelization`, no sleeps, no
  retry loops — the fix is removing the shared-state mutation, not serializing
  around it.
- No assertion weakening or fact deletion; coverage is preserved verbatim.
- Product change is confined to threading the accessor (`BackendLifecycle`
  ctor + `LoadProviderKeys` + the `KeyEnvFile` public wrapper widening).
- Keep new/edited files inspectcode-clean and under the size guard;
  Conventional Commits.
