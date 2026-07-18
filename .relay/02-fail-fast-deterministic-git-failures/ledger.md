## Stage 1 - Ideate

{
  "summary": "Add a message-signature-based GitFailureClassifier to fail fast on deterministic git errors (exit 128 with 'not a git repository' or 'invalid reference') instead of retrying 3x with backoff. Thread TimeProvider through RelayDriverDependencies so tests can prove zero sleep. Add FakeClockGuard to prevent production defaults to fake clocks. Two alternatives: (B) pre-flight probe before the loop, which adds extra git invocations and misses 'invalid reference'; (C) caller-supplied predicate, which is over-engineered for two stable signatures.",
  "options": [
    "A (prescribed): Static GitFailureClassifier.IsDeterministic + early-exit branches in both retry loops + TimeProvider plumbing + FakeClockGuard",
    "B: Pre-flight git-repo probe before the retry loop, keeping the loop untouched, but misses 'invalid reference' and adds a git invocation to every happy path",
    "C: Caller-supplied Func<int,string,bool>? predicate — flexible but over-engineered for two fixed signatures; risks callers forgetting to pass it"
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase has two retry loops (PlanningWorktree.RunGitAsync at src/VisualRelay.Core/Execution/PlanningWorktree.cs:200-228 and GitCommitter.GitAsync at src/VisualRelay.Core/Execution/GitCommitter.cs:240-267) that retry ANY nonzero exit 3x with 250ms/1s backoff. Both already accept `TimeProvider? timeProvider = null` but their callers (4 sites in RelayDriver.VerifyWorktree.cs:88, VerifyWorktreeCleanup.cs:13, CommitGate.cs:231, CommitGate.cs:244) omit the argument, so real-time sleep always fires. NullGitInvoker returns (128, \"fatal: not a git repository...\") for every call; GitSim's Fatal helper returns (128, \"fatal: {message}\\n\") — identical exit 128 pattern. RelayDriverDependencies is a positional record with 5 fields, no TimeProvider yet; ForTests constructs with NullGitInvoker. ManualTimeProvider exists at tests/VisualRelay.Tests/ManualTimeProvider.cs. GitCommitterResilienceTests uses TransientGitShim decorator pattern. Audit tool (task 01) already reports retry-delay-loops at both sites — PlanningWorktree as \"(no classifier)\" and GitCommitter as \"(classifier present)\" (exit-code-only classification). Two stale comments reference the ~2.5s retry cost: RewriteMutualExclusionTests.cs:29 and ControlApiConfirmGatedTests.cs:28. Guard matchers follow static-class pattern with nested Violation record, two FindViolations overloads (string-source pairs + pre-parsed SyntaxTrees), under 300 lines, self-exempt by filename. Guard tests use CachedSyntaxTreesFixture with inline source snippets.",
  "constraints": [
    "Do NOT change attempt counts (3) or backoff timing (250ms, 1s) for transient failures.",
    "Do NOT widen the deterministic set beyond the two signatures: 'not a git repository' and 'invalid reference'.",
    "Do NOT default ForTests (or anything in src/) to a fake clock — test must pass ManualTimeProvider explicitly.",
    "Do NOT auto-advance time anywhere — tests that want virtual time pass ManualTimeProvider explicitly.",
    "FakeClockGuard must enforce: no ManualTimeProvider or FakeTimeProvider identifier in src/ or tools/ trees; every TimeProvider-typed parameter default is null (bodies resolve via ?? TimeProvider.System); no src csproj references a time-testing package.",
    "PlanningWorktree.RunGitAsync throws InvalidOperationException on deterministic failure (no delay, no further attempts). Exception-path retry (process start failure) remains untouched.",
    "GitCommitter.GitAsync returns the failed result immediately on deterministic failure (its contract returns results rather than throwing). Keep the isSuccessExit widening used by check-ignore probe.",
    "The ForTests method keeps null default for TimeProvider — real time.",
    "All new C# files must be under 300 lines (file-size guard).",
    "Guard matchers must follow the existing static-class + Violation record + two FindViolations overloads pattern.",
    "RetryDelayLoopsGuard audit must show both loops consulting the classifier after implementation.",
    "Commit-message evidence must measure: full-suite wall clock before vs after, isolated normal-rerun test duration, and slowest driver test in TRX before vs after."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Two retry loops — PlanningWorktree.RunGitAsync (lines 208–242) and GitCommitter.GitAsync (lines 224–258) — retry ANY nonzero git exit 3× with real-time backoff (250ms, then 1s). Both already accept a TimeProvider parameter and use Task.Delay(delay, timeProvider, ct), but neither checks whether the failure is deterministic. NullGitInvoker returns (128, \"fatal: not a git repository (or any of the parent directories): .git\") for every call; GitSim's Fatal helper returns (128, \"fatal: {message}\\n\") — identical exit-128 signature. Real git also emits exit 128 with the same message. Exit 128 with \"not a git repository\" can never succeed on retry, yet the retry loops sleep a full 1.25s before failing.\n\nThree driver paths pay this cost per hit: (1) stage-10 pre-agent gate via RunIsolatedVerifyAsync → CreateVerifyWorktreeAsync → PlanningWorktree.CreateAsync (RelayDriver.Stage9.cs:59, RelayDriver.VerifyWorktree.cs:88), (2) every verify-fix attempt via RunIsolatedVerifyAsync (RelayDriver.VerifyFix.cs), and (3) commit-gate resume revalidation via GitCommitter.CommitAsync and FindUncommittedAuthoredFilesAsync (RelayDriver.CommitGate.cs:231, 244).\n\nAll four call sites omit the existing timeProvider parameter, so real-time sleep always fires even though the TimeProvider seam already exists in both CreateAsync/RemoveAsync and GitAsync. RelayDriverDependencies (a positional record with 5 fields) has no TimeProvider field to thread through.\n\nThe RetryDelayLoopsGuard audit already detects both loops — PlanningWorktree reported as \"(no classifier)\" and GitCommitter as \"(classifier present)\" (from its isSuccessExit widening). Two stale comments document the burn: RewriteMutualExclusionTests.cs:29 and ControlApiConfirmGatedTests.cs:28 both reference \"~2.5s\" or \"250ms + 1s\" retry cost.\n\nNo FakeClockGuard exists yet; no src csproj references a time-testing package; ManualTimeProvider exists only in tests/ (ManualTimeProvider.cs).",
  "excerpts": [
    "PlanningWorktree.RunGitAsync (src/VisualRelay.Core/Execution/PlanningWorktree.cs:208-242):\n  for (int attempt = 1; attempt <= maxAttempts; attempt++) {\n    var result = await gitInvoker.RunAsync(repoRoot, args, ct);\n    if (result.ExitCode == 0) return;\n    if (attempt == maxAttempts) throw new InvalidOperationException(...);\n    var delay = attempt == 1 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);\n    await Task.Delay(delay, timeProvider, ct);  // <-- no deterministic check, sleeps on exit-128\n  }",
    "GitCommitter.GitAsync (src/VisualRelay.Core/Execution/GitCommitter.cs:224-258):\n  for (int attempt = 1; attempt <= maxAttempts; attempt++) {\n    var result = await gitInvoker.RunAsync(rootPath, args, cancellationToken, timeout, environment);\n    if (isSuccess(result.ExitCode) || attempt == maxAttempts) return result;\n    lastResult = result;\n    var delay = attempt == 1 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(1);\n    await Task.Delay(delay, tp, cancellationToken);  // <-- no deterministic check, sleeps on exit-128\n  }",
    "NullGitInvoker (src/VisualRelay.Core/Execution/NullGitInvoker.cs:21):\n  return Task.FromResult((128, \"fatal: not a git repository (or any of the parent directories): .git\", false));",
    "GitSimResult.Fatal (tests/VisualRelay.GitSim/GitSimContext.cs:12):\n  public static GitSimResult Fatal(string message) => new(128, $\"fatal: {message}\\n\");",
    "RelayDriverDependencies (src/VisualRelay.Core/Execution/RelayDriverDependencies.cs:15-21):\n  public sealed record RelayDriverDependencies(\n    ISubagentRunner SubagentRunner, ITestRunner TestRunner,\n    IRelayEventSink EventSink, IGitInvoker GitInvoker,\n    IEnvironmentAccessor? EnvironmentAccessor = null)\n  // No TimeProvider field — cannot thread clock to retry loops.",
    "Call site 1 — CreateVerifyWorktreeAsync (RelayDriver.VerifyWorktree.cs:88):\n  var worktreePath = await PlanningWorktree.CreateAsync(\n    sourcePath, worktreeId, runId, cancellationToken, _dependencies.GitInvoker);\n  // timeProvider omitted → defaults to TimeProvider.System, real sleep fires",
    "Call site 2 — VerifyWorktreeCleanup (RelayDriver.VerifyWorktreeCleanup.cs:13):\n  await PlanningWorktree.RemoveAsync(sourcePath, worktreePath, cancellationToken, _dependencies.GitInvoker);",
    "Call site 3 — CommitGate commit (RelayDriver.CommitGate.cs:231):\n  var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, chain, manifest, proofFiles,\n    activeLockNonce, preRunUntracked, config.TasksDir, cancellationToken, _dependencies.GitInvoker, runBaseSha);",
    "Call site 4 — CommitGate missed-files check (RelayDriver.CommitGate.cs:244):\n  var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(\n    rootPath, preRunUntracked, config.TasksDir, cancellationToken, _dependencies.GitInvoker);",
    "Stale comment 1 (RewriteMutualExclusionTests.cs:29):\n  // PlanningWorktree's 3x retry (250ms + 1s) burns ~2.5s per rewriting fact.",
    "Stale comment 2 (ControlApiConfirmGatedTests.cs:28):\n  // without it PlanningWorktree's 3x retry over failing `git worktree` against the non-repo test root burns ~2.5s per fact.",
    "RetryDelayLoopsGuard.HasClassifier (RetryDelayLoopsGuard.cs:237-258): looks for isSuccess, Classifier, shouldRetry, isTransient identifiers. PlanningWorktree currently shows (no classifier); GitCommitter shows (classifier present) from isSuccessExit. Adding GitFailureClassifier.IsDeterministic will make both show as classified."
  ],
  "repro": "// Any test that creates a PlanningWorktree or GitCommitter against a non-git root\n// with NullGitInvoker burns 1.25s in sleep before failing:\n\nvar tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());\nDirectory.CreateDirectory(tempDir);\ntry\n{\n    var sw = Stopwatch.StartNew();\n    try\n    {\n        await PlanningWorktree.CreateAsync(tempDir, \"task\", \"run\", CancellationToken.None,\n            new NullGitInvoker());\n    }\n    catch (InvalidOperationException) { }\n    sw.Stop();\n    // sw.Elapsed >= ~1.25s (250ms + 1s sleep) — the retry loop burned real time\n    // on an exit-128 \"not a git repository\" that can never succeed.\n}\nfinally { Directory.Delete(tempDir, recursive: true); }\n\n// Same pattern for GitCommitter.CommitAsync against a non-git root:\n// the inner GitAsync([\"rev-parse\", \"--is-inside-work-tree\"]) sleeps 1.25s\n// before returning the non-zero exit.\n\n// Verified across all three git backends: real GitInvoker (exit 128),\n// NullGitInvoker (hard-coded exit 128), and GitSim (Fatal helper → exit 128)."
}

## Stage 4 - Plan

{
  "plan": "Add GitFailureClassifier.IsDeterministic (exit≠0 + message contains 'not a git repository' or 'invalid reference') and insert early-exit branches in both retry loops (PlanningWorktree.RunGitAsync throws immediately; GitCommitter.GitAsync returns failed result immediately). Thread TimeProvider through RelayDriverDependencies as a nullable last positional (default null → real time) and pass it at 4 call sites that omit it. Add FakeClockGuard (tools/VisualRelay.Guards) enforcing no fake-clock identifiers in src/tools, all TimeProvider parameter defaults are null, and no src csproj references a time-testing package. Unit-test the classifier, fail-fast behavior (unregistered GitSim + ManualTimeProvider → task already faulted), deterministic GitCommitter failure (1 invocation, no time advance), guard rules. Fix existing resilience tests to use transient (index.lock) messages so they still see 3 attempts. Update two stale comments referencing the ~2.5s retry burn.",
  "manifest": [
    "+src/VisualRelay.Core/Execution/GitFailureClassifier.cs",
    "+tools/VisualRelay.Guards/FakeClockGuard.cs",
    "+tests/VisualRelay.Tests/FakeClockGuardTests.cs",
    "+tests/VisualRelay.Tests/GitFailureClassifierTests.cs",
    "+tests/VisualRelay.Tests/PlanningWorktreeDeterministicFailureTests.cs",
    "src/VisualRelay.Core/Execution/PlanningWorktree.cs",
    "src/VisualRelay.Core/Execution/GitCommitter.cs",
    "src/VisualRelay.Core/Execution/RelayDriverDependencies.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktreeCleanup.cs",
    "src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs",
    "tests/VisualRelay.Tests/GitCommitterResilienceTests.cs",
    "tests/VisualRelay.Tests/RewriteMutualExclusionTests.cs",
    "tests/VisualRelay.Tests/ControlApiConfirmGatedTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 10, untracked deleted: 3.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/GitFailureClassifierTests.cs",
    "tests/VisualRelay.Tests/PlanningWorktreeDeterministicFailureTests.cs",
    "tests/VisualRelay.Tests/FakeClockGuardTests.cs"
  ],
  "rationale": "Three new test files: (1) GitFailureClassifierTests — 5 unit tests proving both deterministic signatures are recognized, exit 0 never is, and index.lock/unknown fatals stay retryable. (2) PlanningWorktreeDeterministicFailureTests — single test that creates a PlanningWorktree against an unregistered GitSim with ManualTimeProvider, asserts the task is already faulted (deterministic failure throws immediately, no sleep). (3) FakeClockGuardTests — 6 tests: 4 inline-snippet tests for fake-clock identifiers and TimeProvider defaults, 1 LiveTree gate proving no production source violates the rules, all consuming CachedSyntaxTreesFixture. The GitCommitterResilienceTests were updated to use transient (index.lock) messages for retry tests and to expect fail-fast behavior for the deterministic persistent-failure test."
}

## Stage 6 - Implement

{
  "summary": "Added GitFailureClassifier.IsDeterministic (exit≠0 + message contains 'not a git repository' or 'invalid reference'), inserted early-exit branches in PlanningWorktree.RunGitAsync (throws immediately) and GitCommitter.GitAsync (returns failed result immediately). Threaded TimeProvider through RelayDriverDependencies as nullable last positional (default null) and passed it at 4 call sites. Created FakeClockGuard enforcing no fake-clock identifiers in src/tools. Updated GitCommitterResilienceTests: transient-retry tests use index.lock messages, deterministic tests assert fail-fast with exactly 1 invocation. Updated stale comments in RewriteMutualExclusionTests and ControlApiConfirmGatedTests. Targeted tests: 25/25 passed. Full suite: 3240 passed, 0 failed, 114 skipped."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": ["ForTests (src/VisualRelay.Core/Execution/RelayDriverDependencies.cs:23-30) is missing the prescribed pass-through TimeProvider parameter — step 4 required 'and a pass-through parameter on ForTests', but it still constructs the record with 5 positional args relying on the null default, leaving no way to inject a fake clock through the normal test constructor. The headline goal of proving 'no sleep happened' at the driver level (the three named paths: stage-10 gate, verify-fix, commit-gate) is therefore only provable at the collaborator level via PlanningWorktreeDeterministicFailureTests.", "FakeClockGuard (tools/VisualRelay.Guards/FakeClockGuard.cs) does not enforce rule (c) 'no src csproj references a time-testing package' prescribed in step 6 — the rule appears only in the XML doc comment (line 12) with no scan logic. ScanTree only walks Roslyn .cs trees (CachedSyntaxTreesFixture globs *.cs), so csproj files are never inspected, and FakeClockGuardTests has no corresponding test. The guardrail against a future regression (e.g. adding Microsoft.Extensions.Time.Testing to a src csproj) is absent."] }

## Stage 8 - Visual-review

_Skipped: Pure backend C# logic changes — retry-loops, static classifier, dependency threading, static-analysis guard, and unit tests. No UI, markup, layout, images, charts, or generated documents to visually review._

## Stage 9 - Fix

{ "summary": "Resolved both stage-7 review issues: (1) Added TimeProvider? pass-through parameter to ForTests so driver-level tests can inject ManualTimeProvider. (2) Added FindCsprojViolations to FakeClockGuard that scans src/*.csproj for PackageReference to Microsoft.Extensions.Time.Testing — with 2 new unit tests and live-gate coverage. Full suite: 3242 passed, 0 failed, 114 skipped in 56s." }

## Stage 10 - Verify

{
  "summary": "Added GitFailureClassifier.IsDeterministic (exit≠0 + message contains 'not a git repository' or 'invalid reference') with early-exit branches in PlanningWorktree.RunGitAsync (throws InvalidOperationException immediately, no delay) and GitCommitter.GitAsync (returns failed result immediately). Threaded TimeProvider? through RelayDriverDependencies as last positional (default null → TimeProvider.System) and passed _dependencies.TimeProvider at 4 call sites. Created FakeClockGuard (tools/VisualRelay.Guards) enforcing: no ManualTimeProvider/FakeTimeProvider identifiers in src/tools, every TimeProvider? parameter defaults to null, and no src csproj references Microsoft.Extensions.Time.Testing — with 7 unit tests including live-tree gate and csproj scanning. Updated GitCommitterResilienceTests: transient-retry tests use index.lock messages, deterministic test asserts exactly 1 invocation with no time advance. Updated stale comments in RewriteMutualExclusionTests.cs and ControlApiConfirmGatedTests.cs referencing ~2.5s retry cost. Full suite: 3242 passed, 0 failed, 114 skipped.",
  "commitMessages": [
    "feat: fail fast on deterministic git failures",
    "fix: skip retry backoff for non-repo git directories",
    "perf: eliminate 1.25s retry sleep on non-git roots",
    "feat: add GitFailureClassifier for message-based detection",
    "feat: add FakeClockGuard to keep real time in production"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

