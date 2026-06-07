# `./visual-relay test` should self-timeout after 60s

A deadlocked test (e.g. two overlapping Avalonia headless UI tests sharing the process-global
Avalonia app) makes `dotnet test` hang **forever**. The `test` subcommand in `./visual-relay`
(the `test)` case, lines 49–51) runs `dotnet test … "$@"` with no wall-clock cap, so a hung
suite blocks the dev loop (and relay runs) indefinitely until something kills it manually.

The suite runs in ~10s today, so a 60s cap is generous headroom. If a normal run ever exceeds
60s, that itself is a signal worth investigating — not something to wait out.

## Recommended approach

- Wrap the `dotnet test` invocation in the `test)` case of `./visual-relay` in a **60s
  wall-clock watchdog**. Default 60s, overridable via an env var (e.g.
  `VISUAL_RELAY_TEST_TIMEOUT`).
- On timeout: kill the process tree, exit non-zero, and print a clear message pointing at
  `TROUBLESHOOTING.md` and the `./visual-relay test --blame-hang --blame-hang-timeout 30s`
  diagnostic. Do **not** write a hang dump by default — they are multi-GB; `--blame-hang`
  stays the opt-in diagnostic.
- **Portability:** macOS has no `timeout(1)` by default. Inside `nix develop` coreutils
  provides `timeout`, but the direct-PATH path (when `dotnet` is already available and the
  script does not re-enter nix) may not. Detect `timeout`/`gtimeout`, or implement a small
  bash watchdog (background the run, sleep, kill the tree) that does not depend on a tool
  that may be absent.
- Apply the cap to the default `./visual-relay test`; keep it simple (capping
  argument-supplied runs the same way, with the same env override, is fine).

## Done when

- A hung `./visual-relay test` kills itself and exits non-zero after 60s (configurable via
  env var), printing a message that references the `--blame-hang` diagnostic and
  `TROUBLESHOOTING.md`.
- Normal (~10s) runs are completely unaffected.
- Works whether or not the script re-entered `nix develop` (no dependency on a `timeout`
  binary that might not be installed).
- The default path creates no large dump files.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
