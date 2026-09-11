# Retire the fact-count baseline in SplitGuardVerificationTests

`SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline`
counts `[Fact]` attributes across 36 test-file families and compares the sum to
a hand-maintained constant. It was written to prove that the 2026-06 split of
oversized test files lost no test. That split is long done, and what remains is
a toll: every fact added or removed in those families needs a bump of the
constant plus a dated line in a file that sits at exactly 300 lines, the cap the
guard family itself enforces. The count cannot tell a deleted test from a merged
theory row, its log is internally inconsistent, and the next legitimate bump
forces a split of the guard file. Delete the test and keep the size guards.

## Evidence

- `git log -G'const int baseline = ' -- tests/VisualRelay.Tests/SplitGuardVerificationTests.cs`
  lists 43 commits. The body carries 35 dated bumps from 2026-06-18 to
  2026-09-11 (two of them net zero) and the doc block eight more.
- The doc block (`:56-94`) says the count "must match the baseline of 143"
  while the constant is 144; the log jumps from `182→189` (`:154`) to
  `185→171` (`:157`) and later to `173→171` (`:162`); `:203-207` calls a drop
  "the first downward bump" after five earlier ones.
- HEAD 5b85640b bumped 143→144 for one bundle-path fact. The plan of
  2026-09-10 (`docs/superpowers/plans/2026-09-10-deepseek-flash-default-and-cleanup.md:20`)
  told implementers to put new facts in classes outside the list to avoid the
  bump; that is the mechanism steering where tests go.
- `wc -l` is 300 exactly; the file cannot take another line.

## Current state (researched at 5b85640b)

- `tests/VisualRelay.Tests/SplitGuardVerificationTests.cs:95-299`: the test.
  `:234` `const int baseline = 144;`; the prefix list `:236-278` (36 entries:
  eleven split families, 23 promoted classes including the `ModelCatalog*`
  ones, two GitSim relocations); the count `:280-298` is
  `Regex.Matches(File.ReadAllText(filePath), @"\[Fact\]").Count` over the
  top-level `*.cs` files whose name equals a prefix or starts with `prefix.`;
  `[Theory]` rows are not counted.
- Class doc `:6-10`: "These MUST fail before the split lands and pass after
  every oversized test file is under 300 lines with zero [Fact] loss."
- The guards that still do the job: `FileSizeGuard_ReportsNoViolations` (`:23`)
  and `AllTestCsFiles_AreAtMost300Lines` (`:38`), both on
  `tools/VisualRelay.Guards/FileSizeGuard.cs` (`DefaultLimit = 300` at `:13`,
  env override `VISUAL_RELAY_FILE_LINE_LIMIT` at `:16`), which
  `./visual-relay check` runs first.
- The partials are untouched by this task: `SplitGuardVerificationTests.Conventions.cs`
  (272 lines), `.Headless.cs` (102), `.InjectionSeams.cs` (183),
  `.WholeAppBoot.cs` (144).
- `grep -rn "const int baseline" tests/ tools/` has exactly one hit; nothing
  else in the repository counts facts.

## Prescribed approach

1. Delete `FactCount_AcrossOversizedFiles_MatchesBaseline` together with its
   doc block and bump log (`:56-299`). No replacement counter: a lost test is
   visible in the diff and in the total `dotnet test` reports, and the 300-line
   cap is the guard the split was about.
2. Rewrite the class doc (`:6-10`) to describe what the file now holds: the two
   size guards, with the conventions, headless, injection-seam and whole-app
   partials named.
3. Leave the historical plans and the retired specs as they are; they are
   records. AGENTS.md and TROUBLESHOOTING.md do not mention the baseline, so no
   doc changes.

The commit body states the bump count and the two inconsistencies above as the
measured reason.

## Tests

- `./visual-relay test SplitGuardVerificationTests` passes with the remaining
  facts; `grep -rn "const int baseline" tests/` returns nothing.
- `./visual-relay check` exits 0 with 0 inspect-code findings; the file-size
  guard still runs over `src`, `tests`, `tools` at 300 lines.

## Verification

No runtime behavior changes; the two commands above are the verification. Run
`./visual-relay test` in full once to confirm the total test count moved by
exactly the one deleted fact.

## Out of scope

Any other guard in the family; per-file caps or allowlists in `FileSizeGuard`;
the stale plan sentence about the baseline.

## Rejected alternatives

- A baseline table in a data file: moves the churn out of the 300-line file and
  keeps every false alarm.
- Per-family counts: the same alarm, 36 times.
- A reflection count of the whole assembly with a floor: a floor that has to
  move every time a test is legitimately removed is the same mechanism again.
