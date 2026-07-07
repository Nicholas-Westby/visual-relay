# Stop Obsidian Bridge Settings From Being Reset — Isolate Tests From the Real User Config, and Audit Every Settings Write

My Obsidian bridge settings keep getting silently reset. The correct state on this machine is:
bridge **enabled**, vault root
`~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/` (which happens to
equal `ObsidianBridgeSettings.DefaultVaultRootTemplate`). After a reset (see
`settings-after-reset.png` in this folder) the bridge is still enabled but the vault root has
become `/Users/dev/obsidian-vault` — a path that exists nowhere on this machine and exactly matches
a test fixture string in `tests/VisualRelay.Tests/RevealVaultRootCommandTests.cs`. It has happened
several times.

Two deliverables in this task:

1. **Make it structurally impossible for the test suite to write the real per-user config.**
2. **Observability:** every write to these settings must leave a durable, attributable record — at
   the very least we must be able to tell when a *user* changed the setting versus anything else.

## Current state (researched)

- **Storage** — `src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs`: `Load`/`Save` keep
  three keys (`VR_OBSIDIAN_ENABLED`, `VR_OBSIDIAN_VAULT_ROOT`, `VR_OBSIDIAN_POLL_SECONDS`) in the
  user-level `.env` at `$XDG_CONFIG_HOME/visual-relay/.env` (fallback
  `$HOME/.config/visual-relay/.env`), via three `KeyEnvFile.Upsert` calls. `Save` swallows
  `InvalidOperationException` ("Nowhere to save — bail"). The same `.env` also stores API keys, so
  any logging of values must redact non-Obsidian keys.
- **The environment seam leaks** — `src/VisualRelay.Core/Configuration/KeyEnvFile.cs`:

  ```csharp
  public static string? GetEnv(string name, IEnvironmentAccessor? accessor = null) =>
      accessor?.GetEnvironmentVariable(name)
      ?? Environment.GetEnvironmentVariable(name);
  ```

  When a test passes a `DictionaryEnvironmentAccessor` that lacks `HOME`/`XDG_CONFIG_HOME`, the
  per-key `??` falls through to the **real process environment**, so path resolution lands on the
  real `~/.config/visual-relay/.env`. An "isolated" accessor is only isolated if it happens to seed
  those keys.
- **Every property set persists immediately** —
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs`:
  `OnObsidianEnabledChanged` / `OnObsidianVaultRootChanged` / `OnObsidianPollSecondsChanged` all
  call `PersistBridgeSettings()`, which writes **all three keys** through
  `ObsidianBridgeSettings.Save(..., EnvironmentAccessor)` and swallows all exceptions.
  `LoadObsidianBridgeSettings()` hydrates by assigning those same observable properties, so merely
  **constructing the view model persists** — including a transient window where `ObsidianEnabled`
  has been hydrated but `ObsidianVaultRoot` is still the field default `""` (the first persist
  writes `VR_OBSIDIAN_VAULT_ROOT=`). The repo's test command uses `--blame-hang-timeout 60s`
  (`.relay/config.json` → `testCmd`), which kills the test host on hangs — a kill inside that
  window strands the partial write. xUnit also runs test collections in parallel, so concurrent
  persists race on the single `.env` file (each persist is three read-modify-write upserts).
- **Constructor default is the real environment** — `MainWindowViewModel`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs`):
  `public MainWindowViewModel(IEnvironmentAccessor? environmentAccessor = null)` stores the
  accessor on `EnvironmentAccessor { get; init; }` and calls `LoadObsidianBridgeSettings()` during
  construction. A null accessor means every read/write resolves against the real environment. The
  production app itself passes null (`src/VisualRelay.App/App.axaml.cs`:
  `new MainWindowViewModel()`), and roughly **235** `new MainWindowViewModel` occurrences exist
  across the test suite, many with no accessor at all (e.g.
  `tests/VisualRelay.Tests/MainWindowViewModelTests.Pause.cs`,
  `tests/VisualRelay.Tests/LiveStateViewModelTests.cs`,
  `tests/VisualRelay.Tests/ActivityColumnTabsUiTests.StageRendering.cs`).
- **The smoking gun** — `tests/VisualRelay.Tests/RevealVaultRootCommandTests.cs`,
  `RevealVaultRootCommand_CanExecute_WhenVaultRootIsSet`: constructs the view model with an
  **empty** `DictionaryEnvironmentAccessor` and then sets
  `ObsidianVaultRoot = "/Users/dev/obsidian-vault"`. Sequence: the constructor's hydration reads
  the user's real `.env` (via the fallback above) and loads `Enabled=true`; the object initializer
  then sets the fixture vault root; `PersistBridgeSettings` writes
  `enabled=true, vaultRoot=/Users/dev/obsidian-vault, pollSeconds=60` into the **real**
  `~/.config/visual-relay/.env`. That is byte-for-byte the corrupted state in the screenshot. Every
  unsandboxed test run re-clobbers it.
- **The correct pattern already exists** —
  `tests/VisualRelay.Tests/ObsidianBridgeVmPropertiesTests.cs` and
  `tests/VisualRelay.Tests/ObsidianDrainSummaryTests.cs` seed fake `HOME` + `XDG_CONFIG_HOME`
  into their accessors (their doc comments literally say "so saving never touches the user's real
  ~/.config config file"); `ObsidianBridgeVmTests.CreateViewModel` sets `env["HOME"]` to a temp
  dir; `TestRepository` (`tests/VisualRelay.Tests/TestDoubles.cs`) pre-seeds `XDG_CONFIG_HOME`.
  `RevealVaultRootCommandTests` just doesn't use any of it.
- **Other legitimate writers** — the control API can set these properties remotely:
  `src/VisualRelay.App/Services/ControlApi.cs` assigns `viewModel.ObsidianEnabled` /
  `viewModel.ObsidianVaultRoot` for the settings command. A migration path
  (`ObsidianBridgeSettings.TryMigrateFromObsidianJson`) also upserts the keys. Neither they, the
  UI, nor anything else leaves any record: there is **zero logging** of settings writes anywhere
  (`PersistBridgeSettings` and `Save` are silent best-effort). Existing sinks under
  `src/VisualRelay.Core/Logging/` (`FileRelayEventSink`, `DrainSummaryLog`) are run/repo-scoped,
  not user-config-scoped.

## What to build (TDD-first)

1. **Make supplied accessors hermetic.** Change `KeyEnvFile.GetEnv` semantics to: if an accessor
   is supplied, it is authoritative (return its answer, even null); only a **null** accessor reads
   `Environment.GetEnvironmentVariable`. Tests first in
   `tests/VisualRelay.Tests/KeyEnvFileTests.cs`: a supplied accessor missing a key returns null
   even when the real process env has that key; null-accessor behavior unchanged. Then audit every
   `GetEnv`/`Read`/`Upsert` call site (`ObsidianBridgeSettings`, `DiagnosticsSettings`,
   `UiStateStore`, and friends) and every failing test: fix by **seeding the accessor** (the
   `ObsidianBridgeVmPropertiesTests` pattern), never by restoring the fallback. Production is
   unaffected: the app passes null.

2. **Hydration must not persist, and persists must be dirty-checked.** Guard
   `PersistBridgeSettings` with a hydration flag set around `LoadObsidianBridgeSettings()`'s
   property assignments (precedent for persist-only-on-real-user-change: the
   `OnCommitProofArtifactsChanged` guard in
   `src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs`). In
   `ObsidianBridgeSettings.Save`, read the current values first and skip upserts for unchanged
   keys, so a no-op persist writes nothing (kills the echo-write on construction, shrinks the race
   window, removes the transient `VR_OBSIDIAN_VAULT_ROOT=` partial state, and makes the audit log
   below meaningful). Tests: constructing a view model against a seeded temp `.env` leaves the file
   byte-identical; a genuine property change persists exactly that change.

3. **Process-level backstop for the whole test suite.** The test host should never be able to
   resolve the real user config even from a null-accessor construction: add a
   `[ModuleInitializer]` (or equivalent assembly-level fixture) in `VisualRelay.Tests` that sets
   the process `XDG_CONFIG_HOME` to a unique temp directory before any test runs, plus a guard
   test asserting the redirect is active. This protects all ~235 existing constructions and every
   future test by default. (Isolation-verifying precedent:
   `tests/VisualRelay.Tests/OrchestratorProfileIsolationTests.cs` /
   `RelayDriverProfileIsolationTests.cs`.)

4. **Fix the leaky tests.** Rework `RevealVaultRootCommandTests` to seed fake
   `HOME`/`XDG_CONFIG_HOME` scratch dirs like `ObsidianBridgeVmPropertiesTests` does, and sweep the
   suite for any other `new MainWindowViewModel` that sets `Obsidian*` properties without a seeded
   accessor. Add a regression test that recreates this incident end-to-end: seed a temp `.env`
   with `enabled=true` and a custom vault root, run the old leaky construction pattern (empty
   accessor + fixture vault root), and assert the seeded `.env` is untouched.

5. **Observability: an append-only settings audit trail.** New small class (new file, e.g.
   `src/VisualRelay.Core/Configuration/SettingsAuditLog.cs`) that appends one line per actual key
   change to `settings-audit.log` **next to the `.env`** (same accessor-based path resolution, same
   0700/0600 hardening as `KeyEnvFile.Upsert`):

   ```
   2026-07-07T05:12:03Z VR_OBSIDIAN_VAULT_ROOT "~/Library/.../Visual Relay LLM Tasks/" -> "/Users/dev/obsidian-vault" source=settings-ui pid=4242 proc=VisualRelay.App
   ```

   Requirements:
   - Called from `ObsidianBridgeSettings.Save` (which, after step 2, already knows old values) and
     from `TryMigrateFromObsidianJson` (`source=migration`).
   - A `source` label distinguishing at minimum: in-app settings persistence (`settings-ui`),
     control-API pushes (`control-api`), and migration. The control API mutates the view model's
     properties directly, so labeling it may need a small internal seam (e.g. an internal
     apply-with-source path used by both, or a scoped source field on the view model) — pick the
     cleanest design that respects the file-size guard. If per-writer labeling inside the app
     proves invasive, `pid`/`proc` identity plus a single in-app source is the acceptable floor;
     the non-negotiable part is that **every write to the bridge keys leaves a durable, timestamped
     old→new record naming the writing process**.
   - Values are logged in clear **only** for an allowlist (`VR_OBSIDIAN_*`); any other key logs
     `<redacted>` — the same `.env` holds API keys.
   - Best-effort and never throws; bounded size (trim to a sane tail when it grows past a small
     cap — exact scheme is your choice).
   - Surface the change in the running app too: set `StatusText` (existing pattern in
     `MainWindowViewModel.ObsidianBridge.cs`) when bridge settings are saved at runtime.
   - Tests: a change appends a correct line (temp XDG); a no-op save appends nothing;
     non-allowlisted keys are redacted; migration writes `source=migration` lines.

## Done when

- `KeyEnvFileTests` prove supplied accessors never fall back to the real process environment.
- Constructing `MainWindowViewModel` (any accessor) performs zero config writes until a real
  user-visible change; the incident regression test in step 4 passes.
- The suite-wide `XDG_CONFIG_HOME` backstop is active, with a guard test proving it.
- Toggling a bridge setting in the running app or via the control API appends an audit line with
  timestamp, key, old → new, source, and process identity; the audit file lives beside the `.env`
  with hardened permissions; API-key-style values can never appear in it.
- `./visual-relay check` passes (guards, format, build, full test suite).

## Guardrails

- **Do not write to or "restore" the real `~/.config/visual-relay/.env`** — it is per-machine user
  data (and the sandbox denies it anyway). After this ships, the human re-enables the bridge once
  in Settings; the default vault root is already the correct iCloud path.
- Do not rename the `VR_OBSIDIAN_*` keys, move the `.env`, or change its format; the audit log is
  purely additive.
- Do not weaken the hermetic-accessor change to green a failing test — seed that test instead.
- 300-line ceiling per file (`tools/VisualRelay.Guards`): current sizes — `KeyEnvFile.cs` 232,
  `ObsidianBridgeSettings.cs` 222, `MainWindowViewModel.ObsidianBridge.cs` 206, `ControlApi.cs`
  268. There is headroom for the small edits, but the audit logic goes in its own new file.
- Match test conventions: plain xUnit `[Fact]` + `DictionaryEnvironmentAccessor`/`TestRepository`
  for logic tests; `[AvaloniaFact]` + `[Collection("Headless")]` only where the UI thread is
  needed (see `ObsidianDrainSummaryTests`).
- Conventional Commits (the `commit-msg` hook enforces the ruleset; see `docs/commit-messages.md`
  and `AGENTS.md`). Minimal diffs — change only what this task needs.
