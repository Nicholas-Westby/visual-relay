# Provider keys live only in the repo `.env`, so a brew-installed copy has nowhere to put them

Today the only place Visual Relay reads provider keys is the repo-root `.env`.
`tools/backend/backend.sh` sources `${REPO_ROOT}/.env` before launching litellm
(the "loading provider keys from .env" block — `set -a; . "${REPO_ROOT}/.env"; set +a`),
and `.env.example` documents the five keys (`MOONSHOT_API_KEY`, `DEEPSEEK_API_KEY`,
`HF_TOKEN`, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`).

That works for a source checkout but breaks the moment Visual Relay is installed
from Homebrew (see `installer-5-distribute-via-homebrew-formula.md`): there is no repo, and
`REPO_ROOT` resolves to the **read-only brew prefix**, so an installed user has
nowhere to write their keys. The in-app key panel
(`installer-4-require-hugging-face-and-add-key-setup-panel.md`) also needs a stable,
user-writable location to read and write.

## Goal

Provider keys live in a user-level dotenv at
`${XDG_CONFIG_HOME:-$HOME/.config}/visual-relay/.env`. Both `backend.sh` and the
app read from there; the app writes there. A key already exported in the process
environment still wins (CI / power users). For a source checkout the repo `.env`
remains a **dev-only** fallback so the existing build loop is unaffected.

Resolution precedence: **process env > user-level `.env` > repo `.env` (dev only)**.

## Approach (suggested)

- Add one source-of-truth helper, e.g. `KeyEnvFile` in
  `src/VisualRelay.Core/Configuration/`, exposing:
  - the resolved path (`$XDG_CONFIG_HOME/visual-relay/.env`, else
    `$HOME/.config/visual-relay/.env`),
  - `Read()` → parse `KEY=VALUE` lines (ignore blanks/`#` comments),
  - `Upsert(key, value)` → set/replace one key, **preserving all other lines**,
    creating the dir `0700` and file `0600`.
- `backend.sh`: replace the single `${REPO_ROOT}/.env` source with a guarded load
  that honors an already-set variable — load the **user-level** file first, then
  the repo `.env` (dev), assigning each `KEY=VAL` only when the variable is unset
  (`[[ -z "${!KEY:-}" ]]`) so the process environment always wins. Keep
  `set -euo pipefail` correctness.
- `.env.example` + the README "Provider keys" section: document the new
  user-level location as the primary place; note the repo `.env` is dev-only.

## Files

- New `src/VisualRelay.Core/Configuration/KeyEnvFile.cs` (path + parse + upsert;
  under the 300-line guard).
- `tools/backend/backend.sh` (the env-loading block; honor-existing-env precedence).
- `.env.example`, `README.md`.
- The app's read/write usage lands in
  `installer-4-require-hugging-face-and-add-key-setup-panel.md`; expose the helper here.

## Tests (write the failing tests first)

Use the existing test project + `TestRepository`/temp-dir helpers.

- **path resolution** honors `XDG_CONFIG_HOME`, else falls back to `$HOME/.config`.
- **parse** reads `KEY=VALUE`, skips comments/blank lines, trims as today's `.env` would.
- **upsert** adds a new key, updates an existing key, and leaves unrelated lines
  byte-for-byte intact; creates the dir/file with `0700`/`0600` perms.
- **precedence (helper semantics)**: a key already present in the environment is
  not overwritten by a file value (the "load only if unset" rule). The bash-side
  precedence in `backend.sh` is smoke-checked manually / in the backend task — note it.

## Sequencing

Foundational. Do this **before**:
- `installer-4-require-hugging-face-and-add-key-setup-panel.md` (the panel reads/writes via this helper), and
- `installer-3-generate-backend-config-from-present-keys.md` (the generator inspects the same resolved key set).

## Done when

- Keys placed in `~/.config/visual-relay/.env` are picked up by
  `tools/backend/backend.sh start` with **no repo present**.
- An exported `MOONSHOT_API_KEY` overrides the file; a source checkout's repo
  `.env` still works.
- The Core helper reads/upserts the file at `0600`, preserving unrelated lines.
- `.env.example` / README document the user-level location.
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.

## Notes

Keep the dotenv `KEY=VALUE` format so the bash load stays trivial. macOS Keychain
storage is a deliberate **non-goal** here (documented future hardening) —
user-level dotenv was the chosen trade-off during the installer brainstorm
(simplicity, reuses the existing source-`.env` flow), accepting plaintext at
`0600` file perms.
