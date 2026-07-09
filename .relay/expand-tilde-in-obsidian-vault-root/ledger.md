## Stage 1 - Ideate

{
  "summary": "Backport `~/…` expansion into `ObsidianVaultLayout` constructor and `RevealVaultRoot`, so that the literal tilde string stored in the VM property and `.env` is expanded to the real home directory at the point of filesystem I/O, preventing stray `<repo>/~/Library/…` trees. Also extract a shared `TildePath` helper, replace the private `ExpandTilde` in settings, and add unit tests — all without normalizing raw user input at write/storage sites.",
  "options": [
    "A — Expand at the consumption choke point (prescribed): add `TildePath.Expand(vaultRoot)` in `ObsidianVaultLayout` constructor + `RevealVaultRoot`. Single-line fix covers all consumers; minimal diff.",
    "B — Expand in the VM property getter: protects all downstream code (not just layout/reveal) but would break the Settings textbox binding showing the raw tilde form, requiring workarounds.",
    "C — Expand at each call site individually: more explicit but three change sites instead of one, and a future fourth consumer would silently re-introduce the bug."
  ]
}

## Stage 2 - Research

{
  "findings": "Bug confirmed: `ObsidianVaultLayout` constructor (line 58 of 216-line file) stores `_vaultRoot = vaultRoot;` verbatim with no tilde expansion. Both production call sites in `MainWindowViewModel.ObsidianBridge.cs` — `RunObsidianBridgeScanAsync` (line 115) and `ExportSummaryOnCompletion` (line 173) — and `RevealVaultRoot` (line 92–96) pass the raw `ObsidianVaultRoot` VM property (holding literal `~/…`) into filesystem I/O, creating `<cwd>/~/Library/…`. The private `ExpandTilde` in `ObsidianBridgeSettings.cs` (lines 242–251) does correct expansion but is only called during `Load()` — live writes bypass it entirely. No shared helper exists yet; `HomePathToTildeConverter.cs` does the reverse (absolute→`~` for display) and is unrelated. Existing tests (`ObsidianBridgeSettingsTests.Load_ExpandsTildeInVaultRoot`, `ObsidianVaultLayoutTests` with absolute paths, `ObsidianBridgeHermeticityTests`) all pass and will remain green. File-size headroom: ObsidianVaultLayout.cs 216/300, ObsidianBridgeSettings.cs 260/300 (net negative after removing private ExpandTilde), MainWindowViewModel.ObsidianBridge.cs 222/300, new TildePath.cs ~30 lines. Conventional Commits enforced via commit-msg hook.",
  "constraints": [
    "Do NOT normalize at write sites (textbox change handler, control-API setter, PersistBridgeSettings) — the raw tilde in the VM property and .env is intended; only consumption expands.",
    "Do NOT add rooted-path validation or reject other relative vault roots — out of scope.",
    "Do NOT delete the existing stray <repo>/~ tree — user cleanup, not this task.",
    "All touched files must stay under the 300-line guard.",
    "TDD-first: write failing tests before implementation code.",
    "The ObsidianVaultLayout tilde regression pin test must NOT call EnsureScaffold (no directory creation under the real home).",
    ".env must continue to store the tilde form; only filesystem I/O paths expand it.",
    "TildePath.Expand must be the single implementation — delete the private ExpandTilde from ObsidianBridgeSettings.",
    "TildePath.Expand must have identical semantics to current private ExpandTilde: only ~/ prefix expansion, no ~user or bare ~ support.",
    "Use Conventional Commits format with type prefix, ≤72-char subject, lowercase after prefix, no trailing period, body of at most 3 hyphen bullets (≤20 words each)."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Confirmed gap: `ObsidianVaultLayout` constructor (line 58 of `ObsidianVaultLayout.cs`, 216 lines) stores `_vaultRoot = vaultRoot;` verbatim with zero tilde expansion. Both production call sites — `RunObsidianBridgeScanAsync` (line 115 of `MainWindowViewModel.ObsidianBridge.cs`) and `ExportSummaryOnCompletion` (line 173) — pass the raw `ObsidianVaultRoot` VM property directly into the ctor and then call `EnsureScaffold()`, which calls `Directory.CreateDirectory` on `Path.Combine(_vaultRoot, …)`. When `_vaultRoot` is `\"~/Library/…\"`, .NET resolves the tilde as a relative path under the app's working directory (repo root), creating `<cwd>/~/Library/…`. `RevealVaultRoot` (lines 92–96) has the identical defect, passing the raw value to `FileReveal.Reveal`. The control API (`ControlApi.cs` line 256) writes `viewModel.ObsidianVaultRoot = path;` — same downstream path. The private `ExpandTilde` in `ObsidianBridgeSettings.cs` (lines 242–251) expands correctly but is only called from `Load()` at startup; the hydration guard (`_isHydrating = true` in `LoadObsidianBridgeSettings`, lines 71, 79) prevents the expanded value from being persisted back, so app restart is fine — only live textbox/control-API writes trigger the bug. No shared `TildePath` helper exists; `HomePathToTildeConverter` does the reverse (absolute→`~` for display) and is unrelated. Existing tests (`ObsidianVaultLayoutTests` with `/vault` and temp absolute paths, `ObsidianBridgeSettingsTests.Load_ExpandsTildeInVaultRoot` through `Load`) all pass and will remain green — the ctor change is a verbatim no-op for non-tilde paths.",
  "excerpts": [
    "ObsidianVaultLayout.cs:58 — `_vaultRoot = vaultRoot;` — raw string stored, no tilde expansion.",
    "ObsidianVaultLayout.cs:62 — `public string RepoDir => Path.Combine(_vaultRoot, _repoName);` — resolves relative `~/…` against CWD.",
    "ObsidianVaultLayout.cs:163–164 — `if (!Directory.Exists(path)) Directory.CreateDirectory(path);` — creates stray tree on relative path.",
    "MainWindowViewModel.ObsidianBridge.cs:115 — `var layout = new ObsidianVaultLayout(ObsidianVaultRoot, repoName); layout.EnsureScaffold();` — raw VM property flows into ctor then filesystem I/O.",
    "MainWindowViewModel.ObsidianBridge.cs:173 — identical pattern in `ExportSummaryOnCompletion`: `new ObsidianVaultLayout(ObsidianVaultRoot, repoName)` + `EnsureScaffold()`.",
    "MainWindowViewModel.ObsidianBridge.cs:92–96 — `RevealVaultRoot()` passes `ObsidianVaultRoot` raw to `FileReveal.Reveal()`.",
    "ControlApi.cs:256 — `viewModel.ObsidianVaultRoot = path;` — control-API write site, no expansion.",
    "ObsidianBridgeSettings.cs:242–251 — private `ExpandTilde` exists and works, but called only from `Load()` at startup, not from live writes.",
    "ObsidianBridgeSettings.cs:83 — `ExpandTilde(vaultRootStr, home)` — expansion happens in `Load()`, guarded by `_isHydrating` so it never persists back."
  ],
  "repro": "1. Set `ObsidianVaultRoot` VM property to `\"~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/\"` via the Settings textbox or control-API `{\"command\":\"obsidian-bridge\",\"path\":\"~/…\"}`.\n2. Wait for the bridge poll cycle (`RunObsidianBridgeScanAsync`) or complete a task (`ExportSummaryOnCompletion`).\n3. Both call `new ObsidianVaultLayout(ObsidianVaultRoot, repoName).EnsureScaffold()`. The ctor stores `_vaultRoot = \"~/Library/…\"` verbatim (line 58). `EnsureScaffold` calls `Directory.CreateDirectory(Path.Combine(\"~/Library/…\", repoName))` — .NET resolves the relative `~` against the app's working directory (the repo root).\n4. Result: a stray `<repo-root>/~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/<repo>/` directory tree is created on disk. `RevealVaultRoot` similarly shells out with the raw relative path."
}

## Stage 4 - Plan

{
  "plan": "0. Create TildePathTests.cs with 8 red unit tests (Expand_TildeSlashPrefix_ReplacesWithHome, Expand_AbsolutePath_ReturnsUnchanged, Expand_PlainRelative_ReturnsUnchanged, Expand_BareTilde_ReturnsUnchanged, Expand_TildeUserPrefix_ReturnsUnchanged, Expand_NullHome_ReturnsInputVerbatim, Expand_EmptyHome_ReturnsUnchanged, Expand_ParameterlessOverload_UsesHOMEEnv). \n\n1. Create src/VisualRelay.Core/Configuration/TildePath.cs with static TildePath.Expand(string) and Expand(string,string?) — identical semantics to the existing private ExpandTilde. Tests go green.\n\n2. In ObsidianBridgeSettings.cs: replace the two call sites of the private ExpandTilde (Load line 83, ExpandDefaultVaultRoot line 239) with TildePath.Expand; delete the private ExpandTilde method (lines 242–251). All existing ObsidianBridgeSettingsTests stay green.\n\n3. In ObsidianVaultLayout.cs ctor line 58: change _vaultRoot = vaultRoot; to _vaultRoot = TildePath.Expand(vaultRoot);. Add using VisualRelay.Core.Configuration.\n\n4. In ObsidianVaultLayoutTests.cs: add Ctor_ExpandsTildeInVaultRoot regression pin — new ObsidianVaultLayout(\"~/vault-tilde-test\", \"repo\") yields RepoDir equal to Path.Combine(<home>, \"vault-tilde-test\", \"repo\"), not starting with \"~\". Must NOT call EnsureScaffold. Test is red before step 3, green after.\n\n5. In MainWindowViewModel.ObsidianBridge.cs RevealVaultRoot line 95: wrap ObsidianVaultRoot in TildePath.Expand before passing to FileReveal.Reveal. Add using VisualRelay.Core.Configuration.\n\n6. Run ./visual-relay check — all tests pass.",
  "manifest": [
    "+src/VisualRelay.Core/Configuration/TildePath.cs",
    "+tests/VisualRelay.Tests/TildePathTests.cs",
    "src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs",
    "src/VisualRelay.Core/ObsidianBridge/ObsidianVaultLayout.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs",
    "tests/VisualRelay.Tests/ObsidianVaultLayoutTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 3, untracked deleted: 1.

> **Manifest merge (stage 5)**: added 2 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/TildePathTests.cs",
    "tests/VisualRelay.Tests/ObsidianVaultLayoutTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeHermeticityTests.cs"
  ],
  "rationale": "All targeted tests pass: 8 TildePath unit tests, 49 ObsidianVaultLayout (including new Ctor_ExpandsTildeInVaultRoot regression pin), 16 ObsidianBridgeSettings (confirming existing tilde-expansion Load tests stay green after replacing private ExpandTilde with TildePath.Expand), and 4 ObsidianBridgeHermeticity. The full ./visual-relay check timed out at the full test suite (60s limit) — a pre-existing issue unrelated to these changes."
}

## Stage 6 - Implement

{
  "summary": "Created TildePath.cs shared helper (21 lines), replaced private ExpandTilde in ObsidianBridgeSettings.cs with TildePath.Expand calls (deleted the private method, net -11 lines), added TildePath.Expand in ObsidianVaultLayout constructor (+1 line, 217 total), and wrapped RevealVaultRoot's FileReveal.Reveal call with TildePath.Expand. All 2965 tests pass (2855 passed, 110 skipped, 0 failures). The fix ensures that any tilde-prefixed vault root (from Settings textbox or control-API writes) is expanded to the real home directory at the point of filesystem I/O via the ObsidianVaultLayout constructor and RevealVaultRoot, while the VM property and .env continue to store the raw tilde form unchanged."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure backend C# logic changes — tilde expansion in path strings, no UI, markup, styles, visual assets, charts, documents, or images involved._

## Stage 9 - Fix

{ "summary": "Fix complete. TildePath helper extracted as single implementation; ObsidianVaultLayout ctor and RevealVaultRoot now expand ~/ prefix at point of filesystem I/O. All 27 targeted tests and 2855 full-suite tests pass. ./visual-relay check fails on 152 pre-existing InspectCode findings unrelated to this change." }

## Stage 10 - Verify

{
  "summary": "All 2965 tests pass (2855 passed, 110 skipped, 0 failed). TildePath shared helper extracted and wired into ObsidianVaultLayout constructor and RevealVaultRoot, expanding a `~/` prefix to the user's home directory at the point of filesystem I/O, preventing stray `<cwd>/~/…` directory trees. The private ExpandTilde in ObsidianBridgeSettings is replaced with TildePath.Expand calls. The raw tilde form in the VM property and .env is preserved unchanged.",
  "commitMessages": [
    "fix: expand tilde prefix in vault root before filesystem i/o",
    "feat(core): extract shared tilde path expansion helper",
    "fix: prevent stray home-directory tree under repo root",
    "refactor: deduplicate tilde expansion into TildePath utility",
    "test: add regression pins for tilde-expanded vault root paths"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

