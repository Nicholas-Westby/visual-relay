# Harden the test suite: a single test can stall `check` for 34 minutes, and AppIconTests needs an unmanaged `magick`

Running `./visual-relay check` on 2026-06-13 surfaced two distinct failures that compound
each other:

- **A red test:** `AppIconTests.AppIcon_ContainsMultipleResolutions` failed in ~104 ms with
  `System.ComponentModel.Win32Exception: An error occurred trying to start process
  '/opt/homebrew/bin/magick' … No such file or directory`. The test shells out to ImageMagick
  to inspect the `.ico`, but `magick` is not installed on this machine and is **not** provided
  by the nix devshell.
- **A 34-minute stall:** despite that fast failure, the overall `dotnet test` run then sat at
  `VisualRelay.Tests net10.0 … Testing (2025.0s)` for ~34 minutes until the user hit Ctrl-C.
  A single misbehaving test (or, more precisely, the suite's known parallel-headless deadlock)
  wedged the entire `check` run with **no wall-clock cap to break it**.

The icon failure is the visible symptom; the stall is the systemic, expensive one. This task
fixes both — and the stall is the priority, because nothing should ever burn 34 minutes before
the operator notices a wedged suite.

> **Sequencing — do this first.** This is the lead task of a three-task group
> (08 → 09 → 10). Both follow-ups (09 eliminate-reflection-hop bindings, 10 adopt
> InspectCode standards repo-wide) end their acceptance on "`./visual-relay check` green",
> which is currently impossible: the suite stalls and `AppIconTests` errors. Land
> this task first so `check` runs to completion and is bounded; the other two
> depend on it.

## Current state (researched)

### The stall: `check` runs `dotnet test` with no timeout, unlike `test`

The repo already has a working test-timeout watchdog, but the `check` subcommand bypasses it:

- The `test` subcommand wraps `dotnet test` in `_timeout_watchdog`
  (`visual-relay:316`), which launches the command in its own process group, and after
  `VISUAL_RELAY_TEST_TIMEOUT` seconds (default **60**, `visual-relay:227`) `kill -TERM`/`-KILL`s
  the whole tree and forces exit code 124 (`visual-relay:226-284`). A hung suite under
  `./visual-relay test` self-terminates in ~60 s with an actionable message pointing at
  `--blame-hang` (`visual-relay:246-249`).
- The `check` subcommand calls `dotnet test` **raw** — no watchdog wrapper:
  `dotnet test "$SCRIPT_DIR/tests/VisualRelay.Tests/VisualRelay.Tests.csproj" -m:1
  -p:UseSharedCompilation=false` (`visual-relay:356`). There is no `--blame-hang`,
  no `--blame-hang-timeout`, and no shell watchdog. **This is the direct cause of the
  34-minute stall**: once the suite wedged, nothing capped it but the operator's Ctrl-C.

### Why the suite wedges in the first place (the underlying hang)

The 34-min hang is **not** the magick test — that failed fast at 104 ms because
`process.Start()` threw before any wait (`AppIconTests.cs:57`, a plain `[Fact]`, never
participates in a deadlock). The suite's documented hang mechanism is a parallel-execution
deadlock on the process-global Avalonia headless session:

- `TROUBLESHOOTING.md:5-27` describes exactly this signature — `Testing (NNNs)` climbing with
  no test ever completing — and attributes it to headless Avalonia UI tests: Avalonia headless
  uses **one process-global app/dispatcher per process**, and xUnit runs separate test classes
  in parallel by default, so two headless classes overlapping can wedge each other
  (`TROUBLESHOOTING.md:17-21`, `:29-36`).
- Mitigations already in place: all UI tests use `[AvaloniaFact]`/`[AvaloniaTheory]` (one
  shared serialized session), and `HeadlessUnitTestSession` is banned via
  `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`tests/VisualRelay.Tests/BannedSymbols.txt`,
  `TROUBLESHOOTING.md:38-39`). The project ships **no** `xunit.runner.json` and **no**
  `[assembly: CollectionBehavior]` override, so class-level parallelism is on by default.
- Prior deflake work (`llm-tasks/DONE-deflake-timing-sensitive-tests.md`) already identified
  process-global **environment mutation** racing parallel readers (13
  `Environment.SetEnvironmentVariable` sites, concentrated in `KeyEnvFileTests` and
  `KeySetupPanelUiTests`) as a flake source — the same family of "shared global state under
  parallelism" defect that produces wedges, not just flakes.
- The driver-side analogue was already capped by
  `llm-tasks/DONE-cap-and-degrade-long-test-runs.md` (replaced
  `Timeout.InfiniteTimeSpan` in `ShellTestRunner`). **The operator-facing `check` path was
  never given the same treatment** — the watchdog exists for `test` but not `check`.

Conclusion: the strongest-supported root cause of the stall is **the missing watchdog on the
`check` path** (`visual-relay:356`), allowing the suite's known parallel-headless / shared-
global-state deadlock to run unbounded. There is no single hung product call site to "fix" —
the bare `WaitForExit()` calls in the test helpers (`GitCommitterTests.cs:289`,
`RelayDriverGitCommitTests.cs:296`, `PreCommitHookTests.cs:113,171`, `TestDoubles.cs:295`,
`CommitTestRunners.cs:115`) all drive short-lived `git` invocations and are not the observed
hang; the process-spawning watchdog/installer tests use bounded
`CancellationTokenSource(TimeSpan.FromSeconds(15))` (e.g. `Installer5LauncherTests.cs:46`,
`VisualRelayTestCommandTimeoutTests.cs:271`). The fix must therefore be a **cap** that bounds
any future wedge, plus making the wedge surface its culprit.

### The magick dependency

- `AppIconTests.AppIcon_ContainsMultipleResolutions` (`AppIconTests.cs:39-91`) builds a
  `Process` with a hardcoded `FileName = "/opt/homebrew/bin/magick"` (`AppIconTests.cs:48`),
  runs `magick identify <app-icon.ico>`, reads stdout, `WaitForExit()`s (`:57-59`), then parses
  the per-resolution lines and asserts the ICO contains 16/32/48/64/128/256 px sub-images
  (`:84-90`). When the binary is absent, `process.Start()` throws `Win32Exception` and the test
  errors — it does not skip.
- The nix devshell provides only `dotnet-sdk_10 git bash icu openssl zlib nono uv python313`
  (`flake.nix:20-30`) — **`imagemagick` is not in the list**, and the test points at a
  Homebrew path (`/opt/homebrew/bin/magick`) that does not exist on this machine. The repo is
  nix-first per recent commits, and the shared host/VM split means per-machine global installs
  are exactly what the devshell is supposed to eliminate.

## What to build

Two independent, complementary fixes. Write the failing test first for each (TDD).

### 1. Cap the `check` test run so a wedge can never stall it (priority)

Bound the `check` path's `dotnet test` the same way the `test` path is already bounded, and
make a wedge name its own culprit:

- **Route `check`'s `dotnet test` through the existing `_timeout_watchdog`**
  (`visual-relay:356` → wrap exactly as `visual-relay:316` does). This is the smallest,
  most consistent change: the watchdog, its process-group kill, the exit-124 override, and the
  actionable stderr message (`visual-relay:226-284`) all already exist and are tested — reuse
  them rather than inventing a second mechanism. After the change a wedged suite under `check`
  self-terminates in `VISUAL_RELAY_TEST_TIMEOUT` seconds instead of running for 34 minutes.
- **Pick a `check`-appropriate cap.** The default 60 s is tuned for the fast `test` loop; a
  full `check` legitimately builds first and runs the whole suite. Either raise the default for
  the `check` invocation (e.g. export a larger `VISUAL_RELAY_TEST_TIMEOUT` for that call, or
  add a separate `VISUAL_RELAY_CHECK_TEST_TIMEOUT`) so a healthy run is never killed, while
  still bounding a wedge to single-digit minutes — **never** 34. Justify the chosen number in
  the script comment.
- **Make the cap actionable on the `check` path too.** When the watchdog fires under `check`,
  the existing message already tells the operator to run `./visual-relay test --blame-hang
  --blame-hang-timeout 30s` to dump the wedged test(s) (`visual-relay:248`). Confirm that
  message reaches the operator on the `check` path (it shares the watchdog), and that `check`
  exits non-zero (124) so it fails the gate rather than appearing to pass.
- **Decide and document** whether to additionally pass `dotnet test --blame-hang
  --blame-hang-timeout <t>` on the `check`/`test` invocation so the culprit is captured
  automatically rather than only via a manual re-run. If you add it, account for the multi-GB
  `TestResults/` dump (`TROUBLESHOOTING.md:23-27`) — gate it behind an env flag or a modest
  per-test hang timeout, and keep `TestResults/` gitignored. State the choice in the task's
  commit body.

### 2. Make AppIconTests independent of an unmanaged global `magick`

Choose **skip-when-missing**, not "provision a global binary." Rationale: the repo is
nix-first and the devshell is the single source of provisioned tooling; an icon-resolution
assertion must never hard-depend on a Homebrew path. Two coordinated changes:

- **Guard the test:** before spawning, resolve `magick` from `PATH` (not a hardcoded
  `/opt/homebrew/bin/magick`, `AppIconTests.cs:48`). If it is not found, **skip** the test
  (xunit.v3 supports a dynamic skip — e.g. `Assert.Skip(...)`/`SkipWhen`, or
  `[Fact(Skip = …)]` driven by a `PATH` probe), with a message saying ImageMagick is absent
  and how to get it (enter the nix devshell). The other four AppIconTests
  (`AppIcon_FileExists`, `Csproj_HasApplicationIcon`, `MainWindow_ReferencesAppIcon`,
  `OldAvaloniaLogo_Removed`) are pure file/XML checks and stay unconditional — they already
  give meaningful coverage without `magick`.
- **Add `imagemagick` to the devshell** (`flake.nix:20-30` `packages`) so that *inside the
  managed environment* the resolution assertion actually runs and protects the icon. Resolve
  the binary from `PATH` so it works both for the nix-provided `magick` and any locally
  installed one. This keeps the test meaningful in the canonical environment while never
  failing a bare host that lacks the tool.
- Mirror the same guard for any other AppIconTests that would shell out (only
  `AppIcon_ContainsMultipleResolutions` does today).

## Done when

- **No stall:** a wedged/deadlocked suite under `./visual-relay check` is killed by the
  watchdog within the configured cap (single-digit minutes, never 34) and `check` exits
  non-zero — verified by routing `check`'s `dotnet test` through `_timeout_watchdog`
  (`visual-relay:356`) exactly as `test` does (`visual-relay:316`), with the watchdog's
  actionable `--blame-hang` guidance reaching the operator on the `check` path. A normal,
  healthy `check` run completes well under the cap and is unaffected.
- **Covered by a test:** the existing watchdog has script-level coverage
  (`VisualRelayTestCommandTimeoutTests.cs`); extend/assert that the `check` dispatch also
  invokes the watchdog (or that a simulated hung command under `check` exits 124 with the
  timeout message) — write that assertion first and watch it fail against the current raw
  `dotnet test` at `visual-relay:356`.
- **AppIconTests resilient:** with `magick` absent from `PATH`,
  `AppIcon_ContainsMultipleResolutions` **skips** (not errors, not the current
  `Win32Exception`) with a clear message; with `magick` present it still asserts the
  16/32/48/64/128/256 px resolutions. The binary is resolved from `PATH`, never the hardcoded
  `/opt/homebrew/bin/magick`. Add a failing test first that proves the missing-binary path no
  longer throws.
- **Devshell provides the tool:** `imagemagick` is in `flake.nix` `packages` so the resolution
  assertion runs inside the managed nix environment.
- `./visual-relay check` is green (and demonstrably bounded); changed files stay under
  300 lines; Conventional Commit subjects (e.g. `fix(check): cap dotnet test via the timeout
  watchdog` and `test(icon): skip resolution check when magick is absent`).
