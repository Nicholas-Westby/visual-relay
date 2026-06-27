# Repo-local backend state (venv, scratch) breaks whichever environment didn't create it — the working copy is shared between host and VM

The visual-relay working copy is a folder shared between a macOS host (user
`nicholaswestby`) and a VM (user `admin`), both aarch64-darwin. The backend keeps
per-machine state inside that shared tree when the repo is writable
(`tools/backend/backend.sh:21-28`): the litellm venv at `tools/backend/.venv` and
scratch (pidfile, log, generated config) at `.relay-scratch/`. A venv is
home-prefixed by construction — entry-point shebangs and the `bin/python` symlink
embed absolute paths — so a venv built in one environment is garbage in the other.

Evidence (2026-06-11, host launch): `./visual-relay` printed
`backend: litellm exited before becoming ready` with
`nohup: .../tools/backend/.venv/bin/litellm: No such file or directory`. The venv
was built in the VM: `head -1 .venv/bin/litellm` →
`#!/Users/admin/Dev/visual-relay/tools/backend/.venv/bin/python`, and
`.venv/bin/python` →
`/Users/admin/.local/share/uv/python/cpython-3.13-macos-aarch64-none/bin/python3.13`
(dangling on the host). The guard at `backend.sh:73` is `[[ -x "${LITELLM_BIN}" ]]`
— an existence check that passes on this broken venv (file present, exec bit set),
so `ensure_litellm()` never re-provisions; execve then fails ENOENT on the
shebang interpreter, which nohup reports as "No such file or directory" on the
script itself.

The shared scratch dir is a second hazard: a pidfile written by one environment is
read by the other (`live_pid()`, `backend.sh:57-65`). A stale VM pid can collide
with a live, unrelated host process — `kill -0` then reports a phantom litellm,
`start` waits on it, and `stop` would SIGTERM an innocent process.

## Goal

Per-machine backend state always lives under the per-user data dir
(`${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/`), never in the repo tree —
host and VM each provision and own their own venv and scratch. The venv is treated
as a disposable cache: probed by *execution*, not existence, and rebuilt from
pinned inputs when broken. Legacy in-repo state is cleaned up.

## Approach (suggested)

- **Make the XDG redirect unconditional** (`backend.sh:21-28`): delete the
  `[[ -w "${REPO_ROOT}" ]]` branch so dev checkouts and brew installs share one
  code path — `VENV_DIR="${DATA_HOME}/backend-venv"`,
  `SCRATCH="${DATA_HOME}/scratch"`. Everything downstream (PID_FILE, LOG_FILE, the
  generated config at `backend.sh:189`) follows automatically.
- **Functional venv probe** (`ensure_litellm`, `backend.sh:72-96`): replace the
  bare `[[ -x "${LITELLM_BIN}" ]]` with a probe that proves the toolchain runs —
  `"${VENV_PY}" -V >/dev/null 2>&1 && [[ -x "${LITELLM_BIN}" ]]` (executing the
  venv python catches the dangling/foreign interpreter without paying a full
  litellm import). On probe failure: log one line, `rm -rf "${VENV_DIR}"`, and
  fall through to the existing uv provisioning. Keep the PATH-litellm fallback
  when uv is absent.
- **Legacy cleanup**: on `start`, if `${SCRIPT_DIR}/.venv` or
  `${REPO_ROOT}/.relay-scratch` exist, `rm -rf` them with a single log line. Both
  are gitignored and disposable; whichever environment runs next re-provisions
  into its own home. (This deletes the VM's broken-on-host venv too — the VM
  rebuilds its own under `/Users/admin/.local/share/...` on next start.)
- **Docs**: note the new locations in `TROUBLESHOOTING.md` (where to find
  `litellm.log` now; that the venv is safe to delete any time).

## Files

- `tools/backend/backend.sh` (path block, `ensure_litellm`, legacy cleanup).
- `tests/VisualRelay.Tests/` (extend the existing backend.sh shell-driving tests).
- `TROUBLESHOOTING.md` (new log/venv locations).
- `.gitignore` (optional: `tools/backend/.venv` / `.relay-scratch` entries may
  stay for old checkouts; do not rely on them).

## Tests (write the failing tests first)

Use the hermetic stub pattern (crafted `PATH`, stub executables recording argv,
temp `HOME`/`XDG_DATA_HOME`) already used for launcher tests:

- **Paths**: with `XDG_DATA_HOME` pointed at a temp dir and a *writable* repo
  root, `backend.sh start` (curl stubbed unhealthy, uv stubbed) creates scratch
  and targets the venv under the temp dir and creates nothing under the repo —
  assert no `.relay-scratch/` or `tools/backend/.venv/` appears.
- **Self-heal**: pre-create a venv whose `bin/python` is a dangling symlink (the
  exact 2026-06-11 shape) plus an executable `bin/litellm`; stub `uv` on PATH
  recording argv. `start` removes the broken venv and invokes uv to re-provision
  (argv recorded), instead of nohup-launching the broken binary.
- **Legacy cleanup**: pre-create repo-local `tools/backend/.venv/` and
  `.relay-scratch/`; `start` removes both and logs it.

## Sequencing

Independent and first in the `bootstrap-*` series. On a machine without `uv` on
PATH this task alone leaves the backend degraded-but-honest (the existing
`missing_toolchain_message`); `bootstrap-2-provision-launch-toolchain-via-nix-devshell.md`
supplies uv via the devshell.

## Done when

- Host and VM each run with their own venv/scratch under their own
  `$XDG_DATA_HOME/visual-relay/`; neither writes per-machine state into the repo.
- A venv with a dangling or foreign interpreter is detected by the execution
  probe and rebuilt automatically; the 2026-06-11 failure shape self-heals.
- Legacy in-repo `.venv`/`.relay-scratch` are removed on first start.
- `./visual-relay check` green; C#/AXAML files under the 300-line guard;
  Conventional Commit subjects.

## Notes

- The size guard (`tools/guards/check-file-size.sh`) only scans `*.cs`/`*.axaml`,
  so backend.sh (currently ~311 lines) is exempt — still, prefer deleting the
  dual-path branch over adding a third path.
- Do **not** try to share one venv between environments even though both are
  aarch64-darwin — shebangs and uv-managed interpreter paths are home-prefixed.
- Brew installs already used the XDG paths (the unwritable-repo branch), so the
  installed layout is unaffected; this unifies dev checkouts onto the same
  behavior.
