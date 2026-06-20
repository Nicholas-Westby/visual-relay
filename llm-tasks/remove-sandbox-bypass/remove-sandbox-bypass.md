# Remove the sandbox-bypass option entirely — the nono sandbox is non-negotiable

Some stages run on cheap, low-capability LLM models. Letting them run **outside** the OS
sandbox is too dangerous, so the user-facing "Bypass nono sandbox" checkbox and the **entire
`bypassSandbox` capability** must go. After this task, Swival and every verification command
**always** run under `nono run -p vr-guard`, and `nono` is a **hard, always-required**
dependency. There is no opt-out — not a checkbox, not a config key, not a launcher path.

## Decisions already made (do NOT re-litigate or re-introduce an opt-out)

1. **Full removal across all surfaces.** Delete the `RelayConfig.BypassSandbox` field itself and
   collapse every branch that reads it to the sandbox-ON path. The UI, the .NET core/domain, the
   control API, and the bash launcher all lose every trace of bypass.
2. **A stale `"bypassSandbox": true` left in a `.relay/config.json` is SILENTLY IGNORED.** The key
   is simply no longer parsed; the run is sandboxed regardless. No error, no migration, no
   warning. (Two regression tests below lock this — they fail against today's code, which still
   honours the key.)

`SandboxExtraAllowPaths` is a **separate** sandbox-tuning field — leave it completely alone.

## Current state (researched — every live site)

The `bypassSandbox` flag is defined once and threaded through five layers. Exact sites:

**Domain / Core (.NET)**
- `src/VisualRelay.Domain/RelayConfig.cs:35-39` — the positional record parameter
  `bool BypassSandbox = false` plus a 4-line comment describing the opt-out.
- `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:38-39` (default in the baked-in
  default config) and `:218` (`BypassSandbox = OptionalBool(root, "bypassSandbox", …)` — the only
  thing that parses the key from JSON).
- `src/VisualRelay.Core/Init/RelayConfigWriter.cs:60-87` — the entire `UpsertBypassSandbox`
  method (read-modify-write of the key); also doc-comment mentions at `:93`.
- `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs` — class summary `:6-16`, and **two**
  branches: `:25-26` (`if (config.BypassSandbox) return await inner.RunAsync(…)`) and the
  `ResolveLaunch` branch `:49-61`, which its own comment says **"only feeds argument-shape unit
  tests"** (not reachable in production).
- `src/VisualRelay.Core/Execution/ProcessRunners.cs:83-84` — `BuildNonoPrefix` returns `[]` when
  bypassed; comment at `:11` and `:72`.
- `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs:65-66` (`BuildSandboxEnvironment`
  returns `null` when bypassed) and `:86-87` (`BuildLaunchTarget` runs swival directly when
  bypassed); comment at `:80-82`.
- `src/VisualRelay.Core/Execution/ProcessRunners.Diagnostics.cs:37` — `MissingRequiredTools` only
  requires `nono` when `!config.BypassSandbox`; comment at `:13-14`.

**App / UI**
- `src/VisualRelay.App/Views/Controls/QueuePanel.axaml:47-57` — the `<StackPanel Grid.Row="1">`
  holding the `<CheckBox … Content="Bypass nono sandbox">` and the ⚠ warning `<TextBlock>` (whose
  `IsVisible` is bound to `BypassSandbox`). This StackPanel is row 1 of the **inner header grid**
  declared at `:10-13` (`<Grid Row="0" RowDefinitions="Auto,Auto" …>`). Row 0 is the title/buttons
  row; the bypass StackPanel is the **only** occupant of row 1.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs:8-22` — the `_bypassSandbox`
  `[ObservableProperty]` and `OnBypassSandboxChanged` (which calls `UpsertBypassSandbox`). The
  file also holds `CommitProofArtifacts` and the settings-modal members — **do not delete the
  file**, only the bypass members.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:111` — hydration line
  `BypassSandbox = configResult.Config.BypassSandbox;` inside `ReloadTaskListAsync`.

**Control API (automation surface)**
- `src/VisualRelay.App/Services/ControlApi.cs:108-118` — the `case "bypass-sandbox":` command
  handler that sets `viewModel.BypassSandbox`.
- `src/VisualRelay.App/Services/ControlApi.State.cs:83` —
  `map["bypass-sandbox"] = new { enabled = true };` in the reported command-state map.

**Launcher (`visual-relay`, bash)**
- `:150-151` comment "nono (unless sandbox bypassed)" and `:162-165` in `_missing_required_tools`
  — nono is hard-required only `if ! _read_bypass_sandbox`.
- `:193-210` — the whole `_read_bypass_sandbox()` function (greps the config for
  `"bypassSandbox":true`) plus its doc-comment block.
- `:212-234` — `_require_nono()`; its error heredoc at `:229-231` tells the user how to opt out
  ("set bypassSandbox:true … the only supported opt-out").
- `:470-477` (launch/run case) and `:520-523` (run-task case) — both wrap `_require_nono`
  (and `_provision_nono` in the launch case) in `if ! _read_bypass_sandbox; then … fi`.

**User-facing docs**
- `README.md:38-40` ("To run without the sandbox, set `bypassSandbox:true` …") and `:119-127`
  (a whole "sandbox is controlled by the `bypassSandbox` key" subsection with a JSON sample
  showing `"bypassSandbox": true`).
- `AGENTS.md:60-61` — documents the `bypass-sandbox` control command (`body {"value":true|false}`).

**The repo's own config**
- `.relay/config.json:12` — `"bypassSandbox": false,`. Remove the line (cosmetic — it would be
  ignored anyway). This is a tracked project file; fine to edit. (Repo is shared with a VM, see
  [[repo-shared-with-vm]] — this is shared project config, not per-machine state.)

**Leave untouched (point-in-time history, NOT live code):** every `llm-tasks/DONE-*` file
(notably `DONE-sandbox-1/2/3-*`), this task's siblings, and
`docs/superpowers/plans/2026-06-17-fix-verify-loop-converge-or-bail.md`. They describe past work
and must keep their historical wording. Do not "fix" them.

## What to change

Work layer by layer. The end state of each branch is "sandbox is always on", so in every case
**delete the bypass branch and keep the else/sandboxed path** (do not keep a `false` constant).

1. **`RelayConfig.cs`** — remove the `BypassSandbox` parameter and its comment block. It is a
   **positional** record parameter, but every known construction uses named args or `with { … }`,
   so removal is clean; the compiler will flag any positional caller that needs reordering.
2. **`RelayConfigLoader.cs`** — delete the `BypassSandbox: false` line from the default config and
   the `BypassSandbox = OptionalBool(root, "bypassSandbox", …)` parse line. Unknown JSON keys are
   already ignored (the loader reads named keys), so a stale `bypassSandbox` key now no-ops.
3. **`RelayConfigWriter.cs`** — delete the entire `UpsertBypassSandbox` method; scrub the
   `bypassSandbox` mentions from the remaining doc-comments (e.g. `:93`).
4. **`SandboxedTestRunner.cs`** — remove the `:25-26` short-circuit and the `:49-61`
   `ResolveLaunch` bypass branch; `ResolveLaunch` now always builds the `nono` prefix. Update the
   class summary so it no longer references a "bypass checkbox" / no-sandbox path.
5. **`ProcessRunners.cs` / `.Helpers.cs` / `.Diagnostics.cs`** — `BuildNonoPrefix`,
   `BuildSandboxEnvironment`, and `BuildLaunchTarget` always do the sandboxed thing; remove their
   `if (… BypassSandbox)` early returns. In `MissingRequiredTools`, `nono` is now
   unconditionally required (`if (!OnPath(nonoBinary)) missing.Add(nonoBinary);`). Scrub the
   "when sandbox enabled / BypassSandbox == false" comments.
6. **UI** — delete the bypass `StackPanel` from `QueuePanel.axaml` (`:47-57`). Because it is the
   sole occupant of row 1 of the inner header grid (`:10-13`), also drop that now-empty row:
   change `<Grid Row="0" RowDefinitions="Auto,Auto" …>` to a single-row grid (`RowDefinitions="Auto"`
   or remove the attribute) so no empty row is left behind. Verify the panel still renders (run the
   app / a headless render) — leaving a dangling `Grid.Row="1"` reference is the trap here.
7. **`MainWindowViewModel.Settings.cs`** — remove the `_bypassSandbox` property and
   `OnBypassSandboxChanged`. Keep everything else in the file.
8. **`MainWindowViewModel.Helpers.cs`** — delete the `BypassSandbox = …` hydration line at `:111`.
9. **Control API** — delete the `case "bypass-sandbox":` block in `ControlApi.cs` and the
   `map["bypass-sandbox"] = …` entry in `ControlApi.State.cs`.
10. **Launcher (`visual-relay`)** — delete `_read_bypass_sandbox()` entirely. In
    `_missing_required_tools`, require `nono` unconditionally (drop the `_read_bypass_sandbox`
    guard). In both the `launch|run` and `run-task` cases, call `_require_nono`
    (and `_provision_nono` for launch) **unconditionally** — remove the `if ! _read_bypass_sandbox`
    wrappers. Rewrite the `_require_nono` error heredoc so it no longer mentions any opt-out: nono
    is required, here's how to install it, full stop. Update the `:150-151` and `:470-471`
    comments to "nono (always required)".
11. **Docs** — in `README.md`, delete the "To run without the sandbox…" sentence (`:38-40`) and the
    entire "controlled by the `bypassSandbox` key" subsection (`:119-127`); replace the latter with
    a one-liner that the sandbox is always on and `nono` is required. In `AGENTS.md`, remove the
    `bypass-sandbox` entry from the control-command list (`:60-61`). Remove the
    `"bypassSandbox": false` line from `.relay/config.json`.

## Tests

TDD where it buys something. Most of the ~30 referencing test files just need the now-deleted
symbol removed so the suite compiles and stays green on the sandbox-ON path.

- **Delete** tests that asserted bypass behaviour, e.g.: `RelayConfigWriterTests` cases for
  `UpsertBypassSandbox`; the bypass cases in `SandboxedTestRunnerArgumentTests`; the bypass cases
  in `MainWindowViewModelSettingsTests`; and the launcher bypass cases across
  `Installer5Bootstrap2/3LauncherTests`, `Installer5Sandbox2LauncherTests`, and
  `Installer5LauncherTests.cs`.
- **Adjust** tests that construct a config with `BypassSandbox: true/false` or
  `with { BypassSandbox = … }` (e.g. `SwivalSubagentRunnerTests`, `SwivalSubagentRunnerSandboxTests`
  and `.SkipDirs`, `SwivalSubagentRunnerContractRetryTests`, `SwivalSubagentRunnerCommandFilterTests`,
  `SandboxedShellVerifyExecutionTests`, `SandboxExtraAllowPathsConfigTests`, `RelayConfigLoaderTests`,
  `NonoRealBuildTests`, `PlanPhaseTestDoubles`, `ControlApiTests`, etc.) so they construct a config
  with **no** bypass field and assert the sandboxed shape. Any helper/`TestConfig()` that set the
  field loses it.
- **Convert, don't just delete,** `Installer5LauncherTests.CwdSandbox.cs::BypassSandbox_ReadsConfigFromScriptDir`:
  today it proves a `bypassSandbox:true` config makes the launcher **skip** nono. Flip it into a
  regression test (see below) proving nono is **still required**.

**Two NEW regression tests that lock the "silently ignore" decision (must fail against today's
code, pass after):**

- **C# (loader/preflight):** load a `.relay/config.json` whose JSON contains
  `"bypassSandbox": true`; assert it loads without error AND the run is still sandboxed — e.g.
  `SwivalSubagentRunner.MissingRequiredTools(loadedConfig, pathWithoutNono)` still reports `nono`
  as missing (proving nono is required despite the stale key). Today this fails: the key is honoured
  and nono is treated as optional.
- **Launcher (bash):** a fake repo whose `$SCRIPT_DIR/.relay/config.json` has
  `{"bypassSandbox":true}` and **no** `nono` on PATH must still hit the nono requirement (e.g.
  `_require_nono` exits 127 / the prereq gate flags nono), not silently launch. Today this fails
  (the launcher reads the key and skips nono).

## Done when

- A repo-wide grep for `bypasssandbox`/`bypass-sandbox` (case-insensitive) is **empty** across live
  code, docs, and config — the only remaining hits are the untouched historical artifacts
  (`llm-tasks/DONE-*`, this task, `docs/superpowers/plans/*`).
- The "Bypass nono sandbox" checkbox and its ⚠ warning are gone from the app, with no orphaned grid
  row, and the Queue panel still renders correctly.
- Swival and every verification command always run nono-wrapped; there is no code path that runs
  them unsandboxed.
- `nono` is unconditionally required by both the C# preflight (`MissingRequiredTools`) and the bash
  launcher (`_missing_required_tools` + `_require_nono` in launch/run and run-task); the
  `_require_nono` error text advertises no opt-out.
- The control API neither accepts nor advertises `bypass-sandbox` (handler and state-map entry both
  gone); `README.md` and `AGENTS.md` no longer describe a bypass.
- A stale `"bypassSandbox": true` config is ignored and still runs sandboxed, proven by both new
  regression tests; the converted launcher test passes.
- `./visual-relay check` is green (build + format + tests); all touched files stay under the
  300-line guard; Conventional Commit subject, e.g.
  `feat(sandbox): remove the bypass option so the nono sandbox is always on`. In the commit body,
  flag the behaviour change: `bypassSandbox` is fully removed and any leftover key in a config is
  now silently ignored (runs sandboxed regardless).

## Notes / landmines

- **Positional record parameter:** `RelayConfig.BypassSandbox` sits mid-list. Removing it is safe
  for named-arg/`with` callers (all known ones); let the compiler surface any positional caller.
- **The "argument-shape only" branch:** `SandboxedTestRunner.ResolveLaunch`'s bypass branch is
  explicitly unreachable in production — it exists solely for unit tests. Delete the branch and
  those tests together; the nono-wrapped shape is the only shape now.
- **Empty grid row:** the UI trap is leaving `RowDefinitions="Auto,Auto"` (or a stray
  `Grid.Row="1"`) after the bypass StackPanel is gone. Collapse the inner header grid to one row.
- **Don't touch** `SandboxExtraAllowPaths`, the `DONE-*` tasks, or the historical plan doc.
- This is a **standalone** task (no `NN-` prefix). If a concurrent task touches the launcher's nono
  gating, `RelayConfig`, or `ProcessRunners`, it should rebase onto this one — this task removes a
  config field, a launcher function, and several branches they may also edit.
