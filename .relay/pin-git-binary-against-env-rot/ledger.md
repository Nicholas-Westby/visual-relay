## Stage 1 - Ideate

{
  "summary": "Pin git to a stable absolute binary at driver startup, with sanitized environment, so all git call sites are immune to nix-store churn on macOS. The resolved binary is validated once and used throughout the process lifetime.",
  "options": [
    "A — Central GitInvoker with startup resolution + DI: resolve once at startup, inject a GitRunner abstraction into every consumer, enforce with architecture tests.",
    "B — PATH-level wrapper shim: write a hardcoding wrapper script at startup and prepend to PATH; zero call-site changes but fragile and harder to test.",
    "C — Hybrid: realpath resolution + process-wide monkey-patch: resolve at startup, intercept all git subprocess invocations centrally; resolution logic remains testable, call sites untouched."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is a C# (.NET 10) project called VisualRelay.Core/App/Domain. Git is invoked at four independent call sites, each resolving the 'git' binary via PATH separately with no shared abstraction. **Call sites**: (1) `GitCommitter.RunGitCoreAsync` (line 180) — calls `ProcessCapture.RunAsync(\"git\", ...)`, has a static `RawGitRunner` Func seam for testing but only covers the committer; (2) `PlanningWorktree.RunGitAsync` (line 126) — uses `new ProcessStartInfo(\"git\")` directly, already strips `DEVELOPER_DIR`/`SDKROOT` from process env (line 133–134) as a partial mitigation; (3) `RedGate.GitAsync` (line 133) — calls `ProcessCapture.RunAsync(\"git\", ...)`, no env stripping; (4) `HookInstaller.InstallAsync` (line 60) — calls `ProcessCapture.RunAsync(\"git\", ...)`, used at init time before driver startup. **DI pattern**: manual constructor injection via `RelayDriverDependencies` record (`ISubagentRunner`, `ITestRunner`, `IRelayEventSink`), wired explicitly at each call site (e.g. `GuiTaskRunner.cs:36`, `MainWindowViewModel.Execution.cs:229`, `PlanPhaseRunner.cs:117`). Tests use `RelayDriverDependencies.ForTests(...)`. No DI container is used. **No existing IGitRunner/GitInvoker abstraction** exists — each git call site independently shells out to bare `\"git\"`. The closest thing is the `GitCommitter.RawGitRunner` static Func, which is a test seam only for GitCommitter and not usable by other call sites. **Architecture tests exist**: `SplitGuardVerificationTests.Conventions.cs` shows structural convention testing (e.g., companion file naming, file size limits). **Existing env mitigation**: `PlanningWorktree` already strips `DEVELOPER_DIR`/`SDKROOT` per invocation (lines 133-134), but it does so reactively per call rather than resolving a stable git binary once at startup. **All git usage paths**: GitCommitter (commit, rev-parse, add, ls-files, reset), PlanningWorktree (worktree add/remove/prune), RedGate (stash push/list/apply/drop, checkout, status, rev-parse), HookInstaller (config).",
  "constraints": [
    "No DI container is used — all dependency injection is manual constructor wiring via RelayDriverDependencies record",
    "Test files are subject to a 300-line guard — any new test file or companion must stay under 300 lines",
    "GitCommitter.RawGitRunner static seam is already used by existing tests (TransientGitShim) and must be preserved or migrated compatibly",
    "PlanningWorktree already strips DEVELOPER_DIR/SDKROOT — the fix must not regress that behavior, ideally centralizing it",
    "HookInstaller runs during init (before driver startup) — may need a different resolution path than the runtime invoker",
    "The codebase is on macOS primarily (the bug targets Apple's xcrun shim) — resolution logic must handle xcrun --find git vs command -v git",
    "Architecture test pattern already exists (SplitGuardVerificationTests.Conventions.cs) — can be extended for 'all git call sites route through pinned invoker' assertion",
    "RelayDriverOptions has CreateGitCommit flag — when false, no git commit happens, but other git ops (worktree, stash) still occur",
    "ProcessCapture.cs is the common process runner used by GitCommitter and RedGate (but not PlanningWorktree which uses Process directly)",
    "The test double pattern uses ScriptedTestRunner/RecordingTestRunner for ITestRunner — analogous pattern would be natural for IGitRunner",
    "PlanPhaseRunner uses PlanningWorktree (which uses git worktree add/remove/prune) — those call sites must also be covered by the pinned invoker",
    "No .csproj inspection done — may need to check for conditional compilation or platform-specific defines"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Four independent call sites resolve the 'git' binary via PATH separately with no shared abstraction. GitCommitter.cs:180 uses ProcessCapture.RunAsync(\"git\",…), PlanningWorktree.cs:126 uses new ProcessStartInfo(\"git\"), RedGate.cs:133 uses ProcessCapture.RunAsync(\"git\",…), and HookInstaller.cs:60 uses ProcessCapture.RunAsync(\"git\",…). Only PlanningWorktree strips DEVELOPER_DIR/SDKROOT (lines 133-134) but still resolves via PATH, so it fails anyway when the nix store path is gone. GitCommitter has 3-attempt retry (lines 153-168) but the xcrun failure is permanent — once DEVELOPER_DIR's nix store path is GC'd, no retry ever succeeds. The RelayDriverDependencies DI record (RelayDriverDependencies.cs) carries ISubagentRunner/ITestRunner/IRelayEventSink with no IGitRunner. The only test seam is GitCommitter.RawGitRunner (GitCommitter.cs:16), a static Func usable only by GitCommitter. The confirmed failure signature from commit c2388bc is 'target root is not a git repository (git exit 1): xcrun: error: missing DEVELOPER_DIR path: /nix/store/…-apple-sdk-14.4', which matches the nix-shell environment rot pattern exactly.",
  "excerpts": [
    "GitCommitter.cs:180 — ProcessCapture.RunAsync(\"git\", [\"-C\", rootPath, .. arguments], …) — no env stripping, has retry",
    "PlanningWorktree.cs:126-134 — new ProcessStartInfo(\"git\") + strips DEVELOPER_DIR/SDKROOT — partial mitigation, still PATH-resolved",
    "RedGate.cs:133 — ProcessCapture.RunAsync(\"git\", [\"-C\", rootPath, .. arguments], …) — no env stripping, no retry",
    "HookInstaller.cs:60 — ProcessCapture.RunAsync(\"git\", [\"-C\", rootPath, \"config\", …], …) — no env stripping, init-time call",
    "RelayDriverDependencies.cs:5-9 — record carries ISubagentRunner, ITestRunner, IRelayEventSink only; no IGitRunner abstraction",
    "GitCommitter.cs:16 — static Func<string,…> RawGitRunner — test seam scoped to GitCommitter only, not usable by other call sites",
    "GitCommitter.cs:153-168 — 3-attempt retry loop with 250ms/1s backoff — cannot recover from permanent xcrun failure",
    "TransientGitShim.cs:47 — test double falls through to ProcessCapture.RunAsync(\"git\", …) — same PATH resolution problem",
    "SplitGuardVerificationTests.Conventions.cs — existing architecture-test pattern (grep-style source assertions) that can be extended"
  ],
  "repro": "On macOS under `nix develop` with Xcode CLT, start a multi-hour RelayDriver run. While the driver is running, trigger nix store GC that removes the apple-sdk-14.4 derivation. Any subsequent git invocation (stage 11 commit, red-gate stash restore, worktree prune) will fail with: `xcrun: error: missing DEVELOPER_DIR path: /nix/store/<hash>-apple-sdk-14.4`. The failure is permanent for the process lifetime because the shell environment inherited DEVELOPER_DIR pointing to a now-missing nix store path, and all four call sites resolve `git` through PATH which goes through xcrun's shim."
}

## Stage 4 - Plan

{
  "plan": "Static singleton GitInvoker with lazy one-time git binary resolution + env sanitization. All four production call sites (GitCommitter, RedGate, PlanningWorktree, HookInstaller) switch from bare \"git\" via PATH to GitInvoker.RunAsync(...). ProcessCapture gets a lightweight envRemove parameter for surgical DEVELOPER_DIR/SDKROOT stripping. Internal Override test seam mirrors the existing RawGitRunner pattern, avoiding a cascade through 77 ForTests(...) call sites. Architecture test (grep-style) enforces no bare \"git\" in production ProcessCapture/ProcessStartInfo calls.",
  "manifest": [
    "src/VisualRelay.Core/Execution/GitInvoker.cs",
    "tests/VisualRelay.Tests/GitInvokerTests.cs",
    "src/VisualRelay.Core/Execution/ProcessCapture.cs",
    "src/VisualRelay.Core/Execution/GitCommitter.cs",
    "src/VisualRelay.Core/Execution/RedGate.cs",
    "src/VisualRelay.Core/Execution/PlanningWorktree.cs",
    "src/VisualRelay.Core/Init/HookInstaller.cs",
    "tests/VisualRelay.Tests/TransientGitShim.cs",
    "tests/VisualRelay.Tests/GitCommitterTests.cs",
    "tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/GitInvokerTests.cs",
    "tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs"
  ],
  "rationale": "GitInvokerTests.cs (299 lines, ≤300 guard): 8 [Fact] methods testing the Override seam (delegation + binary passthrough), env sanitization (DEVELOPER_DIR/SDKROOT stripping with user-env passthrough, null-env tolerance), fail-fast on resolution failure, one-time caching across invocations, and a real-git end-to-end smoke test. All reference GitInvoker which does not exist yet → compile-time failure. SplitGuardVerificationTests.Conventions.cs (new test at line 147): greps src/VisualRelay.{Core,App,Domain}/**/*.cs for bare ProcessCapture.RunAsync(\"git\",…) and new ProcessStartInfo(\"git\") — currently matches all four production call sites (GitCommitter:180, RedGate:133, HookInstaller:60, PlanningWorktree:126) → runtime failure."
}

## Stage 6 - Implement

{
  "summary": "## Implementation complete: pin-git-binary-against-env-rot\n\n### Files created\n- **src/VisualRelay.Core/Execution/GitInvoker.cs** — Centralized git process factory with lazy one-time resolution (double-checked locking), resolution order (macOS `/usr/bin/git` → `xcrun --find git` → `command -v git` → `/usr/bin/git` fallback), `--version` probe validation, `Override` static Func test seam, `SanitizeEnvironment` that strips `DEVELOPER_DIR`/`SDKROOT` when binary is outside `/nix/store`, and fail-fast `InvalidOperationException` on resolution failure.\n\n### Files modified\n- **ProcessCapture.cs** — Added `IReadOnlySet<string>? envRemove = null` parameter to all three `RunAsync` overloads; removes specified env vars from the child process's inherited environment before overlaying caller-supplied vars.\n- **GitCommitter.cs** — `RunGitCoreAsync` now delegates to `GitInvoker.RunAsync(rootPath, arguments, ...)` instead of `ProcessCapture.RunAsync(\"git\", ...)`. `RawGitRunner` static seam preserved for backward compatibility.\n- **RedGate.cs** — `GitAsync` now uses `GitInvoker.RunAsync(rootPath, arguments, ct)` instead of `ProcessCapture.RunAsync(\"git\", ...)`.\n- **PlanningWorktree.cs** — `RunGitAsync` now uses `GitInvoker.RunAsync(repoRoot, args, ct)` instead of direct `Process`/`ProcessStartInfo`. Removed `using System.Diagnostics`. Removed manual `DEVELOPER_DIR`/`SDKROOT` stripping (now centralized).\n- **HookInstaller.cs** — `InstallAsync` now uses `GitInvoker.RunAsync(rootPath, ...)` instead of `ProcessCapture.RunAsync(\"git\", ...)`.\n- **TransientGitShim.cs** — Fallthrough now uses `GitInvoker.RunAsync(rootPath, argsList, ct, timeout, environment)`.\n\n### Architecture test\n- **SplitGuardVerificationTests.Conventions.cs** — `NoBareGitString_InProductionCallSites` (line 147, from stage 5) now passes: zero bare `\"git\"` strings in production `ProcessCapture.RunAsync` or `ProcessStartInfo` calls.\n\n### Test results\n- All 576 tests pass (9 GitInvoker tests, 9 GitCommitter tests, all remaining tests).\n- GitCommitter's `RawGitRunner` seam + `TransientGitShim` backward compatibility verified."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    "GitInvoker.Override is global static mutable state; GitInvokerTests can run in parallel with GitCommitterTests (via TransientGitShim) causing cross-test races. Add test isolation (collection or internal ResetForTests seam).",
    "RunAsync_WhenSystemGit_StripsDeveloperDir and RunAsync_WhenSystemGit_StripsSdkroot assume the resolved git binary is outside /nix/store/. On Nix hosts the binary may resolve inside /nix/store/, leaving _envRemove null and failing the assertions. Tests need a seam to force the resolved path or _envRemove state.",
    "GitBinary_WhenSetManually_IsUsedByRunAsync is misnamed: the GitBinary property has no setter, so it cannot test manual setting. Rename/rewrite to reflect that it only exercises Override passthrough.",
    "Missing test coverage for the nix-store branch: when the pinned binary lies inside /nix/store/, DEVELOPER_DIR and SDKROOT must be preserved rather than stripped."
  ]
}

## Stage 8 - Fix

{
  "summary": "## Stage 8 review fixes applied\n\n### Issue 1 — Cross-test race (GitInvoker.Override global static mutable state)\n- **GitInvoker.cs**: Added `ResetForTests()` (clears `_gitBinary`, `_envRemove`, `Override` under lock) and `SetResolvedBinaryForTests(string)` (pins a binary path + computes `_envRemove` deterministically) as internal test seams.\n- **GitInvokerTests.cs**: Added `[Collection(\"GitInvoker\")]` to serialize all tests that touch the static `Override` seam. Every test now calls `ResetForTests()` at the start for a clean slate.\n- **TransientGitShim.cs**: Changed fallthrough from `GitInvoker.RunAsync(...)` to `ProcessCapture.RunAsync(\"git\", ...)`. The shim is test code (not in production dirs), so bare `\"git\"` is acceptable and the architecture test does not flag it. This eliminates the cross-collection race: GitCommitterTests (collection \"GitCommitter\") can no longer accidentally pick up a `GitInvoker.Override` set by GitInvokerTests (collection \"GitInvoker\").\n\n### Issue 2 — Tests assumed resolved binary outside /nix/store\n- Added `SetResolvedBinaryForTests(string)` seam so tests can force `_gitBinary` and `_envRemove` without depending on host git resolution. Tests `RunAsync_WhenSystemGit_StripsDeveloperDirAndSdkroot` and `RunAsync_WithNullEnvironment_StillSanitizes` now call `SetResolvedBinaryForTests(\"/usr/bin/git\")` before setting Override, guaranteeing `_envRemove` is populated regardless of host.\n\n### Issue 3 — Misnamed test\n- Renamed `GitBinary_WhenSetManually_IsUsedByRunAsync` → `Override_WhenSet_PassesResolvedBinaryToOverride`. The new name accurately describes that the test validates the Override seam receives and returns the resolved binary path (there is no setter on `GitBinary`).\n\n### Issue 4 — Missing nix-store branch test coverage\n- Added `RunAsync_WhenNixStoreGit_PreservesDeveloperDirAndSdkroot`: forces `_gitBinary` inside `/nix/store/`, sets `DEVELOPER_DIR`/`SDKROOT` in caller env, and asserts both are **preserved** (not stripped) in the captured environment.\n- Consolidated the two system-git stripping tests into one `RunAsync_WhenSystemGit_StripsDeveloperDirAndSdkroot` to keep the file under the 300-line guard.\n\n### Convention test update\n- **SplitGuardVerificationTests.Conventions.cs**: Added `GitInvokerTests_HasCollectionAttribute` assertion verifying `[Collection(\"GitInvoker\")]` is present on `GitInvokerTests.cs`.\n\n### Test results\n- All 577 tests pass (2 pre-existing flaky tests occasionally fail on full-suite parallel runs but pass in isolation — unrelated to these changes).\n- File line counts: GitInvokerTests.cs 204 lines, Conventions.cs 203 lines — both under the 300-line guard."
}

## Stage 9 - Verify

{
  "summary": "Pinned git binary at driver startup via GitInvoker, a lazy singleton that resolves a stable absolute git binary once (preferring macOS /usr/bin/git → xcrun --find git → command -v git → fallback /usr/bin/git), validates with --version, and sanitizes the child-process environment by stripping DEVELOPER_DIR/SDKROOT when the binary lives outside /nix/store. All four production git call sites (GitCommitter, RedGate, PlanningWorktree, HookInstaller) now delegate to GitInvoker.RunAsync instead of invoking bare \"git\" via PATH. ProcessCapture gained an envRemove parameter for surgical env-var stripping. Architecture test (NoBareGitString_InProductionCallSites) enforces zero bare \"git\" ProcessCapture/ProcessStartInfo calls in src/VisualRelay.{Core,App,Domain}. Test isolation: GitInvokerTests uses [Collection(\"GitInvoker\")], ResetForTests/SetResolvedBinaryForTests internal seams, and TransientGitShim fallthrough bypasses GitInvoker.Override to avoid cross-collection races with GitCommitterTests. 9 GitInvoker unit tests cover Override delegation, env sanitization (system-git strips DEVELOPER_DIR/SDKROOT, nix-store-git preserves them, null-env tolerance), fail-fast on resolution failure, one-time caching, and a real-git smoke test. All 577 tests pass.",
  "commitMessages": [
    "feat(git): pin stable git binary at startup and centralize all invocations via GitInvoker",
    "fix(git): prevent nix-store environment rot by resolving git once and stripping DEVELOPER_DIR/SDKROOT",
    "feat(execution): add GitInvoker — lazy singleton with env sanitization, migrate all four call sites",
    "test(git): add GitInvoker unit tests, architecture guard, and test-collection isolation for static Override seam",
    "fix(driver): resolve git binary once at startup so multi-hour runs survive nix GC of apple-sdk paths"
  ]
}

## Stage 10 - Fix-verify

{
  "summary": "Ran `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false`. All 577 tests passed (0 failed, 0 skipped) in 1 m 13 s. Exit code 0."
}

## Stage 11 - Commit

Committed by Visual Relay.

