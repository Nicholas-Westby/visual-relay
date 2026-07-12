## Stage 1 - Ideate

{
  "summary": "Replace the literal \"your time\" in peak-pricing window headlines with the actual local time zone label (e.g. \"Pacific Time (Los Angeles)\") by stripping the `(UTC±HH:MM) ` prefix from `TimeZoneInfo.Local.DisplayName`. Three files need edits: add `LocalTimeZoneLabel()` and `StripUtcOffsetPrefix()` helpers in the view-model partial class (`internal static` for testability), call `LocalTimeZoneLabel()` in `BuildWindowHeadline`, add `TextWrapping=\"Wrap\"` in the AXAML control, and update/expand tests — a deterministic theory for the pure stripping function, a pattern pin for the 12-hour headline, and a fact that asserts the label appears and the old literal / PST8PDT alias do not.",
  "options": [
    "Prescribed: extract internal static helpers (LocalTimeZoneLabel, StripUtcOffsetPrefix), unit-test the pure function with InlineData theories, update the headline call site and XAML wrapping.",
    "Inline: compute the stripped label directly inside BuildWindowHeadline (no extracted methods), test only through PopulateModelCostRows integration-style.",
    "Private helpers: same extracted methods but private (no direct testability), validate headline output only through the public PopulateModelCostRows pathway."
  ]
}

## Stage 2 - Research

{ "findings": "Three target files exist at expected paths. MainWindowViewModel.CostPerModel.cs (187 lines) has the literal \"your time\" at line 148 inside BuildWindowHeadline's try branch; the catch branch (line 154) is untouched. AssemblyInfo.cs confirms InternalsVisibleTo(\"VisualRelay.Tests\"). CostPerModel.axaml (172 lines) has the Headline TextBlock at lines 113-114 needing TextWrapping=\"Wrap\". CostPerModelTests.Display.cs (128 lines) has 6 existing facts; the regex on line 100 must change \"your time\"→\".+\", the test at lines 115-127 must be replaced with UsesLocalZoneLabel, and a new StripUtcOffsetPrefix theory must be added. Three additional unused using statements (System.Text.RegularExpressions, System.Collections.ObjectModel) exist in Display.cs but the spec prohibits touching unrelated code — the Regex import is used by the existing test and should remain.", "constraints": ["Touch only three files: MainWindowViewModel.CostPerModel.cs, CostPerModel.axaml, CostPerModelTests.Display.cs", "No new packages, no new files", "No changes to RelayPricing, ModelCostRow, ModelCostWindowRow, BuildWindowSourceNote, ConvertWindowTimeToLocalDisplay, FormatRate, FormatRateRelativeToInput, TierOrderIndex", "BuildWindowHeadline catch branch must stay unchanged (keeps 'in {window.TimeZoneId}')", "Do not memoize the label in a static field", "No regex for prefix strip — use StartsWith/IndexOf slice", "No CultureInfo gymnastics around DisplayName — ICU localization is desired", "Do not reformat or restructure unrelated code in the touched files", "PopulateModelCostRows_WindowSourceNote_IsCorrect must still pass unchanged", "All other tests in CostPerModelTests.cs and CostPerModelTests.Display.cs must pass without modification", "No changes to process/test time zones (TZ/TimeZoneInfo.ClearCachedData)", "All three files must stay under 300 lines after edits"] }

## Stage 3 - Diagnose

{
  "evidence": "The bug is a literal string \"your time\" on line 148 of `MainWindowViewModel.CostPerModel.cs` in method `BuildWindowHeadline`. When time-of-day peak-pricing windows are displayed in the Settings → \"Cost Per LLM Model\" panel, headlines read like `6:00 PM – 9:00 PM your time — 2× peak pricing`. This is ambiguous — screenshots and support threads can't identify the viewer's time zone, and a user on an unexpected zone (e.g. CI machine set to UTC) has no indication.\n\nThe fix replaces \"your time\" with `LocalTimeZoneLabel()`, a new `internal static` helper that reads `TimeZoneInfo.Local.DisplayName` and strips the leading `(UTC±HH:MM) ` / `(UTC) ` prefix (e.g. `(UTC-08:00) Pacific Time (Los Angeles)` → `Pacific Time (Los Angeles)`). DisplayName is ICU-backed and resolves POSIX aliases like `PST8PDT` (the macOS development host's system zone id) to proper human names — the existing test `NeverContainsPlatformTimezoneId` was specifically written to keep `PST8PDT` out of headlines.\n\nThree files need changes, all currently under the 300-line guard:\n\n1. **`MainWindowViewModel.CostPerModel.cs`** (187 lines):\n   - Line 148: `{start} – {end} your time —` → `{start} – {end} {LocalTimeZoneLabel()} —`\n   - Add two `internal static` methods after line 156 (after `BuildWindowHeadline` closes): `LocalTimeZoneLabel()` (1 line wrapping `StripUtcOffsetPrefix`) and `StripUtcOffsetPrefix(string)` (~12 lines with `StartsWith`/`IndexOf` slice).\n   - Catch branch (line 154) stays untouched — it shows source-zone times with `in {window.TimeZoneId}`.\n   - `BuildWindowSourceNote` (lines 158-171), `ConvertWindowTimeToLocalDisplay` (173-180), and all rate helpers are untouched.\n\n2. **`CostPerModel.axaml`** (172 lines):\n   - Lines 113-114: The headline `TextBlock` has no `TextWrapping` attribute. The new label is ~17 characters longer than \"your time\" and will clip in a narrow settings pane. Add `TextWrapping=\"Wrap\"`.\n   - No other XAML changes — `Headline` is a plain bound string, so the view-model change flows through.\n\n3. **`CostPerModelTests.Display.cs`** (128 lines):\n   - Line 100: Regex must change from `your time` → `.+` to allow any zone label.\n   - Lines 115-127: Replace `PopulateModelCostRows_WindowHeadline_NeverContainsPlatformTimezoneId` with `PopulateModelCostRows_WindowHeadline_UsesLocalZoneLabel` — asserts the label appears, \"your time\" does not, and `PST8PDT` does not.\n   - Add a `[Theory]` with 6 `[InlineData]` cases for `StripUtcOffsetPrefix_StripsLeadingOffsetChunk` — covering normal prefix strip, UTC strip, no-prefix passthrough, and empty-after-strip passthrough.\n   - All other facts (lines 12-113) pass unchanged.\n\nConfirmed: `AssemblyInfo.cs` line 3 grants `[assembly: InternalsVisibleTo(\"VisualRelay.Tests\")]`, so `internal static` helpers are directly callable from tests.",
  "excerpts": [
    "BuildWindowHeadline line 148: `return $\"{start} – {end} your time — {window.Multiplier.ToString(\"0.#\", CultureInfo.InvariantCulture)}× peak pricing\";` — the literal \"your time\" must become `{LocalTimeZoneLabel()}`.",
    "CostPerModel.axaml lines 113-114: `<TextBlock Text=\"{Binding Headline}\" FontSize=\"11\" Foreground=\"#E0A458\"/>` — no `TextWrapping`; the longer label will clip.",
    "Test regex line 100: `@\"^\\d{1,2}:\\d{2} [AP]M – \\d{1,2}:\\d{2} [AP]M your time — 2× peak pricing$\"` — pins the literal \"your time\"; must loosen to `.+` for any zone label.",
    "Test lines 115-127: `PopulateModelCostRows_WindowHeadline_NeverContainsPlatformTimezoneId` asserts zone names never appear — obsolete since the headline now deliberately contains one; must be replaced with `UsesLocalZoneLabel`.",
    "AssemblyInfo.cs line 3: `[assembly: InternalsVisibleTo(\"VisualRelay.Tests\")]` — confirms test assembly can call `internal` helpers."
  ],
  "repro": "dotnet test tests/VisualRelay.Tests --filter FullyQualifiedName~CostPerModelTests"
}

## Stage 4 - Plan

{
  "plan": "## Concrete implementation plan\n\n### 1. `src/VisualRelay.App/ViewModels/MainWindowViewModel.CostPerModel.cs`\n\n**Edit A — line 148**: Replace `your time` with `{LocalTimeZoneLabel()}` in `BuildWindowHeadline`'s try branch:\n\n```csharp\n// OLD (line 148):\nreturn $\"{start} – {end} your time — {window.Multiplier.ToString(\"0.#\", CultureInfo.InvariantCulture)}× peak pricing\";\n// NEW:\nreturn $\"{start} – {end} {LocalTimeZoneLabel()} — {window.Multiplier.ToString(\"0.#\", CultureInfo.InvariantCulture)}× peak pricing\";\n```\n\nThe catch branch (line 154) is untouched.\n\n**Edit B — after line 156** (after `BuildWindowHeadline`'s closing `}` and blank line, before `BuildWindowSourceNote` at line 158): Insert two `internal static` methods:\n\n```csharp\n    /// <summary>Human-readable local time zone for the peak-window headline,\n    /// e.g. \"Pacific Time (Los Angeles)\". Derived from\n    /// <see cref=\"TimeZoneInfo.Local\"/>.DisplayName rather than .Id because the\n    /// Id can be a POSIX alias like \"PST8PDT\" (observed as the macOS system\n    /// zone), while the ICU-backed DisplayName resolves the alias to a proper\n    /// generic name.</summary>\n    internal static string LocalTimeZoneLabel() =>\n        StripUtcOffsetPrefix(TimeZoneInfo.Local.DisplayName);\n\n    /// <summary>Removes the leading \"(UTC±HH:MM) \" / \"(UTC) \" chunk from a\n    /// TimeZoneInfo.DisplayName. Returns the input unchanged when the prefix is\n    /// absent or nothing would remain after stripping.</summary>\n    internal static string StripUtcOffsetPrefix(string displayName)\n    {\n        if (displayName.StartsWith(\"(UTC\", StringComparison.Ordinal))\n        {\n            var close = displayName.IndexOf(')');\n            if (close >= 0)\n            {\n                var stripped = displayName[(close + 1)..].TrimStart();\n                if (stripped.Length > 0)\n                    return stripped;\n            }\n        }\n        return displayName;\n    }\n```\n\n### 2. `src/VisualRelay.App/Views/Controls/CostPerModel.axaml`\n\n**Edit — lines 113-114**: Add `TextWrapping=\"Wrap\"` to the Headline TextBlock:\n\n```xml\n<!-- OLD (lines 113-114): -->\n<TextBlock Text=\"{Binding Headline}\"\n           FontSize=\"11\" Foreground=\"#E0A458\"/>\n<!-- NEW: -->\n<TextBlock Text=\"{Binding Headline}\"\n           FontSize=\"11\" Foreground=\"#E0A458\"\n           TextWrapping=\"Wrap\"/>\n```\n\n### 3. `tests/VisualRelay.Tests/CostPerModelTests.Display.cs`\n\n**Edit A — line 100**: Replace the regex pattern to accept any zone label:\n\n```csharp\n// OLD:\n@\"^\\d{1,2}:\\d{2} [AP]M – \\d{1,2}:\\d{2} [AP]M your time — 2× peak pricing$\",\n// NEW:\n@\"^\\d{1,2}:\\d{2} [AP]M – \\d{1,2}:\\d{2} [AP]M .+ — 2× peak pricing$\",\n```\n\n**Edit B — lines 115-127**: Replace the entire `PopulateModelCostRows_WindowHeadline_NeverContainsPlatformTimezoneId` fact with:\n\n```csharp\n    [Fact]\n    public void PopulateModelCostRows_WindowHeadline_UsesLocalZoneLabel()\n    {\n        var vm = new MainWindowViewModel();\n        vm.PopulateModelCostRows();\n\n        var flash = vm.ModelCostRows.Single(r => r.ModelKey == \"deepseek-v4-flash\");\n        foreach (var w in flash.Windows)\n        {\n            Assert.Contains(MainWindowViewModel.LocalTimeZoneLabel(), w.Headline, StringComparison.Ordinal);\n            Assert.DoesNotContain(\"your time\", w.Headline, StringComparison.Ordinal);\n            Assert.DoesNotContain(\"PST8PDT\", w.Headline, StringComparison.Ordinal);\n        }\n    }\n```\n\n**Edit C — after line 128** (end of file): Append the pure-function theory:\n\n```csharp\n    [Theory]\n    [InlineData(\"(UTC-08:00) Pacific Time (Los Angeles)\", \"Pacific Time (Los Angeles)\")]\n    [InlineData(\"(UTC) Coordinated Universal Time\", \"Coordinated Universal Time\")]\n    [InlineData(\"(UTC+05:30) India Standard Time (Kolkata)\", \"India Standard Time (Kolkata)\")]\n    [InlineData(\"(UTC-05:00) GMT-05:00\", \"GMT-05:00\")]\n    [InlineData(\"Pacific Time\", \"Pacific Time\")]\n    [InlineData(\"(UTC-08:00)\", \"(UTC-08:00)\")]\n    public void StripUtcOffsetPrefix_StripsLeadingOffsetChunk(string input, string expected)\n    {\n        Assert.Equal(expected, MainWindowViewModel.StripUtcOffsetPrefix(input));\n    }\n```\n\n### Verification\n\n```bash\ndotnet test tests/VisualRelay.Tests --filter FullyQualifiedName~CostPerModelTests\n./visual-relay check\n```\n\nAll three files stay under 300 lines after edits (CostPerModel.cs ~210, CostPerModel.axaml ~173, CostPerModelTests.Display.cs ~145).",
  "manifest": [
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.CostPerModel.cs",
    "src/VisualRelay.App/Views/Controls/CostPerModel.axaml",
    "tests/VisualRelay.Tests/CostPerModelTests.Display.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 2.

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/CostPerModelTests.Display.cs"], "rationale": "All 26 tests pass. The three target files were modified exactly as specified: replaced 'your time' with LocalTimeZoneLabel() in BuildWindowHeadline, added LocalTimeZoneLabel/StripUtcOffsetPrefix helpers, added TextWrapping in AXAML, updated the regex pattern test, replaced NeverContainsPlatformTimezoneId with UsesLocalZoneLabel, and added StripUtcOffsetPrefix theory. The ./visual-relay check failure is pre-existing (JetBrains InspectCode cannot load System.Composition.AttributedModel) — build succeeded with 0 errors/warnings." }

## Stage 6 - Implement

{ "summary": "Three files edited: (1) MainWindowViewModel.CostPerModel.cs — replaced 'your time' with {LocalTimeZoneLabel()} on line 148, added LocalTimeZoneLabel() and StripUtcOffsetPrefix() internal static helpers after BuildWindowHeadline; (2) CostPerModel.axaml — added TextWrapping=\"Wrap\" to the Headline TextBlock; (3) CostPerModelTests.Display.cs — already updated from prior stage with UsesLocalZoneLabel fact, loosened regex, and StripUtcOffsetPrefix theory. All 26 tests pass, all files under 300 lines." }

## Stage 8 - Visual-review

{
  "verdict": "pass",
  "issues": []
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Replaced literal \"your time\" in peak-pricing headlines with the machine's actual time zone label (e.g. \"Pacific Time (Los Angeles)\") by stripping the `(UTC±HH:MM) ` prefix from `TimeZoneInfo.Local.DisplayName`. Three files were touched: (1) `MainWindowViewModel.CostPerModel.cs` — added `internal static` helpers `LocalTimeZoneLabel()` and `StripUtcOffsetPrefix()`, called the former in `BuildWindowHeadline`'s try branch; (2) `CostPerModel.axaml` — added `TextWrapping=\"Wrap\"` to the headline TextBlock; (3) `CostPerModelTests.Display.cs` — updated the 12-hour-pattern regex to accept `.+” instead of \"your time\", replaced the obsolete `NeverContainsPlatformTimezoneId` fact with `UsesLocalZoneLabel` asserting the label appears and old literal/alias don't, and added a parameterized `StripUtcOffsetPrefix` theory with 6 cases covering normal, UTC, no-prefix, and degenerate inputs. All 3029 tests pass (0 failed, 113 skipped pre-existing). All three files remain under the 300-line guard.",
  "commitMessages": [
    "feat: show actual local time zone instead of 'your time' in peak-pricing headlines",
    "feat(CostPerModel): derive human-readable zone label from TimeZoneInfo.Local.DisplayName",
    "fix: make peak-window headlines self-describing by naming the machine's time zone"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

