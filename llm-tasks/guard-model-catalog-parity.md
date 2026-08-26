## Task: Guard the model catalog against drifting out of sync

The set of concrete models the backend can dispatch is written down in three
places that nothing checks against each other:

1. `model_name:` entries in `tools/backend/litellm-config.yaml` — the only
   names LiteLLM can actually route.
2. Keys of `RelayPricing.Default` in `src/VisualRelay.Core/Costs/RelayPricing.cs`
   — what the cost estimator can price.
3. A hardcoded `string[] realModels` array inside
   `BackendConfigGeneratorSelectableTests.SelectableModels_PerTierShapeAndCapped`,
   which the test uses as its notion of "a real model_list model".

Drift between them fails quietly in both directions. A model added to the
template but never priced is estimated at $0 for every run that touches it, and
nothing says so. A model priced (or listed as selectable, or named in a tier
chain) but absent from the template resolves to a name LiteLLM rejects with
`Invalid model name`, which reads as a provider outage rather than a config
mistake.

### Evidence (verified 2026-08-26)

- All three lists happen to agree today: 11 names in the template, the same 11
  keys in `RelayPricing.Default`, the same 11 in the hardcoded array.
- They agree only because the GLM 5.3 Flash swap edited all three by hand. The
  swap's own regression cover is
  `BackendConfigGeneratorZaiFrontierTests.RetiredGlmModelNames_AreGoneFromPricingAndSelectableLists`,
  which hardcodes the two retired names. That catches exactly one past mistake
  and nothing about the next one.
- `BackendConfigGeneratorAliasConsistencyTests` already checks a narrow slice:
  every `DefaultTierResolution` value has a pricing entry. It never looks at the
  template, so it cannot see a priced-but-unroutable model or an unpriced one.

### What to build

1. A test helper that parses the ordered list of `model_name:` values out of a
   litellm-config YAML string. Put it next to the existing template parsers in
   `BackendConfigGeneratorTestHelpers` (`ParseUpstreamModel`, `ParseModelTimeouts`)
   and follow their parsing style: enter at `model_list:`, leave at the next
   top-level key.
2. A new standalone test class holding the parity guards:
   - the template's `model_name` set equals the `RelayPricing.Default` key set,
     with a failure message naming which side each missing model is on;
   - every model named in `BackendConfigGenerator.Chains` exists in the template
     (the `fallback` pseudo-model is a tier alias, not a model, and is exempt);
   - every model in `BackendConfigGenerator.SelectableModelsByTier` exists in the
     template.
3. A negative control for each guard: run the same comparison against a small
   synthetic YAML string with a model removed and one added, and assert the
   comparison reports the difference. A guard nobody has watched fail is a guard
   nobody knows works.
4. Delete the hardcoded `realModels` array in
   `SelectableModels_PerTierShapeAndCapped` and derive that set from the template
   instead, so the third copy stops existing.

### Constraints

- Test-only and helper-only. No changes to `litellm-config.yaml`, to
  `RelayPricing`, or to any generator code — the lists agree today and this task
  is about keeping them that way, not about editing them.
- Put the new facts in a NEW test class whose file name does not start with any
  prefix tracked by `SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline`.
  `BackendConfigGeneratorSelectableTests` IS tracked, so do not add or remove a
  `[Fact]` there; step 4 edits the body of an existing fact only.
- Keep `RetiredGlmModelNames_AreGoneFromPricingAndSelectableLists` as it is. It
  asserts something the new guards do not: that two specific retired names are
  gone from all three lists at once.
- C# files stay under 300 lines.

### Tests (red first)

- Write the negative controls first and watch them fail against a helper that
  does not exist yet, then against a helper that returns an empty set. They must
  fail because the guard cannot see the difference, not because of a typo.
- The three parity guards pass on the first green run. That is expected: they
  encode an invariant that currently holds. The negative controls are what prove
  they would fire.

### Done when

- `./visual-relay test` is green, including the split-guard fact-count baseline.
- Removing any single `model_name` block from the template makes the parity guard
  fail with a message naming that model. Verify this by hand once before
  finishing, then restore the template.
