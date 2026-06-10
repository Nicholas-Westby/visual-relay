# Launcher drops all subcommand arguments when re-entering through nix develop

`./visual-relay <cmd> <args...>` silently loses `<args...>` whenever `dotnet` is not on
PATH and the launcher re-enters itself through `nix develop`. Evidence (2026-06-09, a
shell with no dotnet): `./visual-relay gen-backend-config tools/backend/litellm-config.yaml`
exits 2 with `usage: VisualRelay.GenBackendConfig <template-path>` — the tool ran inside
nix develop but received zero arguments. The same failure shape hits `run-task <root>
<taskId>` (the headless pipeline driver) and every other arg-taking subcommand, and it is
why `tools/backend/backend.sh start` logs `gen-backend-config unavailable; using static
config` on dotnet-less shells (backend.sh:190 shells out to `visual-relay
gen-backend-config <template>`).

ROOT CAUSE — `visual-relay:28` defines `_require_dotnet()` whose nix fallback re-execs:

    exec env -u DOTNET_ROOT "$nix_bin" develop --command bash "$0" "$cmd" "$@"

but `_require_dotnet` is a **bash function, and every dispatch case calls it bare**
(`visual-relay:124-175`, e.g. `run-task) _require_dotnet`). Inside a function `"$@"`
expands to the *function's* arguments — empty — not the script's remaining arguments, so
the re-exec carries only `"$cmd"` and drops everything after the subcommand name. The bug
is invisible on machines where dotnet is on PATH (the function returns before reaching
the exec), which is why source-checkout devs with dotnet never see it.

## Goal

Arguments survive the nix-develop re-entry byte-for-byte: `./visual-relay <cmd> <args...>`
behaves identically whether or not `dotnet` was on PATH, for every subcommand (`run-task`,
`gen-backend-config`, `init`, ...), including arguments containing spaces. Regression-tested.

## Approach (suggested)

- Pass the script's args into the function at every call site (`_require_dotnet "$@"`) so
  the function's `"$@"` *is* the script's remainder, or capture them once into a global
  array right after the top-level `shift` (`ARGS=("$@")`) and exec with `"${ARGS[@]}"`.
  Audit the launcher for any other function that references `"$@"` expecting script args.
- Regression test: the xunit suite already asserts on shell entry points (e.g.
  `tests/VisualRelay.Tests/Installer5DocsTests.cs`). Add a test that runs `bash
  visual-relay gen-backend-config /tmp/fake.yaml` with a crafted PATH containing **no
  dotnet** and a **stub `nix`** executable that appends its argv to a file, then asserts
  the recorded argv still contains `gen-backend-config` *and* `/tmp/fake.yaml` (and a
  space-containing extra arg). The stub keeps the test hermetic — no real nix or dotnet
  re-entry needed.
