# Allow JetBrains Telemetry Writes in the Sandbox (Kill the InspectCode "Crash" Noise)

Every sandboxed InspectCode run emits:

> `Component JetBrains.UsageStatistics.Collectors.ProcessDiagReporter construction has failed …
> Access to the path '~/Library/Application Support/JetBrains/Local/InspectCode/v261/processes/…'
> is denied. Operation not permitted`

That's the nono sandbox denying JetBrains' usage-statistics/diagnostics component its app-data
writes. Analysis itself is unaffected (the same output shows `0 Error(s)`), but the noise
repeatedly gets misread — by stage agents and humans — as "InspectCode crashes". Fix it by
allowing that one path in the sandbox, using the knob that already exists.

## Current state (researched)

- `RelayConfig.SandboxExtraAllowPaths` (`src/VisualRelay.Domain/RelayConfig.cs`) is parsed from
  the `sandboxExtraAllowPaths` key in `.relay/config.json`
  (`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`) and appended to the nono
  invocation as `-a <path>` per entry (`src/VisualRelay.Core/Execution/ProcessRunners.cs`, the
  shared nono-prefix builder). The Settings dialog's "Sandbox Paths" expander surfaces it
  (`MainWindowViewModel.Sandbox.cs`).
- `.relay/config.json` currently has **no** `sandboxExtraAllowPaths` key.
- **Portability constraint:** this repo (including `.relay/config.json`) is shared between the
  host and a VM with different usernames, so the entry must not hard-code
  `/Users/<name>/…` — it needs to be written as `~/Library/Application Support/JetBrains` and
  expand per-machine.
- **Tilde expansion already exists — no code needed.** `RelayConfigLoader.cs` expands `~/` and
  `$HOME` **specifically for `sandboxExtraAllowPaths`** at load time ("Expand ~ and $HOME" in
  the `sandboxExtraAllowPaths` parse block), rejects `..` traversal as `Malformed`, normalizes
  to an absolute path, and requires the result to resolve under `$HOME` or the workspace root.
  `ProcessRunners.BuildNonoPrefix` therefore appends already-absolute paths as `-a` — nono
  never sees a tilde. (`SandboxPathInspector.ExpandPath` does the same expansion for the
  Settings display.) `~/Library/Application Support/JetBrains` is under `$HOME`, so it passes
  validation on both host and VM.

## What to build

1. **Pin the expansion in a test if not already pinned.** Check the existing
   `RelayConfigLoader` tests for coverage of `~/` and `$HOME` expansion of
   `sandboxExtraAllowPaths`; add a case if missing (a `~/…` entry loads as the absolute
   home-rooted path). No production code change is expected.
2. **Add the entry.** `sandboxExtraAllowPaths: ["~/Library/Application Support/JetBrains"]` in
   `.relay/config.json` (this covers the failing `Local/InspectCode/v261/processes/…` subtree).
3. **Verify against the real symptom.** Run the repo's inspect path (or a sandboxed
   `dotnet jb inspectcode` invocation matching how the gate runs it) and assert the
   `ProcessDiagReporter` / `Operation not permitted` line no longer appears in the output.
   Before/after grep of the captured output is sufficient evidence — include it in the summary.

## Done when

- A sandboxed InspectCode run produces no JetBrains access-denied noise.
- The config entry is written tilde-portable (`~/…`), and a loader test pins that
  `sandboxExtraAllowPaths` expansion (existing behavior in `RelayConfigLoader.cs`).
- `./visual-relay check` passes.

## Guardrails

- Allow exactly this one JetBrains directory — do not widen the sandbox further, and do not
  switch to `--caches-home`/HOME-redirect approaches in this task (considered; the allow-path
  is the minimal change matching the existing knob).
- Do not reorder or reformat unrelated `.relay/config.json` keys; minimal diffs.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`).
