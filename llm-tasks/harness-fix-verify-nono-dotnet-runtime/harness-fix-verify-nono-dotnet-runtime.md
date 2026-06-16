# Harness: make verify-through-nono actually run `dotnet test` (runtime resolution under nono)

`harness-sandbox-package-manager-writes` (commit `c829c65`) routed verification through the nono
`vr-guard` sandbox (`nono run -p vr-guard --allow-cwd [-a …] -- <cmd>`). The **wiring is correct and
leaks no escape** (security-reviewed). But on a **nix-provisioned .NET toolchain** (this repo's own
self-hosting host), the verification command **fails to launch under nono**:

```
nono run -p vr-guard --allow-cwd -- dotnet test …
  → DOTNET_ROOT = <not set>
  → test host resolves Microsoft.NETCore.App 10.0.7 at ~/.dotnet (incomplete / wrong version)
    instead of the nix SDK's 10.0.8 at /nix/store/…-dotnet-sdk-10.0.300/share/dotnet
  → https://aka.ms/dotnet/app-launch-failed
```

Plain `dotnet test` (no nono) passes (1018/0/9). The **first task to actually exercise verify-through-
nono flagged after all 5 fix-verify loops** with this `app-launch-failed`. So with `bypassSandbox=false`
(the secure default), **every .NET task's verify fails** — the self-hosting pipeline is blocked.

> **Operational note:** to unblock the current batch, `bypassSandbox` was set to `true` locally in
> `.relay/config.json` (gitignored, uncommitted). **This task must restore the invariant so
> `bypassSandbox` can return to `false`** and a normal .NET task verifies through nono. Re-set it to
> `false` as part of validating this fix.

## Root cause (diagnosed 2026-06-16)

- The `./visual-relay` launcher runs commands via `env -u DOTNET_ROOT … nix develop --command …`, so
  inside the verify environment **`DOTNET_ROOT` is unset** and `dotnet` is the nix SDK (10.0.8).
- With `DOTNET_ROOT` unset, the .NET host uses multi-level lookup and can resolve to a **different**
  install — here a stale `~/.dotnet` (10.0.7) — which mismatches what the test assemblies were built
  against (nix 10.0.8) → `app-launch-failed`. On the host (no nono) this happened to resolve correctly;
  under the nono-wrapped exec it does not (the resolution is sensitive to env/path, and the wrapper
  changes the process's view enough to tip it to the wrong runtime).
- Confirmed: under nono, `dotnet --list-runtimes` succeeds when pointed at a complete SDK; the failure
  is **runtime resolution**, not nono blocking dotnet outright.

## Why the existing tests/review missed it

- The security review verified the **wiring** (sandbox always applied, no escape) — not that `dotnet`
  **functionally launches** under nono.
- The sandbox task's **real-build integration tests** (`NonoRealBuildTests`) are correctly opt-in
  (`VR_RUN_NONO_INTEGRATION=1`, skipped by default — that gating is right). But they were **never run
  on this nix host**, so the runtime-resolution break shipped undetected. A real `dotnet build+test`
  under nono on a nix toolchain is exactly the missing coverage.

## What to build

Keep it **general** (VR drives any repo); do not hardcode nix store paths. The fix belongs in the
sandbox environment builder, which already redirects caches for the sandbox:

- In `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs` `BuildSandboxEnvironment` (the dict of
  env overrides applied to the nono-wrapped exec; already sets `HF_HOME`/`XDG_CACHE_HOME`/`UV_CACHE_DIR`),
  **pin the .NET runtime resolution** so the host can't fall back to a mismatched install under nono:
  set `DOTNET_ROOT` to the **active** SDK root (derive at runtime — e.g. from `dirname $(realpath
  $(command -v dotnet))`, or `dotnet --info`'s "Base Path", not a literal path) and set
  `DOTNET_MULTILEVEL_LOOKUP=0`. Apply this **only** when the sandbox is enabled and a .NET toolchain is
  in use (detect like the test-command/bootstrap detection already does), so non-.NET repos are
  unaffected. Mirror the same env on the Swival run if it shells out to dotnet.
- This must be **toolchain-scoped**, not a blanket env: a Swift/Node/Go repo should not get
  `DOTNET_ROOT`. Reuse the existing toolchain-detection seam.

## Tests (the coverage gap that let this ship)

- **Unit:** `BuildSandboxEnvironment`, for a .NET repo with sandbox enabled, includes a `DOTNET_ROOT`
  pointing at a real SDK base path and `DOTNET_MULTILEVEL_LOOKUP=0`; for a non-.NET repo it does not.
- **Integration (opt-in `VR_RUN_NONO_INTEGRATION=1`, but THIS ONE MUST BE RUN before trusting the
  fix):** a real `nono run -p vr-guard --allow-cwd -- dotnet test` (or restore+build+test) in a scratch
  .NET repo **exits 0** — i.e. the runtime resolves and the test host launches under nono. This is the
  test that fails today (app-launch-failed) and must pass after the fix.

## Done when

- `nono run -p vr-guard --allow-cwd -- dotnet test …` launches and exits 0 on the nix host (no
  `app-launch-failed`); the runtime resolves to the active SDK, not a stale `~/.dotnet`.
- `bypassSandbox` is restored to `false` in this repo's `.relay/config.json` and a normal .NET task
  verifies **through nono** end-to-end (drive one to confirm).
- The env pinning is toolchain-scoped (no `DOTNET_ROOT` for non-.NET repos) and uses no hardcoded
  store paths.
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit subjects.

## Notes / how to validate (important — self-hosting trap)

This fix **cannot be validated by the normal self-hosted verify** while the bug exists: the running
harness binary contains the broken behavior, and with `bypassSandbox=true` the verify skips nono
entirely (so it can't prove dotnet-under-nono works). Validate by **directly** running
`nono run -p vr-guard --allow-cwd -- dotnet test …` against the built tree after applying the fix
(manually, or via the opt-in integration test invoked explicitly), then flip `bypassSandbox=false` and
drive one real task to confirm verify-through-nono is green. Driving this task via an older harness
binary that verifies on the host (the established "fix harness bugs with a clean older binary" pattern)
is also acceptable for landing the code, but the nono-dotnet behavior itself must be confirmed by an
actual nono-wrapped dotnet run.
