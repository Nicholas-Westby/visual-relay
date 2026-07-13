## Task: Split KeySetupPanelUiTests into smaller facts

The single slowest always-on test is `KeySetupPanelUiTests.PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` at 18.68 s. It renders a `MainWindow` with `SettingsPanel` and asserts all five provider states in one Avalonia headless test. Split it into focused facts that each assert on fewer providers, so the Avalonia headless dispatcher overhead per-test drops and individual tests complete faster.

### Baseline measurements

From `llm-tasks/speed-up-automated-tests/timings-baseline.txt`:

| Test | Duration |
|---|---|
| `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` | 18.68 s |
| Other KeySetupPanelUiTests (7 tests) | ~1-3 s each |
| **File total** | **~19 s** |

The 18.68 s test constructs a `MainWindow`, opens `SettingsPanel`, and asserts on all five provider keys (HF_TOKEN, DEEPSEEK_API_KEY, MOONSHOT_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY) plus the HF gate and pricing note — all in one test. The Avalonia headless dispatcher processes all UI interactions in this single test sequentially, making it the longest single serial span in the always-on `[Collection("Headless")]` chain.

### Prescribed approach

Split `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` into **three** `[AvaloniaFact]` tests, each focused on a subset of providers:

1. **`PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv`** — Asserts HF_TOKEN is set (display value contains "hf-a"), DEEPSEEK_API_KEY is set, `IsHuggingFaceConfigured` is true, `HfGateMessage` is empty, pricing note contains "pay-as-you-go", all provider URLs start with "https://". This is the most common scenario (HF + DeepSeek) and will be the fastest of the three.

2. **`PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv`** — Asserts MOONSHOT_API_KEY, ANTHROPIC_API_KEY, and OPENAI_API_KEY are all not set with "(not set)" display values. No HF-specific assertions here — those are covered by test #1.

3. **`PanelRendersAllProviderCount_AndKeyUrls_FromSeededEnv`** — Asserts `AllProviderKeys.Count == 5`, `KeyStates.Count == 5`, and every provider row's `GetKeyUrl` starts with "https://". This is the structural assertion test.

Each test creates its own `TestRepository`, seeds the same `.env` content (`HF_TOKEN=hf-abc123xyz\nDEEPSEEK_API_KEY=sk-deepseek-456\n`), constructs `MainWindowViewModel` and `MainWindow`, opens `SettingsPanel`, and asserts only its subset. The `SettingsPanel` construction cost is amortized across three smaller tests.

### Name-by-name coverage mapping

| Original test | → Destination |
|---|---|
| `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` | Split into three: `PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv` + `PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv` + `PanelRendersAllProviderCount_AndKeyUrls_FromSeededEnv` |

Every assertion from the original test must appear in exactly one of the three new tests. No assertion may be weakened or omitted. Audit assertions by enumerating them from the original test body:

- `Assert.Equal(5, MainWindowViewModel.AllProviderKeys.Count)` → test #3
- `Assert.Equal(5, vm.KeyStates.Count)` → test #3
- `Assert.True(hf.IsSet)` → test #1
- `Assert.Contains("hf-a", hf.DisplayValue)` → test #1
- `Assert.DoesNotContain("(not set)", hf.DisplayValue)` → test #1
- `Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == "DEEPSEEK_API_KEY").IsSet)` → test #1
- `foreach (var k in new[] { "MOONSHOT_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_API_KEY" })` → test #2
- `Assert.True(vm.IsHuggingFaceConfigured)` → test #1
- `Assert.Equal(string.Empty, vm.HfGateMessage)` → test #1
- `foreach (var row in MainWindowViewModel.AllProviderKeys) Assert.StartsWith("https://", row.GetKeyUrl!)` → test #3
- `Assert.Contains("pay-as-you-go", vm.HfPricingNote)` → test #1
- `dialog.Close()` — each test closes its own dialog

### Expected saving

The original 18.68 s test is replaced by three tests expected to run at ~6 s, ~6 s, and ~4 s respectively. Since they all run in the `[Collection("Headless")]` serial chain, total headless chain time drops by ~3-5 s. Expected saving: **~3–5 s** from full-suite wall time (constrained by the headless serial bottleneck).

### Pitfalls and guardrails

- **All three new tests must carry `[Collection("Headless")]` and `[AvaloniaFact]`.** The `SplitGuardVerificationTests` convention guard enforces this.
- **Each test seeds its own `.env` with the same content.** Do not share a seeded repo across tests (Avalonia headless tests cannot share a dispatcher-scoped fixture easily, and the existing pattern in `KeySetupPanelUiTests` is one `TestRepository` per test).
- **Do NOT combine this with any other refactoring.** The sole change is splitting one test into three. Do not touch other test methods, do not add shared fixtures, do not change `DictionaryEnvironmentAccessor` management.
- **Test count increases from 8 to 10** (7 other tests + 3 new ones). The coverage mapping above accounts for the removed test.
- **All 10 tests must pass in a full suite run.**

### Coverage rules

- Never delete, disable, skip, or weaken a test.
- Every assertion from the original test must appear in exactly one of the three new tests. Use the assertion audit list above as a checklist.
- Any assertion present in the original but missing from the three new tests constitutes lost coverage — do not proceed.

### Commit-message evidence

```
- test time dropped from 19s to 15s, saving 4s (KeySetupPanelUiTests file total)
```
