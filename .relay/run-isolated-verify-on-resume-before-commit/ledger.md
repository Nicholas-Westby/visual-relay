## Stage 1 - Ideate

{
  "summary": "Add an isolated, sandboxed verification step triggered on resume (e.g., from a saved checkpoint or suspended session) that runs before any commit operation, blocking the commit if the check fails.",
  "options": [
    "1. **Git pre-commit hook with containerized verify script** — Install a `pre-commit` hook (`.git/hooks/pre-commit`) that launches a Docker/Podman container running the verification script against a clean checkout. The hook exits non-zero (blocking the commit) if verification fails. Isolation via ephemeral container; no changes leak.",
    "2. **Process-level wrapper with exec sandbox** — Wrap the resume+commit flow in a thin supervisor that forks a child process in a Linux `user` + `mount` namespace (CLONE_NEWUSER | CLONE_NEWNS). The child runs verification; the parent waits for its exit code. If verification fails, the supervisor refuses to execute the commit binary. Isolation via kernel namespaces (no container runtime needed).",
    "3. **Sidecar daemon with attestation check** — A long-running sidecar daemon watches for resume events (e.g., inotify on a checkpoint file, or a D-Bus sleep/resume signal). On resume, it runs verification in a gVisor/bubblewrap sandbox and writes an attestation token. The commit tool rejects the commit unless a fresh (≤ N seconds old) token exists. Isolation via gVisor/bubblewrap sandbox."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is a .NET/C# application (VisualRelay) that implements an AI-driven software development relay system. The feature requested is **already substantially implemented**:\n\n1. **Resume flow**: `RelayDriver.Resume.cs` — When `RelayDriverOptions.Resume = true`, `LoadResumeStateAsync()` loads prior task state and determines where to continue. If the only remaining stage is 12 (Commit), it calls `ValidateCommitGateResumeAsync()`.\n\n2. **Existing verification on resume before commit**: `RelayDriver.CommitGate.cs` (lines 25-85) — `ValidateCommitGateResumeAsync()` does exactly what the task describes: on resume to the commit stage, it re-runs the test suite (`_dependencies.TestRunner.RunAsync()`) to verify the gate still passes, AND recomputes the worktree hash to compare against the recorded stage-11 seal `treeHash`. If either check fails, the commit is blocked (falls back to restarting from stage 5).\n\n3. **Sandbox isolation already active**: All test commands run through `SandboxedTestRunner` which wraps them in `nono run -p vr-guard --allow-cwd --` (the Nono sandbox on macOS/Linux). There is no opt-out; sandboxing is always on. The sandbox is maintained via `NonoProfileEnsurer.EnsureAsync()`.\n\n4. **Worktree hash check is NOT sandboxed**: `WorkingTreeHash()` (in `RelayDriver.Artifacts.cs` lines 135-146) reads files directly from disk — this is a pure filesystem fingerprint, not a sandboxed operation, but it doesn't run untrusted code.\n\n5. **Pre-commit hook**: `.githooks/pre-commit` — Enforces commit authority during active runs via `RELAY_COMMIT_TOKEN` check. This is a separate guard from the resume verification.\n\n6. **No fresh-run gate check**: On a fresh (non-resume) run, there is no separate verify-on-commit gate — verification is baked into stages 10-11 (Verify/Fix-verify) as part of the pipeline.\n\n7. **Three ideation options remain un-implemented as alternatives**: (a) containerized pre-commit hook, (b) process-level namespace supervisor, (c) sidecar daemon with attestation tokens. The current implementation uses the existing Nono sandbox via `SandboxedTestRunner`, which is lighter-weight than containers but heavier than a namespace-only fork.\n\n8. **Test coverage**: Tests exist in `RelayDriverResumeCommitGateTests.cs` covering the gate validation scenarios during resume.",
  "constraints": [
    "The Nono sandbox (nono-cli) must be installed and available — it's the existing isolation mechanism; no Docker/container runtime is required or configured",
    "The resume verification runs INSIDE the RelayDriver process — it cannot outlive the driver or run as a separate daemon (eliminates sidecar option without architecture change)",
    "The worktree hash check (WorkingTreeHash) reads manifest-listed files directly from disk with no isolation — the files are the same workspace the commit targets",
    "Commit stage 12 (GitCommitter.CommitAsync) runs `git commit` directly (not sandboxed) — the commit itself has no isolation wrapper",
    "The pre-commit hook (.githooks/pre-commit) relies on a RELAY_COMMIT_TOKEN environment variable set by the driver — any verification there requires the same token mechanism",
    "All three ideation options (container, namespace fork, sidecar) would need new infrastructure or configuration since none is currently present in the repo",
    "On resume test failure, the driver falls back to stage 5 (re-running author tests + re-verifying) — this is expensive and cannot be short-circuited to a hard-block without changing fallback behavior",
    "The sandbox profile (vr-guard) is pinned per-run via ResolvePinnedSwivalProfileContentAsync — any new verification step would need its own profile or reuse the existing one",
    "Command-guard middleware must be published before any sandboxed stage runs — this is already handled via CommandGuardEnsurer.EnsureAsync() at the start of RunTaskAsync",
    "The codebase uses C# 12 / .NET 9 with Nerdbank.GitVersioning — any new files or build targets must follow the existing `.csproj` conventions"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The task is 'run-isolated-verify-on-resume-before-commit'. The codebase already has a commit-gate verification on resume (`ValidateCommitGateResumeAsync` in `RelayDriver.CommitGate.cs` lines 28-111) that re-runs the test suite and checks the worktree hash before allowing commit on resume — BUT the test re-run IS NOT ISOLATED: it runs in-place on the real repository via `_dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, ...)` (line 42-43). The codebase already has a fully built isolated-verify mechanism (`RunIsolatedVerifyAsync` in `RelayDriver.VerifyWorktree.cs` lines 18-55) that creates a disposable git-worktree snapshot, overlays uncommitted changes, runs the test command there, and then tears it down — leaving the real repo untouched. Both the stage-10 pre-agent verify (line 59 of `RelayDriver.Stage9.cs`) and the stage-11 fix-verify loop (line 164 of `RelayDriver.VerifyFix.cs`) use this isolated mechanism. The gap is explicitly acknowledged in a code comment at `RelayDriver.VerifyWorktree.cs` lines 238-240: 'isolation covers the authoritative test gates (stages 9/10) only — the bootstrap check and commit gate still run in-place.' The commit gate on resume needs to be switched from the in-place `TestRunner.RunAsync()` call to the existing `RunIsolatedVerifyAsync()` method so the verification is truly isolated from the worktree before committing.",
  "excerpts": [
    "RelayDriver.CommitGate.cs:42-43 — `var testResult = await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, cancellationToken);` — runs in-place, no worktree isolation",
    "RelayDriver.VerifyWorktree.cs:238-240 — `// NOTE: isolation covers the authoritative test gates (stages 9/10) only — the bootstrap check and commit gate still run in-place` — explicit comment acknowledging the gap",
    "RelayDriver.VerifyWorktree.cs:18-55 — `RunIsolatedVerifyAsync` creates isolated worktree via `CreateVerifyWorktreeAsync`, overlays uncommitted state, runs test there, cleans up — the existing isolation mechanism",
    "RelayDriver.Stage9.cs:59 — `var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(...)` — stage 10 uses isolated verify",
    "RelayDriver.VerifyFix.cs:164 — `var (testResult, verifyMutations) = await RunIsolatedVerifyAsync(...)` — stage 11 uses isolated verify",
    "RelayDriver.VerifyWorktree.cs:82-164 — `CreateVerifyWorktreeAsync` builds a full-fidelity isolated snapshot: detached HEAD worktree + overlay of every uncommitted change + git-ignored runtime content (small entries copied, large symlinked), supporting all major toolchains"
  ],
  "repro": "1. Create a task that reaches stage 12 (Commit) and flags (e.g., by using a non-git repo). 2. Resume the task — `ValidateCommitGateResumeAsync` fires because `firstStageToRun == 12`. 3. The test command runs in-place via `_dependencies.TestRunner.RunAsync(rootPath, ...)` — not via `RunIsolatedVerifyAsync`. 4. Any side-effect writes from the test command (e.g., a test that generates files, touches build artifacts, or mutates the working tree) pollute the real repository. 5. If the gate passes, those polluted files get committed. The stage-10 and stage-11 verify gates do NOT have this problem because they run in an isolated worktree that is discarded after the test."
}

## Stage 4 - Plan

{
  "plan": "## Summary\n\nReplace the in-place test suite re-run inside `ValidateCommitGateResumeAsync` with a call to the existing `RunIsolatedVerifyAsync`, so the commit-gate resume re-verification runs in an isolated git worktree snapshot — matching the isolation already used by stages 10 and 11. Update the stale code comment that acknowledged this gap.\n\n## Changes\n\n### 1. `src/VisualRelay.Core/Execution/RelayDriver.cs` (line 56)\n\nAdd `runId` and `taskId` arguments to the `ValidateCommitGateResumeAsync` call. Both variables are already in scope at the call site (line 25–26).\n\n### 2. `src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs`\n\n- **Signature**: Add `string runId, string taskId` parameters before `CancellationToken`.\n- **Body (lines 38–49)**: Replace the `_dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, ...)` call with:\n  ```csharp\n  var (testResult, _) = await RunIsolatedVerifyAsync(\n      rootPath, config, stageNumber: 12, attempt: 1, runId, taskId, cancellationToken);\n  ```\n  `RunIsolatedVerifyAsync` handles the non-git-repo fallback internally (returns in-place result), so the existing `catch` block still covers all failures.\n\n### 3. `src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs` (lines 238–240)\n\nUpdate the comment that says \"the bootstrap check and commit gate still run in-place\" to reflect that the commit gate now also uses isolation.\n\n## Test impact\n\nBoth existing tests in `RelayDriverResumeCommitGateTests.cs` should pass unchanged:\n\n- **`RunTaskAsync_Resume_CommitGateWithMatchingHash_SkipsToCommit`**: Uses an unregistered `GitSimEngine` → `CreateVerifyWorktreeAsync` throws → `RunIsolatedVerifyAsync` falls back to in-place `RunTestCommandWithRetryAsync` → calls `RecordingTestRunner` → assertion on `Calls` still holds.\n- **`RunTaskAsync_Resume_CommitGateWithHashMismatch_RestartsFromStage5`**: Uses a registered `GitSim` → worktree creation succeeds → `ScriptedTestRunner` consumes result 1 (green) in the worktree → hash mismatch triggers stage-5 restart → remaining scripted results consumed as before.",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayDriver.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs"
  ]
}

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/RelayDriverResumeCommitGateVerifyTests.cs"], "rationale": "Two tests added to RelayDriverResumeCommitGateVerifyTests.cs: (1) RunTaskAsync_Resume_CommitGateWithFailingTest_ReflagsWithVerifyResult — a resumed flagged task whose test command fails re-flags (outcome Flagged) and the run log must contain a verify_result event with check:red; no stage 12 stage_done is emitted. (2) RunTaskAsync_Resume_CommitGateWithPassingTest_CommitsWithVerifyResult — a resumed flagged task whose test command passes commits (outcome Committed) and the run log must contain a verify_result event with check:green that appears BEFORE the stage 12 stage_done event. Both tests compile and fail at the verify_result assertion since the current ValidateCommitGateResumeAsync runs the test in-place without publishing verify_result events — exactly the gap the implementation must fill." }

## Stage 6 - Implement

{ "summary": "Replaced the in-place test runner call in ValidateCommitGateResumeAsync with RunIsolatedVerifyAsync + PublishVerifyResultAsync (matching stages 10/11). The method now returns a nullable RelayTaskOutcome to flag immediately on verify failure. Updated the RelayDriver.cs call site to pass runId/taskId and handle the early-return outcome. Updated the stale comment in VerifyWorktree.cs. Fixed the new test that needed 2 failing results (retry path). All 2956 tests pass, 0 failed." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Pure backend C# logic changes — control flow, method calls, and unit test assertions in an execution driver. No UI, markup, layout, images, charts, documents, or any other visual output is involved or affected._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "All 2956 tests pass (0 failed, 95 skipped). The resume-to-commit path in `ValidateCommitGateResumeAsync` (`RelayDriver.CommitGate.cs`) was switched from an in-place `TestRunner.RunAsync()` call to the authoritative isolated-worktree `RunIsolatedVerifyAsync()`, matching the isolation already used by pipeline stages 10/11. The method now emits a `verify_result` event + artifact (via `PublishVerifyResultAsync`) and returns a nullable `RelayTaskOutcome` to flag immediately on red. The call site in `RelayDriver.cs` (line 56) passes `runId`/`taskId` and handles the early-flag return. The stale comment in `RelayDriver.VerifyWorktree.cs` (lines 238-241) was updated to reflect that the commit gate now also runs isolated. Two new tests in `RelayDriverResumeCommitGateVerifyTests.cs` verify: (1) a failing test on resume re-flags with `verify_result check:red` and no stage-12 `stage_done`; (2) a passing test on resume commits with `verify_result check:green` appearing before the commit event. Regression tests unchanged. No other path to stage-12 commit bypasses the gate: `skipTestsTaskIds` is the only exception and remains untouched.",
  "commitMessages": [
    "fix: run isolated verify gate on resume-to-commit path instead of in-place test",
    "fix(resume): gate stage 12 behind RunIsolatedVerifyAsync with verify_result events",
    "fix: plug resume commit-gate isolation gap — reuse authoritative worktree verify"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

