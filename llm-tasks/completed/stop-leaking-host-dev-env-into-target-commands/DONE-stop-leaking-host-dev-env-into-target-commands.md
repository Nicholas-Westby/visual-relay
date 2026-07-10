# Stop leaking VR's dev-shell environment into target-repo commands

VR is launched through `./visual-relay`, which re-execs into a nix devshell before starting the
app. That shell injects toolchain variables into VR's process environment — observed leaking into
target-repo verify/guard commands in real runs: `SDKROOT` and `DEVELOPER_DIR` pointing at a nix
apple-sdk (which mismatches the host's Xcode Swift and breaks `swift build`/`swift test` with
"this SDK is not supported by the compiler"), a `TMPDIR` under `/tmp/nix-shell.*`, and a nix-first
`PATH`. Operators had to hand-write `env -u SDKROOT -u DEVELOPER_DIR …` prefixes into every Swift
repo's testCmd — exactly the kind of per-repo configuration VR's design philosophy rejects.
**Maintainer ruling: VR must work with minimal configuration. No per-repo env map. The fix is
internal and automatic: commands run against a target repo see the USER's environment, not VR's
build-shell internals.**

## Diagnosed mechanism (verified, do not re-derive)

- The launcher script (`visual-relay`) enters `nix develop` once (`_ensure_devshell`) and execs the
  CLI; everything VR spawns inherits the devshell env.
- Target commands (testCmd/guardCmd/formatCmd/bootstrap-check, and the sandboxed stage shells) get
  that env plus the overrides in `SwivalSubagentRunner.BuildSandboxEnvironment`
  (`src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs`).
- Empirical proof of harm: inside the devshell, `swift test --disable-sandbox` fails on the nix
  SDKROOT; with SDKROOT/DEVELOPER_DIR unset it passes 228/228 (patternsmith).

## What to build

1. **Snapshot the pre-devshell environment at launch.** In the `visual-relay` bootstrap script,
   BEFORE entering the devshell, capture the caller's environment (e.g. `env -0` to a file under
   the run scratch dir, or a single exported variable pointing at the snapshot file — keep the
   script within its size guard; a tiny helper is fine). The packaged/Homebrew fast path (no nix)
   needs no snapshot — the live env is already the user's.
2. **Use the user env as the BASE for target-repo command execution.** Where VR builds the
   environment for commands that run against a target repo (the sandboxed test/guard/format/
   bootstrap runners and the swival stage subprocess), start from the snapshot env when one exists
   (fall back to the current process env when it doesn't), then apply VR's own required overrides
   ON TOP (the existing `BuildSandboxEnvironment` cache redirects, PYTHONDONTWRITEBYTECODE, etc.).
   The effect: a target repo's `swift test` sees the same SDKROOT/PATH it would in the user's own
   terminal; "works in my terminal, fails in VR" ceases to be an env-divergence class.
3. **VR's own tooling keeps its env.** Binary resolution and execution of VR's OWN machinery —
   `nono`, `swival`, git invocations, the backend — continue to use VR's process env (they need
   the nix-provisioned tools). Only the CHILD command/agent environment switches to the user base.
   The nono/sandbox WRAPPER is VR machinery; the command INSIDE the wrapper is the target's.
4. **Self-hosting must keep working.** When the target repo is VR itself, its testCmd (`./test.sh`)
   re-enters the devshell via the same launcher, so the user-base env is fine. Verify this
   explicitly with the existing self-hosting/driver test patterns — do not special-case VR's repo.
5. No new `.relay/config.json` fields. No per-repo anything.

## Tests (red first)

- Runner env test: with a snapshot file present containing a marker var (and NOT containing a
  poison var like SDKROOT that IS set in the current process env), the environment handed to the
  sandboxed test runner's child contains the marker and not the poison, while VR's required
  overrides are still applied on top.
- Fallback test: no snapshot file → current behavior (process env base) — nothing breaks for
  packaged installs.
- Launcher test (script-level, in the existing shell-script test style if present; otherwise a
  CLI-level test): launching through the bootstrap writes a readable snapshot.

## Verification

- `./test.sh` fully green including the new tests.
- Ledger note demonstrating the Swift scenario: a scratch SPM package's `swift test
  --disable-sandbox` testCmd passes under VR without any `env -u` prefix while the VR process env
  carries a deliberately-poisoned SDKROOT.
