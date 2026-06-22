# Fold the Obsidian bridge settings into the user-level `.env`; drop `obsidian.json`

The Obsidian bridge keeps its three settings in a separate per-user file
`$XDG_CONFIG_HOME/visual-relay/obsidian.json`. There's no reason for a second config file — store
those settings as keys in the **same user-level `.env`** the provider API keys live in
(`$XDG_CONFIG_HOME/visual-relay/.env`, fallback `$HOME/.config/visual-relay/.env`), and stop using
`obsidian.json`.

## Current state (bake in)

- `src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs` owns it: a record
  `ObsidianBridgeConfig(bool Enabled, string VaultRoot, int PollSeconds)` with `Load`/`Save` that
  read/write `obsidian.json` (camelCase JSON), XDG-resolved via the same helper as `KeyEnvFile`.
  Defaults: `Enabled=false`, `VaultRoot=~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/`,
  `PollSeconds=60`. It also (a) **expands `~/`** in the vault root on load and (b) enforces
  `MinPollSeconds=15` (a public const referenced by the VM).
- Only consumer is the VM: `src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs`
  (`Load` at the `LoadObsidianBridgeSettings` method, `Save(new ObsidianBridgeConfig(…))`, and
  `ObsidianBridgeSettings.MinPollSeconds`). `MainWindowViewModel.cs` calls
  `LoadObsidianBridgeSettings()` at startup.
- `KeyEnvFile` already does everything needed for `.env`: `Read` (KEY=VALUE, trims, strips
  surrounding quotes, **keeps internal spaces**), `GetEnv` (process-env-wins), and `Upsert` (surgical
  single-key write preserving other lines, dir `0700` / file `0600`).

> **Freshness contract.** Verify by searching for `ObsidianBridgeSettings`, `obsidian.json`,
> `MinPollSeconds`; adapt if these moved.

## Goal

Obsidian bridge settings live in the user-level `.env` as keys; `obsidian.json` is no longer read or
written, and an existing one is migrated then removed. The Settings UI / bridge behave exactly as
before (same defaults, `~` expansion, 15s poll floor).

## Approach (keep the blast radius small)

- **Keep the `ObsidianBridgeConfig` record and the `Load`/`Save`/`MinPollSeconds` surface** so the VM
  consumers don't change — only swap the backing store from `obsidian.json` to `.env` keys via
  `KeyEnvFile`.
- Key names (follow the existing `VR_CONTROL_*` env convention in `ControlServerOptions.cs`):
  - `VR_OBSIDIAN_ENABLED` = `true` / `false`
  - `VR_OBSIDIAN_VAULT_ROOT` = path (written raw; `KeyEnvFile.Read` preserves internal spaces, so the
    `…/iCloud~md~obsidian/…` path with spaces round-trips — **add a test for exactly this path**)
  - `VR_OBSIDIAN_POLL_SECONDS` = integer
- `Load`: read the three keys via `KeyEnvFile.Read`/`GetEnv`; **preserve current semantics** —
  default when absent/malformed (`false` / default template / `60`), expand `~/` in the vault root,
  floor poll at `MinPollSeconds`.
- `Save`: three `KeyEnvFile.Upsert` calls (bool→`"true"/"false"`, int→invariant string). Don't
  rewrite the whole file.
- **One-time migration:** on load, if `obsidian.json` exists, import its values into the `.env` (only
  for keys not already present), then delete `obsidian.json`. This carries the requester's current
  settings over without manual steps.

## Watch-outs

- **The `.env` is sourced into the LiteLLM backend** (`BackendLifecycle.LoadProviderKeys` passes
  *all* `.env` keys to the proxy process). After this change the `VR_OBSIDIAN_*` keys get exported to
  LiteLLM too — harmless (it ignores unknown vars), but note it. (Related: the repo-`.env` source is
  being removed in `centralize-provider-keys-on-user-level-env`; this task targets the user-level
  `.env`, consistent with that direction.)
- Don't lose `~` expansion or the `MinPollSeconds=15` floor — both are currently enforced on load.
- Vault paths with spaces: write raw (no added quotes); `KeyEnvFile.Read` strips *surrounding* quotes
  and trims ends only, so don't wrap the value in quotes or you'll need to round-trip them.

## Tests

- Rewrite `tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs` to assert round-trip via `.env`
  (including the spaces-and-`~md~` vault path), `~` expansion, poll-floor, and absent-keys defaults.
- Add a migration test: a pre-existing `obsidian.json` is imported into `.env` and then deleted.
- Check `ObsidianBridgeVmTests` / `ObsidianBridgeVmPropertiesTests` / `ObsidianBridgeVmScaffoldExportTests`
  for `obsidian.json` assumptions and update.

## Out of scope

- `ui-state.json` and `vr-guard.json` (other user-level files; not requested here).
- Changing the bridge's behavior, vault layout, or the Settings UI controls.
