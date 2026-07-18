# Commit-message evidence sheet

Measure these DURING implementation (warm build, second run of each command)
and put the results in the commit body as at most 3 hyphen bullets, each 20
words or fewer, naming no files and no paths:

1. Isolated duration of the normal-rerun test before vs after (filter token
   `RelayDriverResumeTests.RunTaskAsync_NormalRerun_StartsFromStage1`).
2. Isolated duration of the triple-rerun attempt-index test before vs after
   (filter token `RelayDriverRerunTests`).
3. Confirmation number: how many happy-path calls now exercise the isolated
   verify worktree (count of converted call sites).

Example body shape (fill with real numbers, adjust wording freely):

- happy-path driver runs now exercise the isolated verify worktree on a
  committed sim
- rerun tests NNms and NNms isolated after conversion
- fallback gate coverage unchanged for commit-gate and flag tests
