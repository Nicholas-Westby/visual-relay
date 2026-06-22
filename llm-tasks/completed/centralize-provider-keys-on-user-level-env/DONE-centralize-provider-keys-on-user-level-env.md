# Centralize provider keys on the user-level `.env`; stop loading the repo-root `.env`

Provider API keys are currently resolved from **two** dotenv files, and the GUI and backend disagree
about which they read — so a key in the repo-root `.env` loads at runtime but shows as **"missing"**
in the Settings screen (a real bug a user hit with `DEEPSEEK_API_KEY` / `MOONSHOT_API_KEY`). There is
no reason to support the repo-root `.env`. **Centralize on the single user-level file**
`$XDG_CONFIG_HOME/visual-relay/.env` (fallback `$HOME/.config/visual-relay/.env`) and remove the
repo-root `.env` source entirely.

## Facts established this session (bake in)

- **The GUI already reads only the right thing.** `MainWindowViewModel.RefreshKeyStatesAsync`
  (`…/ViewModels/MainWindowViewModel.Keys.cs`) decides "configured/missing" from **process env +
  the user-level `.env`** (`KeyEnvFile.Read` / `KeyEnvFile.GetEnv`). It never reads a repo-root
  `.env`. So **no GUI change is needed** — removing the backend's repo-root source makes the two
  agree and makes the "missing" indicator truthful. (This supersedes the earlier idea of teaching
  the GUI to read the repo `.env`.)
- **The repo-root `.env` is loaded in exactly one place:** `BackendLifecycle.LoadProviderKeys`
  (`src/VisualRelay.Core/Execution/BackendLifecycle.Start.cs`), which merges the user-level `.env`
  **then** `Path.Combine(RepoRoot, ".env")` (commented "dev fallback"), process-env-wins:
  ```csharp
  if (_options.RepoRoot is { } root)
  {
      var repoEnv = Path.Combine(root, ".env");
      if (File.Exists(repoEnv))
      {
          _log($"loading provider keys from {repoEnv} (dev fallback)");
          Merge(keys, KeyEnvFile.GetUnsetKeysPublic(repoEnv));
      }
  }
  ```
  Removing this block is the core of the task.
- **Keep `_options.RepoRoot` — it has other uses.** It is also passed to backend **config
  generation** (`BackendLifecycle.Start.cs:65`) and **legacy-venv cleanup** (`:198`). Do **not**
  rip out the `RepoRoot` / `VISUAL_RELAY_SCRIPT_DIR` plumbing (`BackendStartOptions.cs`,
  `MainWindowViewModel.Commands.cs`); only delete the repo-`.env` loading.
- **Keep process-env precedence.** The spawned proxy inherits the process environment; the
  user-level `.env` layers under it via `GetUnsetKeysPublic`. New precedence is simply
  **process env > user-level `.env`** (the repo tier is gone).

> **Freshness contract.** Verify by searching for `LoadProviderKeys`, `repoEnv`, and `RepoRoot` in
> `BackendLifecycle.Start.cs`; adapt if the block has moved. Do not delete lines by number.

## ⚠️ Behavior change / migration (call out to the user)

Anyone (including the requester right now) who keeps provider keys **only** in a repo-root `.env`
will lose them at runtime after this change until they move them to
`~/.config/visual-relay/.env`. The keys most likely affected: `DEEPSEEK_API_KEY`, `MOONSHOT_API_KEY`
(the requester's HF token is already in the user-level file). Surface this in the PR description /
status. (Optional nicety, decide in Plan: a one-time log line or migration that copies a repo-root
`.env`'s keys into the user-level file on first run — but the default ask is just "stop reading it".)

## Docs + examples to update (they currently advertise the repo `.env`)

- `README.md` (around the provider-keys section): it says keys load from **both** locations and shows
  `cp .env.example .env   # repo-root .env, git-ignored` and "loads keys from both locations
  automatically". Update to the single user-level location.
- `.env.example`: remove the `cp .env.example .env (repo-root .env…)` option and change the
  precedence line `process env > user-level .env > repo .env` → `process env > user-level .env`.

## Tests

- TDD: add/adjust a backend-lifecycle test asserting a key present **only** in a repo-root `.env`
  is **not** loaded, while a user-level `.env` key still is. Search the test project for any existing
  test that writes a repo-root `.env` and asserts provider-key loading and update it (start with the
  backend-lifecycle/config tests, e.g. `BackendLifecycleStatusTests` / `BackendConfigStepTests`).
- Keep the rest of the backend-start suite green (gen-config + venv paths still use `RepoRoot`).

## Goal

Provider keys come from exactly one file — the user-level `$XDG_CONFIG_HOME/visual-relay/.env`
(plus the process env) — everywhere. The repo-root `.env` is no longer read by the backend. The
Settings screen's configured/missing state now matches what actually loads. `RepoRoot`'s other uses
are untouched. Docs/examples reflect the single location.

## Out of scope

- The Settings UI key-status logic (already correct).
- Per-repo settings in `.relay/config.json` (e.g. the commit-proof toggle) — unrelated.
- The reveal-in-Finder button (`settings-reveal-config-file-in-finder`) — sibling task; it targets
  this same user-level `.env`.
