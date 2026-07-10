# Plan: stop-leaking-host-dev-env-into-target-commands

## Summary

Capture the user's pre-devshell environment in the bootstrap script before `nix develop` replaces
the process env. Use that snapshot as the base for all target-repo command execution, with VR's
sandbox overrides applied on top. VR's own machinery (nono wrapper, swival binary, git, backend)
keeps using the nix devshell env unchanged.

## Detailed changes

### 1. `visual-relay` — snapshot capture (1 new line, 1 line modified)

**Change A:** Insert ONE new line before `_ensure_devshell` (before current line 37):

```bash
[[ -z "${VISUAL_RELAY_NIX_REENTRY:-}" ]] && { _vr_tmp="${TMPDIR:-/tmp}/.vr-run-$$"; mkdir -p "$_vr_tmp"; env -0 >"$_vr_tmp/user-env"; export VISUAL_RELAY_USER_ENV_SNAPSHOT="$_vr_tmp/user-env"; }
```

Gated on reentry: only captures on the FIRST entry. Uses user's real TMPDIR (not yet nix-polluted).
Logic lines: 15 → 16, well under the 20-line limit.

**Change B:** Modify the `exec` line inside `_ensure_devshell` (line 36) to pass the snapshot var through:

Add `VISUAL_RELAY_USER_ENV_SNAPSHOT="$VISUAL_RELAY_USER_ENV_SNAPSHOT"` to the env prefix, after
`ORIGINAL_CWD="$ORIGINAL_CWD"` and before `"$nix"`.

### 2. `+src/VisualRelay.Core/Execution/UserEnvSnapshot.cs` — new file

Static class `UserEnvSnapshot` with one method:

```csharp
internal static IReadOnlyDictionary<string, string>? Load(
    Configuration.IEnvironmentAccessor? accessor = null)
```

- Reads `VISUAL_RELAY_USER_ENV_SNAPSHOT` from the accessor (or process env if null).
- If absent or file missing → returns null (graceful fallback for packaged/brew installs).
- Parses NUL-delimited `KEY=value` pairs (the `env -0` format). Skips empty entries and malformed
  lines. Uses `Encoding.UTF8` to decode bytes to strings.

### 3. `src/VisualRelay.Core/Execution/ProcessRunners.SandboxEnv.cs` — add method

Add `BuildTargetCommandEnvironment(RelayConfig config, IEnvironmentAccessor? accessor = null)`:

- Calls `UserEnvSnapshot.Load(accessor)`.
- If null (no snapshot): returns `BuildSandboxEnvironment(config)` — current behavior, packaged path.
- If snapshot exists: creates a new dict from ALL snapshot entries, then applies every entry from
  `BuildSandboxEnvironment(config)` on top (VR overrides win on collision).
- Returns the merged dict.

### 4. `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs` — line 25

Change:
```csharp
var env = SwivalSubagentRunner.BuildSandboxEnvironment(config);
```
to:
```csharp
var env = SwivalSubagentRunner.BuildTargetCommandEnvironment(config);
```

### 5. `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs` — line 127

Change:
```csharp
var sandboxEnv = BuildSandboxEnvironment(_config);
```
to:
```csharp
var sandboxEnv = BuildTargetCommandEnvironment(_config);
```

### 6. `+tests/VisualRelay.Tests/UserEnvSnapshotTests.cs` — new test file

Tests (using `DictionaryEnvironmentAccessor` for env var injection):

1. **Load_WithValidSnapshot_ReturnsDict** — write a temp file with `VR_SNAP_MARKER=hello\0PATH=/usr/bin\0`, point accessor at it, assert returned dict has both entries.

2. **Load_WhenEnvVarNotSet_ReturnsNull** — accessor without `VISUAL_RELAY_USER_ENV_SNAPSHOT`.

3. **Load_WhenFileMissing_ReturnsNull** — accessor points to nonexistent file.

4. **BuildTargetCommandEnvironment_WithSnapshot_MergesUserBaseWithVROverrides** — snapshot has `VR_SNAP_MARKER=present` and `PYTHONDONTWRITEBYTECODE=0`. Assert returned dict: marker present (from user), `PYTHONDONTWRITEBYTECODE=1` (VR won), and other VR overrides (`MSBUILDDISABLENODEREUSE=1`, etc.) present.

5. **BuildTargetCommandEnvironment_WithoutSnapshot_FallsBackToSandboxOverridesOnly** — no snapshot → returns same dict as `BuildSandboxEnvironment`.

6. **BuildTargetCommandEnvironment_UserEnvDoesNotIncludeNixOnlyVars** — snapshot does NOT have `SDKROOT`. Assert returned dict does not contain `SDKROOT`. The actual child-process stripping of nix SDKROOT happens in `ProcessCapture.StripLeakedNixSdkEnv`; this test verifies we don't accidentally re-introduce it through the snapshot path.

### 7. `tests/VisualRelay.Tests/Installer5LauncherTests.cs` — add launcher test

**Launcher_CapturesUserEnvSnapshot**: follows the existing launcher test pattern (stub nix → run launcher → check side-effect). Stub nix copies the snapshot file to a known location before exiting. Assert the snapshot exists, is nonempty, and contains a marker env var set in the test harness.

### 8. `tests/VisualRelay.Tests/SandboxEnvForwardingTests.cs` — update existing test

Modify `ProcessCapture_AppliesSandboxEnvironment_ReachesSpawnedChild` (or add a sibling test) to verify that when `BuildTargetCommandEnvironment` is used with a snapshot, the child process sees user env vars from the snapshot AND VR's overrides. Can combine with the new test file.

## How the env reaches child processes (reprise)

ProcessCapture starts from the process env (nix devshell), then applies the `environment` dict as
overrides. With the new `BuildTargetCommandEnvironment`:

- Every key in the snapshot (user's pre-devshell env) overwrites the nix value for that key.
  PATH → user's PATH, TMPDIR → user's real TMPDIR.
- Keys NOT in the snapshot but present in the nix devshell survive (edge cases).
- `StripLeakedNixSdkEnv` still runs on the final child env, removing DEVELOPER_DIR and SDKROOT
  when they contain `/nix/store/` — catches the case where the user didn't have them set.
- VR overrides (PYTHONDONTWRITEBYTECODE, MSBUILDDISABLENODEREUSE, cache redirects) are applied
  last and always win.

## VR machinery unaffected

- `GitInvoker` (git invocations): runs through its own `SanitizeEnvironment`, does NOT go through
  `BuildSandboxEnvironment` or `BuildTargetCommandEnvironment`. Unchanged.
- Backend process, nono profiling: spawned directly, no env dict from the target-command path.
  Unchanged.
- The nono WRAPPER binary: launched by ProcessCapture with the target-command env dict, but nono
  is the WRAPPER around the command. The command INSIDE nono gets the environment via process
  inheritance from nono, which itself got the merged (user-base) env. This is correct: the
  sandboxed COMMAND sees the user env, and nono's own operation (Seatbelt sandbox setup) is
  unaffected by env vars.

## Self-hosting

When VR is the target repo and its `testCmd` is `./test.sh`:
1. Outer VR: captures snapshot, enters nix, runs test runner
2. Test runner: builds target-command env from snapshot (user base), runs `./test.sh` in sandbox
3. `./test.sh` calls `./visual-relay` → `_ensure_devshell` → `VISUAL_RELAY_NIX_REENTRY` is NOT
   set in the child (the snapshot's env doesn't have it), so it re-enters nix normally
4. But wait — the test runner runs in a sandbox (nono). nono inherits the merged env from
   ProcessCapture. The merged env has `VISUAL_RELAY_USER_ENV_SNAPSHOT` set. When `./test.sh` →
   `./visual-relay` → the NEW process would see `VISUAL_RELAY_NIX_REENTRY=` not set, so it would
   try to capture a NEW snapshot. But wait — the snapshot capture is gated on
   `[[ -z "${VISUAL_RELAY_NIX_REENTRY:-}" ]]`. In the child process (the test command), there's no
   `VISUAL_RELAY_NIX_REENTRY` (it's not in the user snapshot and not added by VR). So `:-` expands
   to empty, `-z ""` is true, and it captures a NEW snapshot. But wait, the new capture would
   overwrite the file since we use the same TMPDIR path pattern? No — each run uses `$$` (PID), so
   different temp dirs. The new capture would be in the inner process's TMPDIR (which is now the
   user's real TMPDIR since we overwrote it from the snapshot). Actually hmm — the snapshot
   captures the user's TMPDIR, so the inner process TMPDIR is the user's real TMPDIR
   (`/var/folders/...`). The new snapshot file is `$TMPDIR/.vr-run-$$/user-env` which is a
   different file. So no conflict.

5. Then `_ensure_devshell` runs, enters nix, and the inner VR's own test infrastructure runs.
   This should work because: the inner VR captures a snapshot of ITS current env (which is the
   merged user-base env), enters nix, runs tests. The tests run against the inner VR's
   infrastructure. No infinite loop or file conflict.
