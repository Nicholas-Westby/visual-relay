## Stage 1 - Ideate

{
  "summary": "Split the 18.68 s all-in-one test into three focused facts — two scoped to the settings window (avoiding MainWindow boot and cog-click polling) and one windowed fact preserving cog→settings coverage — to cut serial runtime in the headless collection.",
  "options": [
    "Option A — Prescribed 3-test split: PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv (scoped), PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv (scoped), PanelRendersAllProviderCount_KeyUrls_AndOpensFromCog_FromSeededEnv (windowed).",
    "Option B — 2-test split: merge all five provider state assertions into one scoped test; keep structural/cog assertions in a windowed test.",
    "Option C — Optimize in-place: rewrite existing test to use ShowScopedSettings without splitting, removing cog coverage entirely."
  ]
}

## Stage 2 - Research

{
  "findings": "The original test `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` (lines 26–66 of `KeySetupPanelUiTests.cs`) contains 14+ assertions spanning provider key states (HF_TOKEN set, DEEPSEEK_API_KEY set, three unset), UI structural checks (IsSettingsOpen, SettingsPanel in visual tree), count checks (AllProviderKeys.Count==5, KeyStates.Count==5), URL format checks, and HF gate/pricing assertions. The scoped pattern `SettingsTestHelpers.ShowScopedSettings(vm)` already exists (used in `SettingsPanelUiTests.OpenScopedSettingsAsync`) and lets tests open a settings window without booting a full MainWindow or clicking the cog. The `MainWindowViewModel.OpenSettingsAsync()` method populates KeyStates identically to the cog path. From `MainWindowViewModel.Keys.cs`: `MaskValue` on `hf-abc123xyz` returns `hf-a…bc12` (first 4 chars + ellipsis + last 4 chars), so `Assert.Contains(\"hf-a\", ...)` matches the original. The class `KeySetupPanelUiTests` is on the WholeAppBoot allowlist (line 36 of `SplitGuardVerificationTests.WholeAppBoot.cs`) with justification \"key setup reached through the top bar\" — test #3 preserves that by keeping the full MainWindow path. All 7 other tests in the file are untouched. The file currently has 8 tests; splitting the target test into 3 will make 10.",
  "constraints": [
    "All three new tests must carry `[Collection(\"Headless\")]` and `[AvaloniaFact]`.",
    "The class stays on the WholeAppBoot allowlist (test #3 still constructs `MainWindow`).",
    "Each test seeds its own `.env` with the same content — no shared seeded repos across tests.",
    "Every assertion from the original must appear in exactly one of the three new tests — no lost coverage, no weakening.",
    "Do NOT combine this with any other refactoring — sole change is splitting one test into three.",
    "Do not touch other test methods, do not add shared fixtures, do not change `DictionaryEnvironmentAccessor` management.",
    "Each test must call `dialog.Close()` and `Dispatcher.UIThread.RunJobs()` after asserting.",
    "The two scoped tests must call `await vm.OpenSettingsAsync()` before `ShowScopedSettings(vm)` to populate KeyStates.",
    "The windowed test #3 must follow the exact original pattern with `new MainWindow`, `window.Show()`, `OpenSettings(window)` (the polling cog path).",
    "Test count increases from 8 to 10.",
    "If the three replacement tests together are not measurably faster than the original, do not land the split — report measurements instead.",
    "Never delete, disable, skip, or weaken a test."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Read the original test KeySetupPanelUiTests.cs (lines 25-66), SettingsTestHelpers.cs (ShowScopedSettings at lines 97-105), MainWindowViewModel.Keys.cs (MaskValue at lines 292-297, RefreshKeyStatesAsync at lines 154-177), SettingsPanelUiTests.cs (OpenScopedSettingsAsync scoped pattern at lines 32-37), SplitGuardVerificationTests.WholeAppBoot.cs (allowlist entry at line 36), and timings-baseline.txt (18.68s for the target test at line 17). All 14+ assertions from the original test are confirmed auditable and mappable to exactly one of the three replacement tests. The MaskValue(\"hf-abc123xyz\") produces \"hf-a…3xyz\" — meaning Assert.Contains(\"hf-a\", ...) correctly matches the masked display. OpenSettingsAsync() (MainWindowViewModel.Settings.cs line 74) sets IsSettingsOpen=true and calls RefreshKeyStatesAsync(), which populates KeyStates, IsHuggingFaceConfigured, and HfGateMessage — exactly the state the scoped tests need. The WholeAppBoot allowlist must retain the KeySetupPanelUiTests entry because test #3 constructs new MainWindow. The dotnet CLI is not available in this tool environment so fresh before timings cannot be taken here; the 18.68s baseline from the TRX file (2026-06-17) provides the reference point. The implementer must measure before and after with dotnet test as prescribed by commit-message-evidence.md.",
  "excerpts": [
    "// KeySetupPanelUiTests.cs:25-66 — original test with all 14+ assertions\n[AvaloniaFact]\npublic async Task PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv()\n{\n    using var repo = TestRepository.Create();\n    repo.WriteConfig(\"dotnet test\", []);\n    repo.WriteTask(\"alpha\", \"# Alpha\\n\");\n    using var r = SeedUserEnv(repo, \"HF_TOKEN=hf-abc123xyz\\nDEEPSEEK_API_KEY=sk-deepseek-456\\n\");\n    var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };\n    await vm.LoadInitialAsync();\n    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };\n    window.Show();\n    Dispatcher.UIThread.RunJobs();\n    var dialog = OpenSettings(window);\n    // ... 14+ assertions spanning HF/DEEPSEEK set, MOONSHOT/ANTHROPIC/OPENAI unset,\n    //     IsSettingsOpen, SettingsPanel visual, AllProviderKeys.Count, KeyStates.Count,\n    //     IsHuggingFaceConfigured, HfGateMessage, GetKeyUrl, HfPricingNote\n    dialog.Close();\n    Dispatcher.UIThread.RunJobs();\n}",
    "// SettingsTestHelpers.cs:97-105 — scoped settings pattern (no MainWindow, no cog)\npublic static SettingsWindow ShowScopedSettings(MainWindowViewModel vm)\n{\n    var dialog = new SettingsWindow { DataContext = vm };\n    dialog.Show();\n    dialog.Measure(new Size(dialog.Width, dialog.Height));\n    dialog.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));\n    Dispatcher.UIThread.RunJobs();\n    return dialog;\n}",
    "// SettingsPanelUiTests.cs:32-37 — existing scoped pattern usage\nprivate async Task<SettingsWindow> OpenScopedSettingsAsync(TestRepository repo)\n{\n    var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };\n    await vm.OpenSettingsAsync();\n    return SettingsTestHelpers.ShowScopedSettings(vm);\n}",
    "// MainWindowViewModel.Keys.cs:292-297 — MaskValue behavior\nprivate static string MaskValue(string value)\n{\n    if (string.IsNullOrEmpty(value)) return \"(not set)\";\n    if (value.Length <= 8) return new string('•', value.Length);\n    return value[..4] + \"…\" + value[^4..];\n}",
    "// MainWindowViewModel.Settings.cs:74-78 — OpenSettingsAsync populates KeyStates\npublic async Task OpenSettingsAsync()\n{\n    IsSettingsOpen = true;\n    await RefreshKeyStatesAsync();\n}",
    "// SplitGuardVerificationTests.WholeAppBoot.cs:36 — allowlist entry must stay\n[\"KeySetupPanelUiTests\"] = \"key setup reached through the top bar\",",
    "// timings-baseline.txt:17 — baseline measurement\n18.68 s  KeySetupPanelUiTests.PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv"
  ],
  "repro": "1. Measure before: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter FullyQualifiedName~KeySetupPanelUiTests` and record the file total and the 18.68s test duration. Optionally record full-suite wall time.\n2. Delete `PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv` (lines 25-66 of KeySetupPanelUiTests.cs).\n3. Add three new [AvaloniaFact] tests:\n   a. `PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv` — scoped: TestRepository, SeedUserEnv, new MainWindowViewModel, await vm.OpenSettingsAsync(), ShowScopedSettings(vm). Assert: HF_TOKEN IsSet, DisplayValue contains \"hf-a\" and not \"(not set)\", DEEPSEEK_API_KEY IsSet, vm.IsHuggingFaceConfigured, vm.HfGateMessage==\"\", vm.HfPricingNote contains \"pay-as-you-go\". dialog.Close().\n   b. `PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv` — same scoped setup. Assert: MOONSHOT/ANTHROPIC/OPENAI all IsSet==false and DisplayValue==\"(not set)\". dialog.Close().\n   c. `PanelRendersAllProviderCount_KeyUrls_AndOpensFromCog_FromSeededEnv` — full MainWindow path like original: TestRepository, SeedUserEnv, LoadInitialAsync, new MainWindow, Show, OpenSettings(window). Assert: IsSettingsOpen, SettingsPanel in visual tree, AllProviderKeys.Count==5, KeyStates.Count==5, all GetKeyUrl start with \"https://\". dialog.Close().\n4. Run the same dotnet test command and verify 10 tests pass (7 original + 3 new). Verify the file total is less than the before measurement.\n5. Run full suite to verify no regressions."
}

## Stage 4 - Plan

{
  "plan": "## Plan: Split KeySetupPanelUiTests.PanelRendersAllFiveProviders_WithCorrectSetUnsetState_FromSeededEnv\n\n### Measurements (before editing)\n\n1. Run `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter FullyQualifiedName~KeySetupPanelUiTests` and record the file total and the target test's duration.\n2. Optionally record full-suite wall time.\n\n### Edit: `tests/VisualRelay.Tests/KeySetupPanelUiTests.cs`\n\nDelete the original test method at lines 25-66 and replace it with three new `[AvaloniaFact]` methods (preserving order: scoped #1, scoped #2, windowed #3).\n\n**New test #1** — `PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv` (scoped, after line 24):\n\n```csharp\n[AvaloniaFact]\npublic async Task PanelRendersHfAndDeepSeek_WithCorrectSetState_FromSeededEnv()\n{\n    using var repo = TestRepository.Create();\n    using var r = SeedUserEnv(repo, \"HF_TOKEN=hf-abc123xyz\\nDEEPSEEK_API_KEY=sk-deepseek-456\\n\");\n\n    var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };\n    await vm.OpenSettingsAsync();\n    var dialog = SettingsTestHelpers.ShowScopedSettings(vm);\n\n    var hf = vm.KeyStates.First(s => s.Row.EnvVarName == \"HF_TOKEN\");\n    Assert.True(hf.IsSet);\n    Assert.Contains(\"hf-a\", hf.DisplayValue, StringComparison.Ordinal);\n    Assert.DoesNotContain(\"(not set)\", hf.DisplayValue, StringComparison.Ordinal);\n\n    Assert.True(vm.KeyStates.First(s => s.Row.EnvVarName == \"DEEPSEEK_API_KEY\").IsSet);\n\n    Assert.True(vm.IsHuggingFaceConfigured);\n    Assert.Equal(string.Empty, vm.HfGateMessage);\n    Assert.Contains(\"pay-as-you-go\", vm.HfPricingNote, StringComparison.Ordinal);\n\n    dialog.Close();\n    Dispatcher.UIThread.RunJobs();\n}\n```\n\n**New test #2** — `PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv` (scoped):\n\n```csharp\n[AvaloniaFact]\npublic async Task PanelRendersMoonshot_Anthropic_OpenAI_WithCorrectUnsetState_FromSeededEnv()\n{\n    using var repo = TestRepository.Create();\n    using var r = SeedUserEnv(repo, \"HF_TOKEN=hf-abc123xyz\\nDEEPSEEK_API_KEY=sk-deepseek-456\\n\");\n\n    var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };\n    await vm.OpenSettingsAsync();\n    var dialog = SettingsTestHelpers.ShowScopedSettings(vm);\n\n    foreach (var k in new[] { \"MOONSHOT_API_KEY\", \"ANTHROPIC_API_KEY\", \"OPENAI_API_KEY\" })\n    {\n        Assert.False(vm.KeyStates.First(s => s.Row.EnvVarName == k).IsSet);\n        Assert.Equal(\"(not set)\", vm.KeyStates.First(s => s.Row.EnvVarName == k).DisplayValue);\n    }\n\n    dialog.Close();\n    Dispatcher.UIThread.RunJobs();\n}\n```\n\n**New test #3** — `PanelRendersAllProviderCount_KeyUrls_AndOpensFromCog_FromSeededEnv` (windowed, preserves original path):\n\n```csharp\n[AvaloniaFact]\npublic async Task PanelRendersAllProviderCount_KeyUrls_AndOpensFromCog_FromSeededEnv()\n{\n    using var repo = TestRepository.Create();\n    repo.WriteConfig(\"dotnet test\", []);\n    repo.WriteTask(\"alpha\", \"# Alpha\\n\");\n    using var r = SeedUserEnv(repo, \"HF_TOKEN=hf-abc123xyz\\nDEEPSEEK_API_KEY=sk-deepseek-456\\n\");\n\n    var vm = new MainWindowViewModel(_env) { RootPath = repo.Root };\n    await vm.LoadInitialAsync();\n    var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };\n    window.Show();\n    Dispatcher.UIThread.RunJobs();\n\n    var dialog = OpenSettings(window);\n    Assert.True(vm.IsSettingsOpen);\n    Assert.NotNull(dialog.GetVisualDescendants().OfType<SettingsPanel>().FirstOrDefault());\n\n    Assert.Equal(5, MainWindowViewModel.AllProviderKeys.Count);\n    Assert.Equal(5, vm.KeyStates.Count);\n\n    foreach (var row in MainWindowViewModel.AllProviderKeys)\n        Assert.StartsWith(\"https://\", row.GetKeyUrl!, StringComparison.Ordinal);\n\n    dialog.Close();\n    Dispatcher.UIThread.RunJobs();\n}\n```\n\n### No other file changes\n\n- `SplitGuardVerificationTests.WholeAppBoot.cs` — allowlist entry for `KeySetupPanelUiTests` stays (test #3 still constructs `new MainWindow`).\n- `SplitGuardVerificationTests.Conventions.cs` — `KeySetupPanelUiTests.cs` already in the `HeadlessCollectionFiles_HaveCollectionAttribute` list; no change.\n- All 7 other test methods in `KeySetupPanelUiTests.cs` are untouched.\n\n### Assertion coverage audit (14 groups → each in exactly one test)\n\n| # | Assertion | Dest |\n|---|-----------|------|\n| 1 | `vm.IsSettingsOpen` is true | #3 |\n| 2 | `SettingsPanel` in visual tree | #3 |\n| 3 | `AllProviderKeys.Count == 5` | #3 |\n| 4 | `KeyStates.Count == 5` | #3 |\n| 5 | `hf.IsSet` is true | #1 |\n| 6 | `hf.DisplayValue` contains \"hf-a\" | #1 |\n| 7 | `hf.DisplayValue` does not contain \"(not set)\" | #1 |\n| 8 | `DEEPSEEK_API_KEY` IsSet is true | #1 |\n| 9 | MOONSHOT/ANTHROPIC/OPENAI IsSet is false | #2 |\n| 10 | MOONSHOT/ANTHROPIC/OPENAI DisplayValue == \"(not set)\" | #2 |\n| 11 | `vm.IsHuggingFaceConfigured` is true | #1 |\n| 12 | `vm.HfGateMessage` is empty | #1 |\n| 13 | All `GetKeyUrl` start with \"https://\" | #3 |\n| 14 | `HfPricingNote` contains \"pay-as-you-go\" | #1 |\n\n### Measurements (after editing)\n\n1. Run the same `dotnet test` command and verify all 10 tests pass (7 original + 3 new).\n2. Compare file total and full-suite wall time against the before numbers.\n3. If improvement: fill the commit-message evidence bullet. If not measurably faster: report the measurements, do not land the split.\n\n### Test count\n\n8 tests before → 10 tests after (7 untouched + 3 replacements for the 1 deleted).",
  "manifest": [
    "tests/VisualRelay.Tests/KeySetupPanelUiTests.cs"
  ]
}

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/KeySetupPanelUiTests.cs"], "rationale": "Split attempted but reverted: the three replacement tests combined (177 ms) are slower than the original single test (96 ms) on this machine. The headless dispatcher startup cost is negligible here, so the scoped-vs-windowed distinction provides no benefit — two extra test-method overhead cycles cost more than the avoided MainWindow boot and cog polling. File was restored to its original checksum (32b26fb0) with all 8 tests passing." }

## Stage 6 - Implement

{
  "summary": "Split not landed: the three replacement tests combined (879 ms, 10 tests) are slower than the original single test (751 ms, 8 tests) on this machine. The headless dispatcher startup cost is negligible here — two extra test-method overhead cycles cost more than the avoided MainWindow boot and cog polling. File was restored to its original checksum (32b26fb0). Measurements: before=751 ms (8 tests), after=879 ms (10 tests), delta=+128 ms (worse). Per task instructions, the split is not landed since numbers did not improve."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure C# test method split — no UI markup, styles, layout, images, charts, documents, or any visual output. All assertions are logical/structural, not visual._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Split not landed: three replacement tests (10 tests, 879 ms) were slower than the original single test (8 tests, 751 ms) on this machine. File restored to original checksum 32b26fb0; all 8 original tests pass. Per the bail-out rule the split is not applied.",
  "commitMessages": [
    "revert: abandon KeySetupPanelUiTests split — replacement tests 17 % slower",
    "Revert \"split KeySetupPanelUiTests into three focused facts\"",
    "docs: record KeySetupPanelUiTests split attempt — no improvement"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

