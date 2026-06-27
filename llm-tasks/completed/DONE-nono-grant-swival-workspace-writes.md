# Make the nono sandbox actually usable for real stage runs (grant swival's runtime writes)

`RelayConfig.BypassSandbox` defaults to **true** (`src/VisualRelay.Domain/RelayConfig.cs`,
`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`) with the comment "nono wrapping is
broken (exits 1)", so swival currently runs **direct, unsandboxed**. Investigated 2026-06-09:

- nono ITSELF works: `nono run -p vr-guard --allow-cwd --rollback --no-rollback-prompt -- swival
  --version` exits 0; loopback to the LiteLLM proxy under nono returns HTTP 200; the credential
  WARNs (`~/.ssh` etc. blocked by `deny_credentials`) are benign startup notices.
- The real gap: the `vr-guard` profile confines **writes** to the workspace (CWD) + `~/.config/swival`
  only. A real stage run writes OUTSIDE those (a cache/state dir) and nono blocks it → swival exits 1
  → the stage flags instantly. (Empirically, a `~/.cache/...` write under nono was denied while
  `~/.config/swival` and CWD writes succeeded.)

This matters because `sandbox-2-make-nono-a-required-dependency-...` will make nono *required*; if
nono is still broken for real runs, that re-breaks the self-hosting pipeline (the pipeline mocks
the process layer, so sandbox-2 can pass Verify yet break real `run-task` — see memory
`pipeline-mocks-process-layer-blindspot`). So this is a **prerequisite** for sandbox-2 to be safe,
and for ever flipping `BypassSandbox` back to false.

## Goal
nono `vr-guard` can wrap a *real* swival stage run end-to-end (Implement included) without a
write-block crash, so the sandbox is genuinely usable — then `BypassSandbox=false` can be the
default with confidence.

## Approach (suggested)
- Find exactly which path swival writes that nono denies during a real stage (run a stage under
  `nono run -p vr-guard ... -- swival ...` in a scratch repo and read the denial; or `nono learn`).
  Likely a cache/state dir like `~/.cache/swival` or a temp path.
- Either (a) grant that path read-write in the `vr-guard` profile (`~/.config/nono/profiles/vr-guard`),
  or (b) point swival's cache/state into the workspace or `~/.config/swival` (already granted) via
  env/flags so nono's workspace-confinement is satisfied. Prefer the narrowest grant that works.
- Validate by running a REAL `./visual-relay run-task` with `bypassSandbox=false` and confirming a
  heavy stage (Implement) completes under nono — not just a mocked test. Keep `deny_credentials`
  and keychain/browser denies intact.
- Coordinate with [[swival-command-whitelist-degrade]] (the installer preflight should confirm
  `nono` is present). nono wrapping mechanics already fixed in commit `cd4ea88`.
