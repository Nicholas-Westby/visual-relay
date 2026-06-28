# Harness: revert the split-build verify back to single-phase (keep the unrelated fixes)

## Why
The `harness-verify-worktree-build-warm` change (commit `97eb26b`) split verify into an
untimed `buildCmd` phase + a `testCmd --no-build` phase. It never actually **warmed** the
build — the verify worktree still discards `bin`/`obj`, so it is the same cold full rebuild;
the split only moved the build out of the timed window. For that marginal gain it added
moving parts and bugs to the most critical, fragile path (the verify gate). Revert to the
simpler, more robust single-phase verify with a generous timeout.

## Desired END STATE (frame the work by this, not by a blind `git revert`)
- Verify runs ONE command: `testCmd` = a normal `dotnet test` that builds **and** tests
  (NO `--no-build`). No separate `buildCmd`. No `buildTimeoutMs`.
- `.relay/config.json`: remove `buildCmd`; set `testCmd` back to a build+test command
  (`dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 60s --blame-hang-dump-type none`);
  remove `buildTimeoutMs`; raise `testTimeoutMs` back to `1200000` (20 min) so the cold
  worktree build fits in the timed window (the documented band-aid, commit `04a2c20`).
- The fix-verify agent's verify command is the plain `testCmd` again (it builds+tests, so its
  self-check is not stale).

## REMOVE (the split feature + its dependents)
- `97eb26b`: `RunBuildCommandAsync` / the build-phase call in
  `src/VisualRelay.Core/Execution/RelayDriver.Bootstrap.cs`; the `BuildCommand` +
  `BuildTimeoutMilliseconds` fields in `src/VisualRelay.Domain/RelayConfig.cs`; their parsing
  in `RelayConfigLoader.cs`; delete `tests/VisualRelay.Tests/VerifyBuildWarmTests.cs`.
- `7b1f86c` (entirely split-dependent): the explicit-hard-cap `RunAsync` overload in
  `src/VisualRelay.Core/Execution/Interfaces.cs` + its `SandboxedTestRunner` impl + its use in
  `RunBuildCommandAsync`; delete `tests/VisualRelay.Tests/BuildTimeoutThreadingTests.cs`.
  (Confirm nothing outside the build phase uses the overload before removing it.)
- From `72cee87`, ONLY the split-related part **D**: the `AgentFixVerifyCommand` helper that
  returns `buildCmd && testCmd` for the Fix-verify agent (`RelayDriver.VerifyFix.cs`) — restore
  it to hand the plain `config.TestCommand`. Update the matching assertions in
  `VerifyAgentCommandTests.cs`.
- Delete the now-moot deferred task dir
  `llm-tasks/harness-verify-worktree-build-truly-warm/` (it only exists to finish warming the
  split build).

## KEEP (independent improvements from `72cee87` — do NOT revert these)
- **B**: the read-only Verify (stage 9) agent is NOT handed an imperative full-suite "run this
  exact command" (`RelayDriver.cs`); it gets the captured `## Verify output`. Keep + its tests.
- **G**: `## Verify output` rendered via `TrimForTail` (keeps the pass/fail summary) in
  `ProcessRunners.Helpers.cs`. Keep.
- **H**: the saturating-`long` boosted-ceiling/turns math in `RelayDriver.VerifyFix.cs`
  (overflow-safe). Keep + `WatchdogCeilingOverflowTests.cs`.
- Everything from the other 9 task commits is unrelated — leave it alone.

## Done when
- Verify is single-phase (`testCmd` builds+tests; no `buildCmd`/`buildTimeoutMs`); `testTimeoutMs`=1200000.
- The full suite runs GREEN to completion under nono (build+test in one `dotnet test`), validated
  by exit code + stored output, no blame-hang abort.
- The B/G/H behaviors and their tests still pass; no dangling references to removed members.
- `./visual-relay check` green; Conventional Commit. General-purpose: a target repo with no
  build step is unaffected (single `testCmd` is the default model again).
