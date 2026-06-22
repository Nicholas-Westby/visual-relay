# Settings screen: add a "Reveal settings file in Finder" button

Add a button to the Settings screen that opens the OS file manager to the folder containing the
settings file **and selects that file** (on macOS: Finder opens the containing folder with the file
highlighted).

## The reveal mechanism already exists — reuse it

`src/VisualRelay.Core/Execution/FileReveal.cs` → `FileReveal.Reveal(path)` already does exactly the
requested behaviour, cross-platform:
- macOS: `open -R <path>` (opens the containing folder, selects the file) ✅
- Windows: `explorer /select,<path>`; Linux: `xdg-open <dir>` (can't select, opens the folder)
- It is best-effort (swallows failures, never crashes).

So this task is just: a new VM command that calls `FileReveal.Reveal(<settings path>)`, wired to a
button in the Settings panel. (The existing `RevealStageArtifactsCommand` in
`MainWindowViewModel.Commands.cs` is a working example of a reveal command — mirror its shape.)

## The file to reveal: the user-level `.env` (decided)

Reveal the user-level provider-keys dotenv: `$XDG_CONFIG_HOME/visual-relay/.env` (fallback
`$HOME/.config/visual-relay/.env`) — path helper `KeyEnvFile.ResolvePathForCurrentUser()`. This is
what the Settings "Save" buttons write and the single source of truth for provider keys (the
repo-root `.env` is being removed — see `centralize-provider-keys-on-user-level-env`). On macOS
`open -R <path>` opens the `visual-relay` config folder with `.env` selected, which satisfies "open
the folder **and** select the file". **Do not** reveal the repo-root `.env` or the config directory —
target this one file.

## Edge case to handle

If the target file does not exist yet (e.g. no key has ever been saved), `open -R <missing>` does
nothing useful. Ensure the `visual-relay` config directory exists and, when the file is absent,
reveal the **directory** instead of a non-existent file. (`KeyEnvFile.Upsert` already creates the
dir `0700` / file `0600` when saving; the reveal path must not depend on a prior save.)

## Where to add the button

`src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` — the panel opens with the
`<TextBlock Text="Settings"/>` title inside a `StackPanel`. Add a clearly-labelled button such as
"Reveal settings file" / "Show in Finder" — e.g. a header row with the title and a right-aligned
button, or a small row directly under the title. Match the panel's existing button styling (see the
`Save` and `Classes="hyperlink"` "Get a key" buttons already in this file). Bind it to the new
command.

> **Freshness contract.** Verify by searching for `Text="Settings"` in `SettingsPanel.axaml` and
> `RevealStageArtifactsCommand` in `MainWindowViewModel.Commands.cs`; adapt to current structure.

## Goal

A button in the Settings screen reveals the user-level `.env`
(`KeyEnvFile.ResolvePathForCurrentUser()`) in the OS file manager — folder opened, `.env` selected —
using `FileReveal.Reveal`. Works when the file exists; falls back to revealing the `visual-relay`
config directory when it doesn't. No new reveal logic; reuse `FileReveal`.

## Tests

- Unit: the new command resolves the expected path (`KeyEnvFile.ResolvePathForCurrentUser()` or the
  config dir) — assert via the path helper, not by spawning Finder. `FileReveal.BuildCommand` is
  already pure and unit-tested per platform; a test can assert the command builds `open -R <path>`
  on macOS for the resolved settings path.
- Headless UI (style of `ConfigInitEmptyStateUiTests`): the button exists in `SettingsPanel` and is
  bound to the new command.
- Note: actually launching Finder is not asserted (side-effecting, env-dependent).

## Out of scope

- Adding an in-app settings *editor* for these files.
- Changing where settings are stored or consolidating them into one file.

## No screenshot

(Feature request — no current-state screenshot needed.)
