## Task: Split KeySetupPanelUiTests into smaller facts

The single slowest always-on test is `KeySetupPanelUiTests.PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` at 18.68 s. It boots a full `MainWindow`, opens settings through the cog, and asserts all five provider states in one Avalonia headless test. Split it into three focused facts — two of them scoped to the settings window alone — so most of the assertions stop paying the whole-app boot and cog-open cost.

### Baseline measurements

From `llm-tasks/completed/speed-up-automated-tests/timings-baseline.txt`:

| Test | Duration |
|---|---|
| `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` | 18.68 s |
| Other KeySetupPanelUiTests (7 tests) | ~1-3 s each |
| **File total** | **~19 s** |

The 18.68 s test constructs a `MainWindow`, opens `SettingsPanel`, and asserts on all five provider keys (HF_TOKEN, DEEPSEEK_API_KEY, MOONSHOT_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY) plus the HF gate and pricing note — all in one test. The Avalonia headless dispatcher processes all UI interactions in this single test sequentially, making it the longest single serial span in the always-on `[Collection("Headless")]` chain.

### Prescribed approach

The 18.68 s is dominated by whole-app work the provider assertions do not need: booting a full `MainWindow` (1440×900) with `LoadInitialAsync`, then clicking the cog and pumping the dispatcher in a polling helper until the owned `SettingsWindow` appears. The repo convention (see `AGENTS.md` and the `SplitGuardVerificationTests` whole-app-boot guard) is to scope panel assertions down; `SettingsTestHelpers.ShowScopedSettings` is the established pattern.

Split `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` into **three** `[AvaloniaFact]` tests:

1. **`PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv`** (scoped) — creates its own `TestRepository`, seeds the same `.env`, builds the `MainWindowViewModel`, and opens the settings window via `SettingsTestHelpers.ShowScopedSettings(vm)` — no `MainWindow`, no cog click. Asserts HF_TOKEN is set (display value contains "hf-a" and not "(not set)"), DEEPSEEK_API_KEY is set, `IsHuggingFaceConfigured` is true, `HfGateMessage` is empty, and the pricing note contains "pay-as-you-go".

2. **`PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv`** (scoped, same pattern) — Asserts MOONSHOT_API_KEY, ANTHROPIC_API_KEY, and OPENAI_API_KEY are all not set with "(not set)" display values. No HF-specific assertions here — those are covered by test #1.

3. **`PanelRendersAllProviderCount_KeyUrls_AndOpensFromCog_FromSeededEnv`** (windowed — keeps the original path) — boots `MainWindow` and opens settings through the cog exactly like the original, preserving the original's wiring and structural assertions: `IsSettingsOpen` is true, a `SettingsPanel` is present in the dialog's visual tree, `AllProviderKeys.Count == 5`, `KeyStates.Count == 5`, and every provider row's `GetKeyUrl` starts with "https://". Keeping one windowed fact preserves the cog→settings coverage this class is allowlisted for, while the two state facts stop paying for it.

Each test creates its own `TestRepository`, seeds the same `.env` content (`HF_TOKEN=hf-abc123xyz\nDEEPSEEK_API_KEY=sk-deepseek-456\n`), and closes its own dialog.

### Name-by-name coverage mapping

| Original test | → Destination |
|---|---|
| `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` | Split into three: `PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv` + `PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv` + `PanelRendersAllProviderCount_KeyUrls_AndOpensFromCog_FromSeededEnv` |

Every assertion from the original test must appear in exactly one of the three new tests. No assertion may be weakened or omitted. Audit assertions by enumerating them from the original test body:

- `Assert.True(vm.IsSettingsOpen)` → test #3 (needs the cog path)
- `Assert.NotNull(dialog.GetVisualDescendants().OfType<SettingsPanel>().FirstOrDefault())` → test #3
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

### Expected saving (estimate — verify by measurement)

The two scoped facts avoid the whole-app boot and cog polling entirely (comparable scoped settings facts run in ~1–3 s), and the windowed fact does strictly less per-test work than the original. All of these run in the `[Collection("Headless")]` serial chain, so the saving is 18.68 s minus the sum of the three replacements — estimated **3–12 s** off the headless chain. The range is deliberately wide: part of the 18.68 s may be one-time dispatcher warm-up that simply moves to whichever headless test runs first. Only the before/after measurement decides, per the commit-message evidence section below.

### Pitfalls and guardrails

- **All three new tests must carry `[Collection("Headless")]` and `[AvaloniaFact]`.** The `SplitGuardVerificationTests` convention guard enforces this; the scoped facts still run on the shared headless dispatcher.
- **The class stays on the whole-app-boot allowlist** (`SplitGuardVerificationTests.WholeAppBoot`): test #3 still constructs `MainWindow`, so do not remove the allowlist entry.
- **Each test seeds its own `.env` with the same content.** Do not share a seeded repo across tests (Avalonia headless tests cannot share a dispatcher-scoped fixture easily, and the existing pattern in `KeySetupPanelUiTests` is one `TestRepository` per test).
- **Do NOT combine this with any other refactoring.** The sole change is splitting one test into three. Do not touch other test methods, do not add shared fixtures, do not change `DictionaryEnvironmentAccessor` management.
- **Test count increases from 8 to 10** (7 other tests + 3 new ones). The coverage mapping above accounts for the removed test.
- **All 10 tests must pass in a full suite run.**
- **Bail out if the numbers don't improve.** If the three replacement tests together are not measurably faster than the original test (same machine, same command), do not land the split — report the measurements instead.

### Coverage rules

- Never delete, disable, skip, or weaken a test.
- Every assertion from the original test must appear in exactly one of the three new tests. Use the assertion audit list above as a checklist.
- Any assertion present in the original but missing from the three new tests constitutes lost coverage — do not proceed.

### Commit-message evidence

Measure the `KeySetupPanelUiTests` file total (and the full-suite wall time) right
before starting and right after finishing. Then put exactly one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Do not write the numbers into this task file — real
measured numbers belong in the commit message only.
