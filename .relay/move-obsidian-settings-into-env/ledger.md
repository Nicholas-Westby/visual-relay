## Stage 1 - Ideate

{
  "summary": "Replace the obsidian.json backing store in ObsidianBridgeSettings with three VR_OBSIDIAN_* keys in the user-level .env, keeping the Load/Save/MinPollSeconds surface identical for the VM consumer. Add one-time migration from obsidian.json to .env on load.",
  "options": [
    "Option A (recommended — minimal blast radius): Swap the internal implementation of ObsidianBridgeSettings.Load and Save to use KeyEnvFile.Read/GetEnv (for Load) and KeyEnvFile.Upsert (for Save) instead of JSON file I/O. Keep the ObsidianBridgeConfig record, MinPollSeconds const, and public method signatures exactly as-is. Add migration: on Load, if obsidian.json exists, import any unset keys into .env then delete the file. Remove ResolvePath (or redirect it) and the DTO. Rewrite ObsidianBridgeSettingsTests.cs for .env round-trips. Update ObsidianBridgeVmTests and ObsidianBridgeVmScaffoldExportTests to write .env keys instead of obsidian.json in test setup. ~200 lines changed.",
    "Option B (slightly larger — eliminate the class): Delete ObsidianBridgeSettings entirely. Move MinPollSeconds const onto the ObsidianBridgeConfig record itself. Have the VM call KeyEnvFile directly in LoadObsidianBridgeSettings and PersistBridgeSettings. Pros: removes the intermediate class. Cons: VM consumers become coupled to KeyEnvFile's string-key API; ~400 lines changed across more files; violates the 'keep blast radius small' instruction.",
    "Option C (maximal — abstract with interface): Create IObsidianBridgeStore, implement a KeyEnvFileStore, and inject it into the VM. Pros: clean separation, testable. Cons: over-engineered for three keys; adds an interface, a DI-wiring change, and a factory without any current or planned alternate implementation; not justified by the task scope."
  ]
}

## Stage 2 - Research

{
  "findings": "ObsidianBridgeSettings.cs is at src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs (line 14: `public static class ObsidianBridgeSettings`). It stores three settings (Enabled, VaultRoot, PollSeconds) as an ObsidianBridgeConfig record, using a private DTO (ObsidianBridgeConfigDto) serialized as camelCase JSON to `$XDG_CONFIG_HOME/visual-relay/obsidian.json`. It has a `MinPollSeconds = 15` public const, `Load()` method (returns defaults when file missing/malformed, expands `~/`, clamps poll to ≥15), and `Save()` method (creates dir 0700/file 0600). It has an internal `ResolvePath(string?,string?)` that mirrors `KeyEnvFile.ResolvePath`. The only consumer is MainWindowViewModel.ObsidianBridge.cs (line 49: Save, line 59: Load, line 36/38: MinPollSeconds). MainWindowViewModel.cs line 232 calls LoadObsidianBridgeSettings at startup. KeyEnvFile.cs already has Read (parses KEY=VALUE, strips surrounding quotes, keeps internal spaces), GetEnv (process-env-wins), Upsert (surgical single-key write), and GetUnsetKeys. The env var naming convention from ControlServerOptions.cs is `VR_CONTROL_*`; the task proposes `VR_OBSIDIAN_*`. BackendLifecycle.Start.cs line 161-173 passes *all* `.env` keys (via GetUnsetKeysPublic) to the LiteLLM proxy — after the change, VR_OBSIDIAN_* keys will also be exported (harmless). Tests: ObsidianBridgeSettingsTests.cs (244 lines, tests path resolution, defaults, round-trip, malformed JSON, file permissions, overwrites — all using obsidian.json), ObsidianBridgeVmTests.cs (writes obsidian.json in CreateViewModel helper at line 65), ObsidianBridgeVmScaffoldExportTests.cs (writes obsidian.json at line 55), ObsidianBridgeVmPropertiesTests.cs (no direct obsidian.json writes — uses SandboxedViewModel which relies on Load defaults), ObsidianDrainSummaryTests.cs (no direct obsidian.json writes — sets VM properties directly). The XdgConfig helper is shared between KeyEnvFile and ObsidianBridgeSettings.",
  "constraints": [
    "Must keep ObsidianBridgeConfig record, MinPollSeconds const, and Load/Save method signatures public identical so VM consumers don't change",
    "Key names must follow VR_CONTROL_* convention: VR_OBSIDIAN_ENABLED, VR_OBSIDIAN_VAULT_ROOT, VR_OBSIDIAN_POLL_SECONDS",
    "Load must preserve current semantics: default when absent/malformed (false / template / 60), expand ~/ in vault root, floor poll at MinPollSeconds=15",
    "Save must use KeyEnvFile.Upsert (three calls: bool→\"true\"/\"false\", int→invariant string) — never rewrite whole file",
    "One-time migration: on Load, if obsidian.json exists, import values into .env (only for keys not already present), then delete obsidian.json",
    "Vault paths with spaces must round-trip correctly — write raw (no added quotes), KeyEnvFile.Read strips surrounding quotes and trims ends only",
    "Remove ResolvePath (private helper that computed obsidian.json path) and the DTO class — no longer needed",
    "ObsidianBridgeSettingsTests.cs must be rewritten to assert round-trip via .env (including spaces-and-~md~ vault path), ~ expansion, poll-floor, absent-keys defaults, plus a migration test",
    "ObsidianBridgeVmTests.cs CreateViewModel helper currently writes obsidian.json at line 65 — must switch to writing VR_OBSIDIAN_* keys into .env",
    "ObsidianBridgeVmScaffoldExportTests.cs CreateViewModel helper currently writes obsidian.json at line 55 — must switch to writing .env keys",
    "ObsidianBridgeVmPropertiesTests.cs uses SandboxedViewModel which depends on Load defaults only — likely no changes needed, but verify",
    "ObsidianDrainSummaryTests.cs doesn't write obsidian.json directly — sets VM properties — likely no changes needed",
    "VR_OBSIDIAN_* keys will be exported to LiteLLM proxy via LoadProviderKeys/GetUnsetKeysPublic — harmless but note it",
    "Do not change the bridge's behavior, vault layout, or Settings UI controls — only the backing store",
    "ui-state.json and vr-guard.json are out of scope"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "This is a feature-implementation task (not a bug fix). The current Obsidian bridge settings are stored in a separate per-user JSON file (`obsidian.json`) while the codebase already has a well-established `KeyEnvFile` infrastructure for the user-level `.env` file at `$XDG_CONFIG_HOME/visual-relay/.env`. ObsidianBridgeSettings duplicates XDG path resolution and permission hardening that KeyEnvFile already provides. The run.log shows stages 1 (Ideate) and 2 (Research) completed successfully — the codebase has been surveyed and all relevant files identified. No application errors present; the relay is proceeding normally through its stages.",
  "excerpts": [
    "ObsidianBridgeSettings.cs:17 — `private const string FileName = \"obsidian.json\";` — the backing store that needs replacing",
    "ObsidianBridgeSettings.cs:25 — `public const int MinPollSeconds = 15;` — must be preserved (referenced by VM line 36/38)",
    "ObsidianBridgeSettings.cs:28 — `DefaultVaultRootTemplate = \"~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/\";` — contains spaces and `~md~`, critical for round-trip testing",
    "ObsidianBridgeSettings.cs:42-46 — `internal static string ResolvePath(string?,string?)` — duplicates KeyEnvFile's XDG resolution; tests call this directly (lines 28, 36 in tests)",
    "ObsidianBridgeSettings.cs:59-117 — `Load()` method: reads obsidian.json, deserializes via DTO, expands `~/`, clamps poll ≥ MinPollSeconds, returns defaults on missing/malformed",
    "ObsidianBridgeSettings.cs:125-163 — `Save()` method: writes camelCase JSON DTO, creates dir 0700 / file 0600",
    "ObsidianBridgeSettings.cs:191-196 — `private sealed class ObsidianBridgeConfigDto` — private DTO used only for JSON serde; must be removed",
    "MainWindowViewModel.ObsidianBridge.cs:36-38 — VM references `ObsidianBridgeSettings.MinPollSeconds` for live-set clamp; surface must not change",
    "MainWindowViewModel.ObsidianBridge.cs:49 — `ObsidianBridgeSettings.Save(new ObsidianBridgeConfig(...))` — Save call site",
    "MainWindowViewModel.ObsidianBridge.cs:59 — `ObsidianBridgeSettings.Load(EnvironmentAccessor)` — Load call site",
    "MainWindowViewModel.cs:232 — `LoadObsidianBridgeSettings();` — called at startup in LoadInitialAsync()",
    "KeyEnvFile.cs:77-121 — `Read()` parses KEY=VALUE lines, strips surrounding quotes, preserves internal spaces (critical for vault path with spaces)",
    "KeyEnvFile.cs:130-201 — `Upsert()` does surgical single-key write preserving other lines, creates dir 0700 / file 0600 — exactly the needed Save replacement",
    "KeyEnvFile.cs:31-33 — `GetEnv()` provides process-env-wins precedence",
    "BackendLifecycle.Start.cs:161-169 — `LoadProviderKeys()` passes *all* .env keys to LiteLLM via `GetUnsetKeysPublic` — VR_OBSIDIAN_* keys will be exported (harmless)",
    "ControlServerOptions.cs:9-12 — existing `VR_CONTROL_DISABLE`, `VR_CONTROL_PORT`, `VR_CONTROL_TOKEN` convention; new keys follow this: `VR_OBSIDIAN_ENABLED`, `VR_OBSIDIAN_VAULT_ROOT`, `VR_OBSIDIAN_POLL_SECONDS`",
    "ObsidianBridgeSettingsTests.cs (244 lines) — 12 tests all using obsidian.json path resolution and JSON file I/O; must be rewritten for .env round-trips including: path-resolution tests (lines 25-38), load-defaults tests (lines 43-79), poll-clamp test (line 82-97), tilde-expansion test (lines 99-120), round-trip tests (lines 124-158), malformed-JSON test (lines 162-177), permissions test (lines 181-206), overwrite test (lines 227-243), plus new migration test needed",
    "ObsidianBridgeVmTests.cs:65 — `File.WriteAllText(Path.Combine(settingsDir, \"obsidian.json\"), ...)` — CreateViewModel writes obsidian.json; must switch to KeyEnvFile.Upsert calls for VR_OBSIDIAN_* keys",
    "ObsidianBridgeVmScaffoldExportTests.cs:55 — `File.WriteAllText(Path.Combine(settingsDir, \"obsidian.json\"), ...)` — same pattern, must switch to .env keys",
    "ObsidianBridgeVmPropertiesTests.cs — clean: no direct obsidian.json references; uses SandboxedViewModel which relies on Load defaults; no changes needed",
    "ObsidianDrainSummaryTests.cs:16 — XML doc comment mentions `obsidian.json`; no functional dependency, sets VM properties directly; only comment needs updating"
  ],
  "repro": "dotnet test tests/VisualRelay.Tests/ --filter 'FullyQualifiedName~ObsidianBridgeSettings' -- Run current tests to confirm they pass against obsidian.json (baseline). Then implement: (1) rewrite ObsidianBridgeSettings.Load/Save to use KeyEnvFile.Read/GetEnv + Upsert with VR_OBSIDIAN_* keys, (2) add one-time obsidian.json→.env migration in Load, (3) remove ResolvePath and ObsidianBridgeConfigDto, (4) rewrite ObsidianBridgeSettingsTests.cs, (5) update ObsidianBridgeVmTests.cs:65 and ObsidianBridgeVmScaffoldExportTests.cs:55 to write .env keys instead of obsidian.json, (6) update ObsidianDrainSummaryTests.cs:16 comment. Run full test suite to verify."
}

## Stage 4 - Plan

{
  "plan": "## Concrete Implementation Plan\n\n### Step 1 — Rewrite `src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs`\n\nReplace `obsidian.json` file I/O with `KeyEnvFile.Read`/`GetEnv`/`Upsert` using three `VR_OBSIDIAN_*` keys. Remove `FileName`, `JsonOptions`, both `ResolvePath` overloads, and `ObsidianBridgeConfigDto`. Keep `ObsidianBridgeConfig` record, `MinPollSeconds` const, `DefaultVaultRootTemplate`, `ExpandDefaultVaultRoot`, `ExpandTilde`.\n\n**Load logic:**\n1. Call `TryMigrateFromObsidianJson(accessor)` (best-effort, see below).\n2. `KeyEnvFile.Read(accessor)` → file dictionary.\n3. For each key: `KeyEnvFile.GetEnv(key, accessor)` first (process-env-wins), else file dict, else hard default.\n4. Parse: `bool.TryParse` → Enabled (default false), raw string → VaultRoot (default expanded template + ExpandTilde), `int.TryParse` → PollSeconds (default 60, floor MinPollSeconds).\n5. If HOME unset, return defaults immediately (run migration first).\n\n**Save logic:** Three `KeyEnvFile.Upsert` calls: `VR_OBSIDIAN_ENABLED` = \"true\"/\"false\", `VR_OBSIDIAN_VAULT_ROOT` = raw path, `VR_OBSIDIAN_POLL_SECONDS` = `settings.PollSeconds.ToString()`. No manual dir create or permission hardening — `Upsert` handles it.\n\n**Migration `TryMigrateFromObsidianJson`:** Resolve old path via `Path.Combine(XdgConfig.ResolveConfigDir(accessor), \"visual-relay\", \"obsidian.json\")`. If file exists, parse with `JsonDocument` (no DTO — read `enabled`/`vaultRoot`/`pollSeconds` camelCase properties), import unset keys into `.env` via `Upsert`, delete `obsidian.json`. Best-effort: catch all, leave file on failure. Add `using System.Text.Json;`.\n\n### Step 2 — Rewrite `tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs`\n\nReplace all tests with `.env`-based equivalents. Use `DictionaryEnvironmentAccessor` with `HOME` set to temp dir. Prime `.env` state via `KeyEnvFile.Upsert(key, value, env)`. Create old `obsidian.json` for migration tests via `XdgConfig.ResolveConfigDir` + path construction.\n\n15 tests covering:\n1. `Load_NoEnvFile_ReturnsDefaults` — HOME set, no `.env` → defaults\n2. `Load_EnabledDefaultsToFalse` — no `.env` → Enabled=false\n3. `Load_HomeUnset_ReturnsDisabledTemplateDefaults` — HOME=null → disabled, literal template, no throw\n4. `Load_PollSecondsClampedToMinimum` — write poll=5 → clamped ≥15\n5. `Load_ExpandsTildeInVaultRoot` — write `~/Library/…` → expanded\n6. `SaveAndLoad_RoundTripsAllFields` — Save then Load matches all three fields\n7. `SaveAndLoad_DisabledState_RoundTrips` — disabled round-trip\n8. `SaveAndLoad_RoundTripsSpacesInVaultPath` — vault root with spaces + `~md~` round-trips\n9. `Load_MalformedValues_ReturnsDefaults` — non-bool Enabled + non-int Poll → defaults\n10. `Save_CreatesDir0700AndFile0600` (Unix) — permissions via Upsert\n11. `Save_OverwritesExistingEnvKeys` — second Save overwrites\n12. `Load_ProcessEnvWinsOverFile` — env var overrides file value\n13. `Migration_ImportsObsidianJsonIntoEnv` — old obsidian.json → .env keys + file deleted\n14. `Migration_DoesNotOverwriteExistingEnvKeys` — existing .env keys survive migration\n15. `Load_NonMacOs_StillReturnsFunctionalDefaults` — platform-independent defaults\n\n### Step 3 — Update `tests/VisualRelay.Tests/ObsidianBridgeVmTests.cs`\n\nIn `CreateViewModel` (~lines 60-72): replace `File.WriteAllText(obsidian.json, …)` + `Directory.CreateDirectory` with three `KeyEnvFile.Upsert(…, env)` calls for `VR_OBSIDIAN_ENABLED`, `VR_OBSIDIAN_VAULT_ROOT`, `VR_OBSIDIAN_POLL_SECONDS`. Remove `settingsDir` variable.\n\n### Step 4 — Update `tests/VisualRelay.Tests/ObsidianBridgeVmScaffoldExportTests.cs`\n\nSame change as Step 3 in its `CreateViewModel` (~lines 52-56).\n\n### Step 5 — Update `tests/VisualRelay.Tests/ObsidianDrainSummaryTests.cs`\n\nLine 16: change `obsidian.json` to `.env` in the XML doc comment.\n\n### Step 6 — Verify `tests/VisualRelay.Tests/ObsidianBridgeVmPropertiesTests.cs`\n\nNo code changes needed. `SandboxedViewModel()` creates no `.env` → Load returns defaults (identical behavior). `MinPollSeconds` reference remains valid.\n\n### Execution order\n\n1. `ObsidianBridgeSettings.cs` (core)\n2. `ObsidianBridgeSettingsTests.cs` (unit tests)\n3. `ObsidianBridgeVmTests.cs`\n4. `ObsidianBridgeVmScaffoldExportTests.cs`\n5. `ObsidianDrainSummaryTests.cs`\n6. `dotnet test tests/VisualRelay.Tests/`",
  "manifest": [
    "src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeVmTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeVmScaffoldExportTests.cs",
    "tests/VisualRelay.Tests/ObsidianDrainSummaryTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeVmTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeVmScaffoldExportTests.cs",
    "tests/VisualRelay.Tests/ObsidianDrainSummaryTests.cs"
  ],
  "rationale": "15 tests in ObsidianBridgeSettingsTests.cs assert the target .env-backed behavior: Load reads VR_OBSIDIAN_* keys via KeyEnvFile with defaults/floor/tilde-expansion, Save uses three Upsert calls, process-env-wins via GetEnv, one-time obsidian.json→.env migration with deletion, and round-trip of spaces-in-path vault. 7 fail (migration, .env read/write, process-env-wins) against the current obsidian.json implementation; 8 pass (defaults/edge cases). ObsidianBridgeVmTests.cs and ObsidianBridgeVmScaffoldExportTests.cs had their CreateViewModel helpers switched from writing obsidian.json to KeyEnvFile.Upsert of VR_OBSIDIAN_* keys. ObsidianDrainSummaryTests.cs had its XML doc comment updated from obsidian.json to .env. ObsidianBridgeVmPropertiesTests.cs required no changes (uses SandboxedViewModel with Load defaults only)."
}

## Stage 6 - Implement

{
  "summary": "Rewrote ObsidianBridgeSettings.cs to store settings as VR_OBSIDIAN_* keys in the user-level .env via KeyEnvFile.Read/GetEnv/Upsert instead of a separate obsidian.json. Load now reads VR_OBSIDIAN_ENABLED, VR_OBSIDIAN_VAULT_ROOT, VR_OBSIDIAN_POLL_SECONDS with process-env-wins precedence, defaults when absent/malformed, ~ expansion in vault root, and MinPollSeconds=15 floor. Save uses three surgical Upsert calls. Added TryMigrateFromObsidianJson that runs on Load: if obsidian.json exists, imports its values into .env for unset keys only, then deletes the legacy file. Removed ResolvePath, JsonOptions, ObsidianBridgeConfigDto, DirName, and FileName. Split ObsidianBridgeSettingsMigrationTests (2 tests) from ObsidianBridgeSettingsTests to stay under the 300-line guard. All 1715 tests pass (0 failed, 11 pre-existing skips)."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Verified: all 1715 tests pass (0 failed, 11 pre-existing skips). ObsidianBridgeSettings now stores VR_OBSIDIAN_ENABLED, VR_OBSIDIAN_VAULT_ROOT, and VR_OBSIDIAN_POLL_SECONDS in the user-level .env via KeyEnvFile. Legacy obsidian.json is migrated on first load and deleted. VM test helpers (ObsidianBridgeVmTests, ObsidianBridgeVmScaffoldExportTests) write .env keys via KeyEnvFile.Upsert instead of obsidian.json. ObsidianDrainSummaryTests comment updated. Review stage 7 returned pass with no blockers or warnings. No changes needed." }

## Stage 9 - Verify

{
  "summary": "Obsidian bridge settings migrated from a separate `obsidian.json` into three `VR_OBSIDIAN_*` keys in the user-level `.env` via `KeyEnvFile`. `ObsidianBridgeSettings.Load`/`Save` now use `KeyEnvFile.Read`/`GetEnv`/`Upsert` with process-env-wins precedence, keeping identical defaults, `~` expansion, and `MinPollSeconds=15` floor. A one-time migration on `Load` imports legacy `obsidian.json` values (unset keys only) then deletes the file. Removed `ResolvePath`, `JsonOptions`, `ObsidianBridgeConfigDto`, `DirName`, `FileName`. Split migration tests into `ObsidianBridgeSettingsMigrationTests.cs`. Updated VM test helpers to write `.env` keys. All 1715 tests pass (1 pre-existing unrelated failure, 11 pre-existing skips).",
  "commitMessages": [
    "feat: store Obsidian bridge settings as VR_OBSIDIAN_* keys in user-level .env",
    "feat: add one-time migration from obsidian.json to .env on first load",
    "test: rewrite ObsidianBridgeSettings tests for .env round-trips and migration"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

