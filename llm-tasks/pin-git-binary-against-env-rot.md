# Long runs lose git mid-flight: nix-shell env rot breaks the xcrun git shim

Proven 2026-06-10 by the commit-gate diagnostics from `c2388bc`: a finished run flagged
at stage 11 with `target root is not a git repository (git exit 1): xcrun: error:
missing DEVELOPER_DIR path: /nix/store/…-apple-sdk-14.4`. Under `nix develop` on macOS,
`git` resolves through Apple's xcrun shim, which honors the shell's `DEVELOPER_DIR` —
a nix store path. Multi-hour driver processes outlive nix re-evaluations/GC of that
path; every later git invocation in the inherited environment fails with exit 1. This
(not filesystem flakiness) also explains the earlier parallelize stage-11 flag
(overnight run, same signature, pre-diagnostics). Retry-on-transient cannot help — the
environment is permanently rotten for the process's remaining lifetime; only
resume-at-commit (fresh env) salvages.

## Goal

Driver git invocations are immune to dev-shell environment rot: a run that starts with
a working git keeps a working git for its entire lifetime, however long, regardless of
nix store churn. Applies to every git call site (GitCommitter, ActiveTaskLock,
PlanningWorktree, red-gate stash logic, …).

## Approach (suggested)

- At driver startup, resolve git ONCE to a stable absolute binary and use it for all
  subsequent invocations: e.g. `xcrun --find git` → realpath, else `command -v git` →
  realpath; if the resolved path lies in `/nix/store`, prefer the system git
  (`/usr/bin/git` with `DEVELOPER_DIR` cleared/`xcode-select -p` default) when it
  works — validate the chosen binary with one `--version` probe at startup and fail
  fast with a clear message if none works.
- Centralize: a single GitInvoker/process-factory holding the pinned path + sanitized
  env (drop `DEVELOPER_DIR`/`SDKROOT` if the pinned binary is the system one), so no
  call site shells out to bare `git` via PATH.
- Tests: process-factory unit tests for resolution order and env sanitization; a
  driver-level test that all git call sites route through the pinned invoker (e.g.
  grep-style architecture test or DI assertion), mirroring existing committer tests.
