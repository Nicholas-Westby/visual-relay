# Task: Make the identical-failure advisory fire despite duration and tail noise

The fix-verify loop appends `— identical failure across all attempts; likely
environment/harness, not the change` to the flag reason (and publishes a
`verify_identical_failures` event) when every attempt fails the same way. In
the 2026-07-19 patternsmith flag this diagnosis was exactly right — three
attempts red on the identical `everySourceFileUnder200Lines()` failure, with
identical sealed treeHash — yet the advisory never fired. The per-attempt
signatures were 600-char log TAILS whose durations and interleaved
passing-test lines differ every run, and `NormalizeVerifySignature` strips
only ISO timestamps and `outputFile` values. Give the advisory signature
sources that are actually stable.

### Evidence (2026-07-19 patternsmith `better-explain-the-1-stuff` flag)

- `RelayDriver.VerifyFix.cs:246-263`: advisory + `verify_identical_failures`
  event, gated on `normalized.All(n => n == normalized[0])` over the
  per-attempt reasons collected at `:189-192`.
- `RelayDriver.VerifyFix.cs:271-277` `NormalizeVerifySignature`: strips
  ISO-8601 timestamps and `"outputFile": "…"` values, collapses whitespace.
  Durations (`after 0.198 seconds`), counts, and all other digit runs
  survive.
- The three real attempt signatures (run.log `reason=` entries) were
  left-trimmed tails each beginning mid-line: `… after 0.224 seconds. 􁁛
  Test runCapsManyMatchesAndFlagsTruncation() passed …`, `…passed after
  0.207 seconds. 􁁛 Test editingInputClearsStaleMatches() passed …`,
  `…fter 0.198 seconds. 􁁛 Test contextSnippetExtractsSurroundingLines()
  passed …`. They differ in durations AND in which concurrently-finishing
  passing tests landed inside each 600-char window — so digit-masking alone
  would NOT have equalized them. run.log contains zero
  `verify_identical_failures` events for the run.
- The seals already carried a noise-free equality signal: stage-11 attempts
  1-3 all sealed treeHash `ddf95628…`. `WorkingTreeHash`
  (`RelayDriver.Artifacts.cs:157`) fingerprints manifest files only (see
  the note at `RelayDriver.VerifyObservability.cs:38`), and the per-attempt
  value is computed at `RelayDriver.VerifyFix.cs:203` right before sealing.
- In-repo precedent for digit-drift masking: `NormalizeForComparison`
  (`RelayDriver.RepoGuards.cs:120-123`) replaces standalone digit runs with
  `#` precisely so count drift (`332 lines` → `333 lines`) cannot fake a
  new guard violation.

### What to build

1. **Digit masking in `NormalizeVerifySignature`.** Mask standalone digit
   runs (reuse or mirror `NormalizeForComparison`) so durations and counts
   cannot break equality. With companion task
   `07-surface-failing-test-line-in-flag-reason` landed, the three incident
   reasons all anchor on the same failing line and normalize identically
   (`failed after # seconds with # issue`); without 07, masking is
   necessary but not sufficient (see evidence) — which is why item 2
   exists.
2. **Tree-identity trigger.** Also fire the advisory when every red attempt
   sealed the SAME treeHash — identical task-scoped tree in, red out, N
   times is the environment/harness signature even when the reason text is
   noisy. Use distinct wording, e.g. `— tree unchanged across all attempts
   while verify stayed red; likely environment/harness, not the change`,
   and document the scope caveat in a comment: equal hash means the
   MANIFEST files were unchanged, not the whole worktree.
3. **Event payload names the trigger.** Extend the
   `verify_identical_failures` event data with which trigger(s) fired
   (`text`, `tree`, or both) so run.log distinguishes them.
4. **Regression fixtures replaying the incident.** Three tail-style
   signatures from the evidence plus equal tree hashes ⇒ advisory fires via
   the tree trigger; three anchored-style reasons differing only in digits
   ⇒ fires via the text trigger; two genuinely different failures
   (different failing-test names, different tree hashes) ⇒ stays silent.

### Constraints

- No false positives: digit masking must mask digit RUNS only, never words
  — two different failing-test names must never normalize equal; differing
  tree hashes with differing text stay silent.
- Keep the existing text-equality message verbatim and the event name
  `verify_identical_failures` unchanged (both are grep targets).
- The advisory still appends to the flag reason AFTER the `(see …)`
  artifact pointer, preserving today's ordering
  (`RelayDriver.VerifyFix.cs:239-263`).
- `RelayDriver.VerifyFix.cs` is ~298 lines against the 300-line file guard
  — the new trigger logic almost certainly needs a helper/partial rather
  than growing this file.

### Tests (red first)

- `NormalizeVerifySignature("failed after 0.016 seconds with 1 issue")` ==
  `NormalizeVerifySignature("failed after 0.019 seconds with 1 issue")`.
- Incident replay (tree trigger): three noisy tail signatures + equal
  per-attempt tree hashes ⇒ flag reason ends with the tree-trigger wording
  and exactly one `verify_identical_failures` event is published with
  `trigger=tree`.
- Text trigger: three digit-variant anchored reasons, distinct tree hashes
  ⇒ existing wording, `trigger=text`.
- Negative: different failing-test names and different tree hashes ⇒ no
  advisory, no event.

### Verification

- `./visual-relay check` fully green.
