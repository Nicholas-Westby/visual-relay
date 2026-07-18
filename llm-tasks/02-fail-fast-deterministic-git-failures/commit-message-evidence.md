# Commit-message evidence sheet

Measure these DURING implementation (warm build, second run of each command)
and put the results in the commit body as at most 3 hyphen bullets, each 20
words or fewer, naming no files and no paths:

1. Full-suite wall clock before vs after: `time ./visual-relay test`.
2. Isolated duration before vs after of the normal-rerun resume test (filter
   token `RelayDriverResumeTests.RunTaskAsync_NormalRerun_StartsFromStage1`).
3. Slowest driver test in the TRX before vs after (sort the `duration=`
   attributes in the newest TRX under test-logs).

Example body shape (fill with real numbers, adjust wording freely):

- deterministic git failures now fail fast instead of sleeping through retries
- full suite NNNs to NNNs; slowest driver test NNs to NNs
- rerun driver test NNms isolated, was ~2.8s
