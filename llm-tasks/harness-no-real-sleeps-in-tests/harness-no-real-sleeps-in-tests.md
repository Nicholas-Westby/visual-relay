# Harness: forbid real sleeps in the test suite (guard + make the suite sleep-free)

## Why

The test suite is fast (~3 min, measured bare) but intermittently balloons to ~8+ min and
exits 137. Root cause (empirically confirmed by sampling the hung process): ~11 test files
embed **real shell sleeps** (`sleep 60`, `sleep 30`, `sleep 9999`, `trap '' TERM; sleep 30`,
perl `sleep 30`) as stand-ins for "a process that won't stop on its own" when exercising the
watchdog/timeout/reap logic. When the watchdog reaps the child fast the test is quick; when
the reap flakes, the real sleep runs its full duration and the suite hangs. Principle: **tests
must never contain real sleeps.**

## Part A — static guard (primary, build-failing)

Mirror the existing precedent `tests/VisualRelay.Tests/ShellScriptSizeGuardTests.cs` +
`tools/VisualRelay.Guards/ShellSizeGuard.cs`:
- New pure matcher `tools/VisualRelay.Guards/RealSleepGuard.cs`: `FindViolations(IEnumerable<(string Path,string Source)>) -> IReadOnlyList<Violation(Path,Line,Snippet,Reason)>`. No I/O.
- New meta-test `tests/VisualRelay.Tests/RealSleepGuardTests.cs`: enumerates the test project's
  own `*.cs` (excluding bin/obj), runs the matcher, asserts **empty**; plus a "gate bites"
  fact (synthetic `sleep 30` in a string literal IS reported) and a "no false positive" fact
  (`sleep 30` in a `///` comment, identifier `SleepDuration`, and `Task.Delay(50, ct)` are NOT).
- **Use Roslyn** (`CSharpSyntaxTree.ParseText`), matching the shell-sleep regex only against
  string-literal token text (regular/verbatim/raw/interpolated). This makes the doc-comment
  false-positive class impossible by construction — note `SwivalSubagentRunnerWatchdogTests.CpuPulse.cs`
  already has `<c>sleep 30</c>` in a doc comment, and the sleep-free rewrite will add more such
  comments. `Microsoft.CodeAnalysis.CSharp` is a new dep — add it to the `Guards` tool project
  (cheap; same `Microsoft.CodeAnalysis.*` family the repo already uses for BannedApiAnalyzers).
- Patterns: flag **all** shell sleeps (threshold 0) via `\bsleep\s+(\d+(\.\d+)?|infinity)\b`
  and the quoted-argv form `["']sleep["']\s*,\s*["']?\d` (catches `ProcessStartInfo("sleep","30")`,
  `ArgumentList={"sleep","30"}`). Flag C# `Thread.Sleep(...)`/`Task.Delay(...)` only when the
  literal duration is **>= 1000 ms AND no real CancellationToken** arg (`.None`/`default` do not
  count). Don't flag non-evaluatable/variable durations (favor false-negatives on the C# arm).
- Allow-list: inline `// vr-allow-sleep: <non-empty reason>` skips that line; a bare marker is
  itself rejected. Self-exempt `RealSleepGuard.cs` + `RealSleepGuardTests.cs` by filename
  (they contain fixtures), exactly as `SplitGuardVerificationTests.Conventions.cs` self-exempts.

## Part B — make the suite sleep-free (the guard is red until this is done)

Rewrite the ~11 files (full inventory in the brainstorm; classes below):
- **Class A — "blocked/idle until killed"** (the majority): replace the sleep with a 0-CPU,
  no-timer, block-forever child — `exec tail -f /dev/null` (POSIX-gated tests; `ProcessCapture`
  does NOT redirect stdin, so `cat`/`read` would hit EOF — `tail -f /dev/null` is the right idiom).
  Because there's no duration to wait out, a flaked reap can't balloon. Pair with a SHORT C#
  kill-deadline so a regressed watchdog fails in seconds: tighten existing absolute ceilings to
  `window + small margin`, and where there is none (e.g. `ActivityWatchdogSocketWedgeTests`'s
  `absoluteCeilingMs:0`) add `watchdogCts.CancelAfter(window + 8_000)` as the regression backstop.
- **Class B — "survives a measured interval then succeeds"** (the few: `TierWindows.ActivityPulsesExtendPastFlatCap`,
  `SlowButAlive`, etc.): convert to pure `ActivityWatchdog` unit tests driven by an **injected
  clock** (the repo already uses this pattern for `WatchdogTimeouts.Resolve`/`TryDecideSocketWedge`).
  This needs a small production seam: `ActivityWatchdog.WaitAsync` should accept an injectable
  time source instead of real time. Worth it — it also cleans up `AbsoluteCeilingKillsDespiteActivity`.

## Part C — runtime backstop (secondary)

Extend the CLI `check` gate's existing TRX parse (`tools/VisualRelay.Cli/TrxFailureParser.cs`,
tested by `CliTrxFailureParserTests`) with a `SlowTestGuard` that fails if any single
`<UnitTestResult duration=...>` exceeds **30s** (clears the legit ~≤20s watchdog tests with CI
headroom; a flaked ~60s sleep trips it). Catches unknown wasters the static matcher can't see.

## Done when

- `RealSleepGuardTests` is green against a fully sleep-free suite; gate-bites + no-FP facts pass.
- Bare `dotnet test` wall-clock ≈ test Duration (no multi-minute sleep tail); no exit-137.
- Watchdog tests still prove their behavior (now via block-forever + short C# deadline, or an
  injected clock) and FAIL FAST on a regression. `./visual-relay check` green; Conventional Commit.

## Coordination

Unblocks [[harness-verify-uses-exit-code-not-parsing]] — once the suite exits 0 cleanly, verify
can drop the TRX wrapper and use the raw exit code. Implement with TDD (matcher unit tests →
red live gate → rewrite to green). General principle, but the guard scans VR's OWN suite.
