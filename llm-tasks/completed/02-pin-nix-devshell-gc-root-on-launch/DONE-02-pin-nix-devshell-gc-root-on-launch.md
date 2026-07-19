# Pin the nix devshell as a GC root so garbage collection can't strand a running app

Observed 2026-07-18: a nix garbage collection deleted the devshell's nono
store path while the app was RUNNING a drain. The app's PATH pointed at a
dead `/nix/store/…-nono-…/bin` entry, so every subsequent sandbox spawn (and
the ToolPresence gate on the next launch) would have failed until
`nix develop` was re-run by hand to rebuild the closure. Root cause: the
bootstrap's `_ensure_devshell` enters the environment with a plain
`nix develop --command bash "$0" …` (visual-relay:78), which registers no GC
root — and a running process's environment is not a GC root, so every store
path the app depends on (dotnet, git, nono, shfmt, …) is fair game for
collection the moment the `nix develop` process has exec'd into the app.
`nix develop --profile <path>` fixes this: it records the dev shell's closure
in a profile symlink that IS a registered GC root, so collection can never
remove it while the profile exists.

## Prescribed approach

Add `--profile` to the devshell entry in the bootstrap script. The profile
must live at a machine-generic, user-scoped location — never a hardcoded
absolute path — derived the XDG way:
`"${XDG_DATA_HOME:-$HOME/.local/share}/visual-relay/nix-dev-profile"`,
matching the user-data root the backend venv already uses. This stays inside
the bash bootstrap because it must happen pre-dotnet; keep the addition
minimal (the bootstrap has a fixed 100-logic-line ceiling enforced by
`./visual-relay guards`, and its header forbids new behavior beyond
bootstrap-essential work — this qualifies, but only barely, so no
gold-plating).

### Steps

1. In `_ensure_devshell`, before the `exec … nix develop` line: compute the
   profile path from `${XDG_DATA_HOME:-$HOME/.local/share}`, `mkdir -p` its
   parent directory, and append `--profile "$profile"` to the existing
   `nix develop` invocation. No other flow changes: published/brew installs
   and the no-nix path already return before this point and must keep doing
   so.
2. Bound generation growth: each launch adds a profile generation. Before
   entering, when the profile already exists, best-effort
   `"$nix" profile wipe-history --profile "$profile" || true` so the
   directory holds roughly one generation instead of growing forever. Failure
   of the prune must never block launch.
3. Constraint to document in the script comment (one line): with multiple
   simultaneous checkouts on different flake.lock revisions, the single
   shared profile protects only the most recently launched one — accepted
   trade-off; per-checkout profiles are out of scope.
4. Re-run `./visual-relay format` (shfmt) and confirm the shell-size guard
   still passes with the added lines.

## Tests (red first)

The bootstrap has no unit-test harness; enforcement is guard-level. Red-first
here means: before implementing, capture the current behavior —
`nix develop … ` in the script has no `--profile`, and
`nix-store --query --roots <path of a devshell tool like nono>` lists no
visual-relay profile. The change must flip both observations. Guard suite
(`./visual-relay guards`) must stay green at the new line count.

## Verification

`./visual-relay check` green. Manual, on a nix machine: run
`./visual-relay launch`, quit; confirm
`~/.local/share/visual-relay/nix-dev-profile` exists and
`nix-store --query --roots "$(command -v nono)"` (from inside the devshell)
lists that profile; run `nix store gc --dry-run` (or a real GC) and confirm
the devshell's store paths are NOT collected; relaunch works without
rebuilding. Launch twice and confirm generations do not accumulate beyond
current + one.
