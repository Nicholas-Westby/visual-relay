# Harness follow-up: scope the guard-output digit normalizer to standalone numbers

A code review of `harness-scope-fix-to-diff` (commit c5b002e) found a MEDIUM correctness gap in the
baseline-guard normalizer it introduced. `NormalizeForComparison` collapses **every** digit run on a
guard-output line to a placeholder (`\d+` → `#`), which is too aggressive: it also blanks digits that are
part of file paths or identifiers, not just the volatile count. Consequence — a genuinely **new** guard
violation can be wrongly excluded as pre-existing and slip through the stage-9 Verify gate.

## Current state (researched)

> **Freshness contract:** Locate every anchor by searching for the quoted snippet — never by line number.
> If a quoted snippet no longer exists, treat this researched state as stale and re-verify before building.

- The normalizer lives in `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs`. Find the private
  helper `NormalizeForComparison` (search for that name) inside `RunGuardCheckAsync`. It applies a digit-run
  replacement to BOTH the baseline guard output and each working-tree guard line before the exact-key
  `HashSet`/`ExceptWith` diff. Search for the replacement that turns digit runs into a placeholder (e.g. a
  `Regex.Replace(... "#")` or equivalent over `\d+` / `[0-9]+`).
- The shipped guard whose message it targets is `tools/guards/check-file-size.sh`, which prints
  `file too large: <path> has <N> lines (limit <L>)`. The volatile values are `<N>` and `<L>`; `<path>` is NOT
  volatile and must stay distinguishing.

### The bug (confirmed empirically by the reviewer)
With whole-line `\d+ → #`:
- `file too large: src/Page1.cs has 320 lines (limit 300)` and
- `file too large: src/Page2.cs has 999 lines (limit 300)`
both normalize to `file too large: src/Page#.cs has # lines (limit #)`.
So if `Page1.cs` is already oversize in the baseline and the task pushes `Page2.cs` newly oversize, the NEW
`Page2.cs` violation matches the baseline key and is removed — it slips through Verify. The same over-collapse
would mask digit-bearing rule IDs (`CA1822` vs `CA2007`) if a future `guardCmd` emits them.

## What to build

Change the normalizer so it collapses **only standalone digit runs** — digits that are NOT adjacent to an
ASCII letter — leaving digits embedded in identifiers/paths intact. Replace the digit pattern with one
equivalent to:

```
(?<![A-Za-z])[0-9]+(?![A-Za-z])
```

This collapses the count tokens (`320`, `300`, `999` — each bordered by space/`(`/`)`) while preserving
`Page1`/`Page2`, `CA1822`/`CA2007`, `Migration0007`, `area51`, `MacOSX14`, etc. Keep the change GENERAL — do
NOT hard-code the file-size guard's `has N lines` wording into the harness (that would couple VR to one
guard's message format; VR must stay toolchain-agnostic). The lookaround approach stays generic.

Keep everything else about `RunGuardCheckAsync` unchanged: still apply normalization symmetrically to baseline
and working output; still surface the RAW (un-normalized) line to the agent/ledger so operators see real counts.

## Tests (extend the existing ones)

- In `GuardOutputNormalizerTests` (search for that class): the existing "different paths stay distinct" case
  uses alpha names (`foo`/`bar`) and would NOT catch this regression. ADD a case proving two oversize lines
  whose paths differ only by a digit stay DISTINCT after normalization, e.g. `.../Page1.cs has 320 lines (limit 300)`
  vs `.../Page2.cs has 999 lines (limit 300)` → different keys. Keep the "count-only shift collapses" case green.
- In `RelayDriverRepoGuardRegressionTests` (search for that class), add/extend a regression asserting a task that
  newly pushes `Page2.cs` over the limit STILL blocks (stage 10 entered / violation surfaced) even when a
  numbered sibling `Page1.cs` is already oversize in the baseline.

## Done when
- The normalizer no longer collapses digits embedded in identifiers/paths (only standalone numbers).
- The new distinct-numbered-paths unit test and the numbered-sibling regression test pass; all prior guard
  tests stay green.
- No file-size-guard-specific wording is hard-coded into the harness.
