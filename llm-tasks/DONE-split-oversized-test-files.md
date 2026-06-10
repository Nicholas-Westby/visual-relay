# 15 test files violate the repo's 300-line guard (accumulated via sealed commits)

`tools/guards/check-file-size.sh` exits 1 with 15 violations (2026-06-10), all test
files, e.g. `SwivalSubagentRunnerWatchdogTests.cs` at 613 lines,
`RelayDriverResumeTests.cs` at 405, `RelayDriverGitCommitTests.cs` at 358 — the full
list is the guard's output. They grew across today's pipeline-sealed commits because
the pipeline never runs the guard (root cause tracked separately in
`verify-enforce-repo-guards.md`); `./visual-relay check` now fails at its first step
for everyone.

## Goal

`tools/guards/check-file-size.sh` exits 0. Every split preserves the full test set —
same test names, same coverage, no `[Fact]` deleted or skipped — and the suite stays
green. Splits follow the repo's existing partial/companion-file conventions (e.g.
`GitCommitterAutoIncludeTests.cs` + `GitCommitterAutoIncludeTests.Snapshot.cs`).

## Approach (suggested)

- Mechanical: group related test methods into cohesive companion files
  (`<Name>Tests.<Aspect>.cs` or split by fixture); move shared helpers/doubles into
  the existing `TestDoubles.cs`/`*TestDoubles.cs` files where they fit.
- Verify by: guard exits 0, `dotnet test` count is unchanged from before the split
  (compare totals), suite green.
