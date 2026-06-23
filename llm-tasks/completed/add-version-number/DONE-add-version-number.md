# Add an auto-incrementing 0.x version: bump on commit, show in the UI, log it

> Original ask: *"Add a version number. Make it increment on each commit via a commit hook. It'll be
> 0.x for a while, so make the .x part increment (e.g., 0.1 to 0.2). Show the version number in the UI
> somewhere that makes sense. Include the version number in logs."*

## Current state (researched — verify before editing)

- **No version exists.** `Directory.Build.props`, `Directory.Build.targets`, and the `*.csproj` files
  set **no** `Version`/`VersionPrefix`/`InformationalVersion`; builds get the .NET default `1.0.0`.
  There is no `VERSION` file.
- **Hooks are wired via `core.hooksPath`.** `.githooks/` holds `pre-commit` (the commit-**authority**
  gate — blocks unauthorized commits while a VR run is active, keyed on `RELAY_COMMIT_TOKEN` vs the
  run nonce) and `commit-msg` (conventional-commit validator). `./visual-relay install-hooks`
  (`tools/VisualRelay.Cli/Commands/InstallHooksCommand.cs`) sets `git config core.hooksPath .githooks`.
- **UI title block:** `src/VisualRelay.App/Views/Controls/TopBar.axaml` shows `"Visual Relay"` over
  `"task pipeline"` (the `VR` chip + two TextBlocks) — the natural spot for a small version line.
- **Logging:** `src/VisualRelay.Core/Logging/FileRelayEventSink.cs` and `DrainSummaryLog.cs` write the
  run logs; neither the app (`Program.cs` / `App.axaml.cs`) nor the CLI (`tools/VisualRelay.Cli/Program.cs`)
  currently logs a version at startup.

> **Freshness contract.** Confirm `.githooks/pre-commit`'s structure, `install-hooks`, the TopBar
> markup, and the logging entry points by searching for them; the pre-commit authority logic is
> security-sensitive — read it fully before adding anything.

## Goal

- A single source-of-truth **0.x version** (e.g. a tracked `VERSION` file or `Version.props`), whose
  **`.x` auto-increments on each commit** via the git hook (0.1 → 0.2 → …), staged into that same
  commit.
- The version is **baked into the build** (so the app/CLI can read it) and **shown in the UI** (under
  the TopBar title is the obvious place).
- The version appears **in logs** — at app startup and CLI startup, and ideally in the run-log header.

## Approach (Plan/Implement to refine)

- **Store + bake:** keep the number in a tracked file (`VERSION` at root, or `Version.props`); wire it
  into the build via `Directory.Build.props` (set `VersionPrefix`/`InformationalVersion` from the file)
  so every assembly carries it. The app reads it from its own assembly `InformationalVersion` (no file
  IO at runtime); the CLI/hook can read the file directly.
- **Bump on commit:** add the increment step to the hook path. **Critical interactions — get these
  right:**
  - The existing `pre-commit` is the commit-authority gate and **early-exits** for the authorized
    driver commit (token == nonce). Decide where the bump runs: it should bump on **normal developer
    commits**, and must **not** fight the authority flow during an active VR run (the driver produces
    one sealed commit — a hook-staged VERSION change during a run could violate the authority/seal).
    Safest: only bump when **no** VR run is active (no `.relay/ACTIVE`), so run commits are untouched.
  - `git add` the bumped file within the same commit; ensure no infinite loop (staging in pre-commit
    does not re-fire the hook) and it composes with `commit-msg`.
  - Make it robust to rebases/amends and to `core.hooksPath` not being installed (a fresh clone that
    hasn't run `install-hooks` simply won't bump — acceptable; note it).
- **Show in UI:** expose a `Version` string on the VM (from the assembly) and add a small muted
  TextBlock under "task pipeline" in `TopBar.axaml` (e.g. `v0.42`).
- **Log it:** write the version once at app startup (`App.axaml.cs`/`Program.cs`) and CLI startup
  (`tools/VisualRelay.Cli/Program.cs`, to stderr), and include it in the run-log header via
  `DrainSummaryLog`.

## Tests

- Hook bump logic (factor the increment into a testable C# helper rather than only bash): `0.1`→`0.2`,
  `0.9`→`0.10`, missing/garbled file → sane default; and it's a **no-op during an active VR run**.
- The app/CLI exposes the baked version (read from assembly) — assert it's non-default and matches the
  file.
- A startup/run log line includes the version.

## Out of scope

- Semantic versioning / a path to 1.0 (this is deliberately just 0.x with an incrementing minor).
- Release tagging / publishing.
