# Task: Guard the tier tables against the backend template's model_list

Every tier chain and every settings-dropdown entry names a model that must exist
as a `model_name` in the LiteLLM template. Nothing checks that today, so a typo
or a model added to one side only yields a generated config whose alias points
at a model LiteLLM has never heard of. That surfaces as a runtime failure on the
first request to that tier, not as a build or test failure.

### Evidence (2026-08-17)

- `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs` — `Chains`
  holds (model, required-key) pairs per tier, and `ResolveTiers` copies those
  model names straight into the generated `model_group_alias` and `fallbacks`
  blocks. `Generate` never checks a name against the template it just read.
- `src/VisualRelay.Core/Configuration/BackendConfigGenerator.Selectable.cs` —
  `SelectableModelsByTier` is a second, independently maintained list of model
  names, feeding the settings dropdowns. Nothing ties it to the template either.
- `tests/VisualRelay.Tests/BackendConfigGeneratorPerModelTimeoutTests.cs:19-31`
  — `PerModelTimeout_AllTenModelsHaveExplicitCeiling` hardcodes a 10-element
  `allModels` array duplicating the template's model names. It drifts silently:
  a model added to the template but not to the array is never timeout-checked.
- `tests/VisualRelay.Tests/BackendConfigGeneratorTestHelpers.cs:145` —
  `ParseModelTimeouts` already scans the `model_list:` block, so the parsing
  shape to copy exists. The file is 219 lines against the 300-line guard.
- `VisualRelay.Core` sets `InternalsVisibleTo VisualRelay.Tests`
  (`src/VisualRelay.Core/VisualRelay.Core.csproj:34`), so the internal `Chains`
  dictionary is directly reachable from the test assembly.

### What to build

1. In `BackendConfigGeneratorTestHelpers`, add
   `public static HashSet<string> ParseModelNames(string yaml)` returning every
   `model_name:` value in the `model_list:` block, using `StringComparer.Ordinal`.
   Follow `ParseModelTimeouts` exactly for block scanning: enter on the line
   `model_list:`, break on the first column-0 line that is neither blank nor a
   `#` comment, and take names from lines starting with `  - model_name: `.

2. Add a new test class in its own file (e.g.
   `BackendConfigGeneratorTemplateCoverageTests`) with two tests:
   - every model named in `BackendConfigGenerator.Chains` values exists in
     `ParseModelNames`, skipping the `"fallback"` pseudo-model (it is a tier
     alias, not a `model_name`);
   - every model in `BackendConfigGenerator.SelectableModelsByTier` values
     exists in `ParseModelNames`.
   Both assertion messages must name the offending model and its tier, so a
   failure says which table drifted.

3. Rewrite `PerModelTimeout_AllTenModelsHaveExplicitCeiling` to derive its model
   list from `ParseModelNames(yaml)` rather than the hardcoded array, and rename
   it so the name no longer pins a count (e.g.
   `PerModelTimeout_EveryTemplateModelHasExplicitCeiling`). The assertion itself
   (every model carries a `timeout:`) stays as-is. Delete the `allModels` array.

Both new tests must fail if a model name is removed from the template, and pass
on the template as it stands today. Verify that by temporarily deleting a
`model_name:` entry locally and confirming the failure names that model.

### Out of scope

- Making `BackendConfigGenerator.Generate` validate against the template at
  runtime. This task is test-tier guarding only.
- Any edit to `tools/backend/litellm-config.yaml`, to `Chains`, or to
  `SelectableModelsByTier`. They are correct today; the point is keeping them
  correct.
- Pricing coverage, which a separate test already guards.
