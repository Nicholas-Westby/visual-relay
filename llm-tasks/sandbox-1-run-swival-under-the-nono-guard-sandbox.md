# Swival runs unsandboxed, so a stray shell command can delete files anywhere on the machine

Visual Relay launches each Swival subagent with no OS sandbox. `BuildArguments`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs:111-129`) emits `-q --profile … --base-dir
<TargetRoot> --files <scope> --commands <mode> …` and nothing else — so Swival runs with the
launching user's full filesystem rights. Swival's *default* `--sandbox builtin` only applies
**app-layer path guards to Swival's own file tools** (the report fixtures record
`"sandbox": { "mode": "builtin" }`, e.g. `tests/VisualRelay.Tests/Fixtures/stage1-attempt1.report.json:25-27`).
A shell command is an **unguarded write/delete escape** — see the note in
`llm-tasks/parallelize-planning-across-tasks.md:54` ("file-edit tools are sandboxed by
`files=none/some`, but shell is an unguarded write escape"). A model that runs
`rm -rf <wrong path>`, a bad `mv`, or `python -c "shutil.rmtree(...)"` can therefore destroy
files outside the project being driven.

This is **accident containment**, not defense against a malicious agent: stop accidental
destruction outside the workspace, restrict nothing else (reads, network, and tools — including
Playwright/Chromium — stay fully open).

## Goal

Run every Swival subagent under **nono** (OS-enforced: Seatbelt on macOS, Landlock on Linux)
using a permissive **guard profile** that confines *writes and deletes* to the target
workspace while leaving reads, network, and tool execution unrestricted. Sandboxing is **on by
default**; a single config flag (`bypassSandbox`, set by the UI checkbox in
`sandbox-3-add-a-bypass-nono-checkbox-to-the-ui.md`) turns it off. A *missing* nono binary is a
hard error, not a silent downgrade — that is handled in
`sandbox-2-make-nono-a-required-dependency-of-the-installer-and-launcher.md`.

## Approach (suggested)

- **Ship the guard profile** as a repo artifact at `packaging/nono/vr-guard.json` (verified
  content below). `sandbox-2` installs it to `${XDG_CONFIG_HOME:-$HOME/.config}/nono/profiles/`.
- **Add the flags in `BuildArguments`** (`ProcessRunners.cs:111-129`), gated on the config:
  ```csharp
  if (!_config.BypassSandbox)
      args.AddRange(["--sandbox", "nono", "--nono-profile", "vr-guard", "--nono-rollback"]);
  ```
  `_config` (a `RelayConfig`) is already a field on `SwivalSubagentRunner`, so no signature
  change is needed. Swival drives the nono invocation itself (its native `--sandbox nono`),
  so VR does **not** need to wrap `nono run` or pass `--allow-gpu` (verified — see Notes).
- **Add `bool BypassSandbox` to `RelayConfig`** (`src/VisualRelay.Domain/RelayConfig.cs`),
  default it in `RelayConfigLoader.Defaults` (`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:8-27`)
  to `false` (= sandbox on), and merge it in `TryLoadAsync` (lines 87-99) with the existing
  helper: `BypassSandbox = OptionalBool(root, "bypassSandbox", defaults.BypassSandbox)`.
- **Leave network open** (do NOT pass `--nono-block-net`): Swival must reach the LiteLLM backend
  at `127.0.0.1:4000` and the provider APIs. Verified that loopback + outbound are allowed by
  default under the guard profile.
- **Confirm the workspace is writable under the sandbox.** Everything Swival writes lives under
  `<TargetRoot>` — `swival.toml` (written by `SwivalProfileSession.PrepareAsync`), plus
  `.relay/`, `.relay-scratch/`, `.swival/`. `swival --sandbox nono` should grant its `--base-dir`
  writable to nono automatically; verify it does (and that it passes nono's non-interactive
  `--allow-cwd` requirement). If it does not, the guard profile / invocation must explicitly
  grant `<TargetRoot>` read+write.

### The verified `packaging/nono/vr-guard.json`

```json
{
  "extends": "swival",
  "meta": {
    "name": "vr-guard",
    "description": "Visual Relay guard: broad read + network + tools open; writes and deletes confined to the granted workspace. Accident containment, not adversarial isolation."
  },
  "filesystem": { "read": ["/"] },
  "allow_parent_of_protected": true,
  "unsafe_macos_seatbelt_rules": [
    "(allow user-preference-read)",
    "(allow ipc-posix-shm*)",
    "(allow mach-register)",
    "(allow sysctl-read)",
    "(allow iokit-open)",
    "(allow iokit-get-properties)",
    "(allow system-socket)"
  ]
}
```

## Files

- `src/VisualRelay.Core/Execution/ProcessRunners.cs` (`BuildArguments`; keep under 300 lines).
- `src/VisualRelay.Domain/RelayConfig.cs` (add `bool BypassSandbox`).
- `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (`Defaults` + `TryLoadAsync` merge).
- `packaging/nono/vr-guard.json` (New — the artifact above).
- `README.md` (document the sandbox + the `bypassSandbox` key).

## Tests (write the failing tests first)

- **BuildArguments**: with default config (`BypassSandbox == false`), the argument list contains
  `--sandbox nono`, `--nono-profile vr-guard`, and `--nono-rollback`; with
  `BypassSandbox == true` it contains none of them. Extract `BuildArguments` to a testable seam
  if it is not already reachable from a test.
- **RelayConfigLoader**: `Defaults().BypassSandbox` is `false`; a `.relay/config.json` with
  `"bypassSandbox": true` flips it (extend the existing `OptionalBool`-merge coverage that
  already tests `baselineVerify`/`archiveOnDone`).

## Sequencing

Foundation for the batch. `sandbox-2` (installer/launcher ensures nono + installs this profile)
and `sandbox-3` (UI checkbox that writes `bypassSandbox`) both depend on the flag name
`bypassSandbox` and the profile name `vr-guard` — keep both **stable**.

## Done when

- With nono installed and `vr-guard` present, a run executes Swival under nono and a shell `rm`
  (or `rm -rf`, or a new-file write) targeting a path **outside** `<TargetRoot>` is **blocked**
  ("Operation not permitted"); deletes/writes **inside** `<TargetRoot>` succeed.
- Reads anywhere, outbound + loopback network, and a Playwright/Chromium screenshot all still
  work under the sandbox.
- `"bypassSandbox": true` in `.relay/config.json` removes the flags and runs Swival without nono.
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.

## Notes

Empirically verified on macOS 26.4 / Apple Silicon with nono 0.62 and swival 1.0.28:

- The guard model behaves exactly as required: read-anywhere + network + Chromium screenshot
  ALLOWED; delete / `rm -rf` / new-file-write OUTSIDE the granted workspace BLOCKED; delete +
  write INSIDE allowed; nono's own `~/.nono` stays protected.
- `allow_parent_of_protected: true` is **required** — without it nono refuses `read: ["/"]`
  because `/` contains `~/.nono`.
- Chromium needs several non-file Seatbelt ops that nono's base policy omits (it otherwise
  SEGVs at startup with no logged denial). The `unsafe_macos_seatbelt_rules` above are the
  verified minimal set; `process-fork`/`process-exec*` are **not** needed. Crucially these
  explicit `iokit-*` rules make the browser work **without** nono's `--allow-gpu` flag — which
  matters because Swival's `--sandbox nono` exposes `--nono-profile/--nono-rollback/
  --nono-block-net/--nono-allow-domain/--nono-network-profile` but **no GPU passthrough**.
- `unsafe_macos_seatbelt_rules` are **macOS-only** (ignored on Linux/Landlock). The read/write
  confinement is cross-platform, but the Chromium-under-Landlock path was **not** verified —
  validate on Linux separately (Landlock does not mediate the syscalls that crash Chromium under
  Seatbelt, so it may "just work," but confirm).
- Rollback (`--nono-rollback`) snapshots the workspace; to undo a destructive session use
  `nono rollback restore <id> --snapshot 0` (snapshot 0 = pre-session baseline; the default
  restore target is the *last* snapshot, which no-ops).
