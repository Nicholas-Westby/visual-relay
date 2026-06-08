# Run the Fix-verify stage on a verify failure (verify -> fix-verify retry loop)

Stage 10 ("Fix-verify", defined in `RelayStages.cs` as "Fix failures from the
pinned suite") is effectively dead code on the failure path. In `RelayDriver`
(`RelayDriver.cs`), stage 9 ("Verify") runs `config.TestCommand` and, on a red
result, immediately returns `FlagAsync(... 9, "verify failed" ...)`. The stage
loop never reaches stage 10, so a task can never self-heal a verify failure.
(When verify is green, stage 10 still runs but has nothing to fix.)

This means a task whose own changes break the suite — formatting, a lint rule,
a small regression — is flagged for human review instead of being repaired by
the Fix-verify agent that exists precisely for this. The config even carries a
`maxVerifyLoops` knob (see JobFinder's `.relay/config.json`) that nothing honors.

## Goal

On a red stage-9 verify, run stage 10 (Fix-verify) with the failing test output,
then re-run the verify test command. Loop verify <-> fix-verify up to
`maxVerifyLoops` (default e.g. 3) attempts. Only flag if the suite is still red
after the loop is exhausted. A green verify proceeds straight to commit as today.

## Approach (suggested)

- Add `MaxVerifyLoops` to `RelayConfig` (+ `RelayConfigLoader` default + reader),
  mirroring the existing `bool BaselineVerify` wiring.
- Restructure the stage 9/10 handling so a red verify drives the Fix-verify agent
  (an ordinary stage invocation) and then re-runs the test command, bounded by
  `MaxVerifyLoops`. Record each attempt in the ledger/seals/status the same way
  other stages are recorded.
- Compose cleanly with the baseline-aware verify change if both land: the loop
  should key off "is verify red after baseline-exclusion".

## Files

- `src/VisualRelay.Core/Execution/RelayDriver.cs` (stage 9/10 loop)
- `src/VisualRelay.Domain/RelayConfig.cs` + `RelayConfigLoader.cs` (MaxVerifyLoops)
- `src/VisualRelay.Core/Execution/RelayStages.cs` if the stage shape needs it

## Tests

- A fixable verify failure (e.g. the injected test runner reports red once, then
  green after the Fix-verify stage runs) results in a committed task, not a flag.
- An unfixable failure (stays red every attempt) flags after `maxVerifyLoops`.
- `maxVerifyLoops` is respected (exact attempt count).

## Notes

Keep `RelayDriver.cs` under the 300-line guard — extract the verify/fix-verify
loop into a helper (e.g. in `RelayDriver.Artifacts.cs`) if needed. Use the mocked
test runner + subagent runner doubles already in the test project.
