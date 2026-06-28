## Stage 1 - Ideate

{
  "summary": "Add a SyncOverAsyncGuard following the RealSleepGuard idiom — Roslyn static analysis that flags .Result/.GetAwaiter().GetResult()/.Wait() on Task types inside test methods, with a // vr-allow-sync-over-async: <reason> suppression. Wired into the test suite via a live-tree enumeration gate. The three options differ in false-positive avoidance: (A) syntax-only with test-method scoping, (B) full semantic-model type resolution, (C) dual-mode supporting both.",
  "options": [
    "Option A: Syntax-only Roslyn guard with test-method-scoped heuristics — flags .Result/.Wait()/.GetAwaiter().GetResult() only inside [Fact]/[AvaloniaFact]-decorated methods, with proximity bonus for UI-dispatcher signals (Dispatcher.UIThread, window.Show(), RunJobs). Fastest, matches RealSleepGuard idiom exactly, trivially portable. Non-[Fact] helpers (WindowsLauncherTests.cs:60, MainWindowViewModelTests.cs:273) are clean by construction.",
    "Option B: Full Roslyn semantic-model guard — resolves types via Compilation+SemanticModel, only flags on Task/ValueTask/TaskAwaiter expressions. Zero false positives on non-Task .Result properties. Slightly slower and less portable, but maximum precision.",
    "Option C: Dual-mode guard with Mode.Syntax (default, fast) and Mode.Semantic (opt-in, strict). Live-tree gate uses Mode.Syntax; CI can opt into Mode.Semantic. Future-proof but overengineered for today's concrete problem."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase has a well-established guard pattern in `RealSleepGuard.cs` (tools/VisualRelay.Guards/) that serves as the exact model: a pure static Roslyn syntax-only matcher (`FindViolations`) returning `Violation` records, with filename-based self-exemption and `// vr-allow-sleep: <reason>` line-level suppression. The guard is enforced by a `RealSleepGuardTests` meta-test with a live enumeration `[Fact]` that walks all `*.cs` in the test project and asserts zero violations — this runs as part of `dotnet test`, which is part of `./visual-relay check`. The problematic sync-over-async pattern (`.Result`, `.GetAwaiter().GetResult()`, `.Wait()` on Task inside `[Fact]`/`[AvaloniaFact]` test methods) deadlocks the single-threaded Avalonia headless dispatcher. Legitimate uses exist outside the dispatcher context: `WindowsLauncherTests.cs:60` uses `.GetAwaiter().GetResult()` on process stdout/stderr in a non-`[AvaloniaFact]` test, and `MainWindowViewModelTests.cs:273,298`/`ObsidianSummaryWriterTests.cs:50` use it in static helper methods. `BackendVenv.Result(...)` is a class name, not `Task.Result`. The correct async pattern is `async Task` with `await`, shown by `TaskDetailErrorRefreshTests` and all other `[AvaloniaFact]` tests. The Ideate stage selected Option A (syntax-only, test-method-scoped heuristics) as the implementation approach.",
  "constraints": [
    "Must mirror RealSleepGuard exactly: same `FindViolations(IEnumerable<(string Path, string Source)> files)` signature returning `IReadOnlyList<Violation>`, same project location (`tools/VisualRelay.Guards/`), same self-exempt filename pattern, same `// vr-allow-sync-over-async: <reason>` suppression comment (bare marker with no reason does not suppress).",
    "Syntax-only Roslyn analysis (Option A) — no SemanticModel/type resolution. Scope to `[Fact]`/`[AvaloniaFact]`/`[Theory]`/`[AvaloniaTheory]`-decorated method bodies by checking `AttributeListSyntax` on `MethodDeclarationSyntax`. This avoids false positives on non-test helpers by construction.",
    "Must detect all three blocking forms: `.Result` (property access), `.GetAwaiter().GetResult()` (two-part chain), and `.Wait()` / `.Wait(timeout)` (method invocation) on any expression inside a test method body.",
    "Must NOT flag `BackendVenv.Result` — this is a class name/constructor argument, not `Task.Result`. Naturally avoided by scoping to test method bodies and checking member-access syntax on arbitrary expressions rather than property names alone.",
    "Must NOT flag legitimate non-dispatcher uses: `WindowsLauncherTests.cs` stdout/stderr reads (non-`[AvaloniaFact]` test), `MainWindowViewModelTests`/`ObsidianSummaryWriterTests` static helpers (not inside test method bodies).",
    "Self-exempt filenames: `SyncOverAsyncGuard.cs`, `SyncOverAsyncGuardTests.cs`, `RealSleepGuard.cs`, `RealSleepGuardTests.cs`, `WindowsLauncherTests.cs`.",
    "Live enumeration gate: a `[Fact]` method `AllTestProjectCsFiles_HaveNoSyncOverAsync` that walks `tests/VisualRelay.Tests/*.cs` (excluding bin/obj), feeds all sources to `FindViolations`, and asserts zero violations. This runs as part of `dotnet test` → `./visual-relay check`.",
    "Tests must include: basic detection of `.Result` inside `[AvaloniaFact]`, detection of `.GetAwaiter().GetResult()`, detection of `.Wait()`, no false positive for `BackendVenv.Result`, no false positive in non-test helper, suppression via `// vr-allow-sync-over-async: <reason>`, bare marker rejection, and the live enumeration gate.",
    "Test method sorting must be by path (ordinal) then line (same as RealSleepGuard).",
    "Output format for violations must match RealSleepGuard conventions: `{Path}:{Line}: {Snippet} — {Reason}`.",
    "Optional but expected: add `sync-over-async` subcommand to `tools/VisualRelay.Guards/Program.cs` dispatch alongside `shell-size`/`file-size`/`source-enumeration`, so `./visual-relay guards sync-over-async` works as a standalone check."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The deadlock is a classic sync-over-async on the single-threaded Avalonia headless dispatcher. A test method that calls .Result / .GetAwaiter().GetResult() / .Wait() on a Task blocks the dispatcher thread, preventing the awaited continuation (which needs the same thread) from running. The correct pattern (TaskDetailErrorRefreshTests) uses async Task with await + Dispatcher.UIThread.RunJobs(). All existing .GetAwaiter().GetResult() usage (WindowsLauncherTests.cs:60, MainWindowViewModelTests.cs:273/298, ObsidianSummaryWriterTests.cs:50) lives in static helper methods outside [Fact]/[AvaloniaFact] bodies, so Option A's test-method scoping avoids false positives by construction. No .Wait() calls exist. .Result only appears as BackendVenv.Result (class name) or Task.FromResult (factory). The RealSleepGuard (tools/VisualRelay.Guards/RealSleepGuard.cs) provides the exact template: FindViolations with Violation records, self-exempt filenames, // vr-allow-* suppression, and a live enumeration gate in the test suite. The test project already references the Guards project.",
  "excerpts": [
    "TaskDetailErrorRefreshTests.cs:23-25: // async (awaited, never blocked) so these run cleanly under the single-threaded // Avalonia headless dispatcher — a sync-over-async .GetAwaiter().GetResult() // here would deadlock the dispatcher.",
    "TaskDetailErrorRefreshTests.cs:44-46: [AvaloniaFact] public async Task DrainRunStart_OnSelectedTask_ClearsStaleError_ThenCommitKeepsItCleared() { ... Dispatcher.UIThread.RunJobs(); ... }",
    "WindowsLauncherTests.cs:60: return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()); — in static LaunchPowerShellScript(), not a test method body",
    "MainWindowViewModelTests.cs:273: StageStatusRecord.WriteAsync(taskDirectory, statusEntries).GetAwaiter().GetResult(); — in private static void WriteStaleReportAsync() helper",
    "ObsidianSummaryWriterTests.cs:50: StageStatusRecord.WriteAsync(taskDir, entries).GetAwaiter().GetResult(); — in private static void WriteStatusRecord() helper",
    "RealSleepGuard.cs:62: public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files) — the exact signature to mirror",
    "RealSleepGuardTests.cs:149: public void AllTestProjectCsFiles_AreSleepFree() — the live enumeration gate pattern, walks tests/VisualRelay.Tests/*.cs excluding bin/obj",
    "Program.cs:20-25: return sub switch { \"shell-size\" => ..., \"file-size\" => ..., \"source-enumeration\" => ..., _ => Unknown(sub) } — dispatch needs sync-over-async added"
  ],
  "repro": "A deadlocking test would look like:\n\n[AvaloniaFact]\npublic void MyDeadlockingTest()\n{\n    var task = SomeAsyncOperation();\n    task.GetAwaiter().GetResult(); // DEADLOCK — dispatcher thread blocked\n}\n\nThe guard detects this because:\n1. Method has [AvaloniaFact] attribute\n2. .GetAwaiter().GetResult() member access chain exists in method body\n3. → reported as violation: path:line: snippet — 'sync-over-async: .GetAwaiter().GetResult() in [AvaloniaFact] test method'\n\nThe correct pattern (no violation):\n\n[AvaloniaFact]\npublic async Task MyCleanTest()\n{\n    await SomeAsyncOperation(); // no blocking\n    Dispatcher.UIThread.RunJobs(); // pump dispatcher for UI bindings\n}"
}

## Stage 4 - Plan

{
  "plan": "## Implementation Plan: SyncOverAsyncGuard\n\n### 1. Create `tools/VisualRelay.Guards/SyncOverAsyncGuard.cs`\n\nMirror `RealSleepGuard.cs` exactly in structure:\n\n- **`public static class SyncOverAsyncGuard`** in namespace `VisualRelay.Guards`\n- **`public sealed record Violation(string Path, int Line, string Snippet, string Reason)`**\n- **`public static IReadOnlyList<Violation> FindViolations(IEnumerable<(string Path, string Source)> files)`** — identical signature\n- **Self-exempt filenames**: `SyncOverAsyncGuard.cs`, `SyncOverAsyncGuardTests.cs`, `RealSleepGuard.cs`, `RealSleepGuardTests.cs`, `WindowsLauncherTests.cs`\n- **Suppression regex**: `// vr-allow-sync-over-async: <reason>` (requires non-whitespace after colon, same as sleep guard)\n\n**Detection logic** (syntax-only, Option A):\n\n1. Parse each file with `CSharpSyntaxTree.ParseText(source, CSharpParseOptions(LanguageVersion.Latest))`\n2. Find every `MethodDeclarationSyntax` that carries a `[Fact]`, `[AvaloniaFact]`, `[Theory]`, or `[AvaloniaTheory]` attribute (check `AttributeLists` for `AttributeSyntax` whose `Name.ToString()` matches).\n3. Within each test method body, scan for three blocking patterns:\n   - **`.Result`**: `MemberAccessExpressionSyntax` where `Name.Identifier.Text == \"Result\"`. This is a property access on any expression — inside a test body, `.Result` on a Task is the deadlock trigger. False positives are minimal because the codebase's only `.Result` uses are `new BackendVenv.Result(...)` (object creation, not member access) and `Task.FromResult(...)` (invocation with \"FromResult\", not property \"Result\").\n   - **`.GetAwaiter().GetResult()`**: An `InvocationExpressionSyntax` whose `Expression` is a `MemberAccessExpressionSyntax` with `Name.Identifier.Text == \"GetResult\"`, whose own `Expression` is an `InvocationExpressionSyntax` whose `Expression` is a `MemberAccessExpressionSyntax` with `Name.Identifier.Text == \"GetAwaiter\"`.\n   - **`.Wait()` / `.Wait(timeout)`**: `InvocationExpressionSyntax` whose `Expression` is a `MemberAccessExpressionSyntax` with `Name.Identifier.Text == \"Wait\"`.\n4. Compute the 1-based line number from `SourceText.Lines.GetLinePosition(spanStart).Line + 1`.\n5. Suppress any violation whose line matches `// vr-allow-sync-over-async: <non-whitespace-reason>` (a bare colon with no reason does NOT suppress).\n6. Deduplicate by `(Line, Reason)` within a file.\n7. Sort violations by path (ordinal) then line.\n\n**Reason strings**:\n- `.Result` → `\"sync-over-async: .Result in [Fact]/[AvaloniaFact] test method\"`\n- `.GetAwaiter().GetResult()` → `\"sync-over-async: .GetAwaiter().GetResult() in [Fact]/[AvaloniaFact] test method\"`\n- `.Wait()` → `\"sync-over-async: .Wait() in [Fact]/[AvaloniaFact] test method\"`\n\n### 2. Create `tests/VisualRelay.Tests/SyncOverAsyncGuardTests.cs`\n\nMirror `RealSleepGuardTests.cs` exactly. The class is `public sealed class SyncOverAsyncGuardTests`. Tests:\n\n1. **`DotResult_InAvaloniaFact_IsReported`** — `[Fact]`: inline source with `[AvaloniaFact] void M() { var t = SomeAsync(); _ = t.Result; }`. Asserts single violation.\n2. **`GetAwaiterGetResult_InFact_IsReported`** — `[Fact]`: inline source with `[Fact] void M() { SomeAsync().GetAwaiter().GetResult(); }`. Asserts single violation.\n3. **`Wait_InAvaloniaFact_IsReported`** — `[Fact]`: inline source with `[AvaloniaFact] void M() { SomeAsync().Wait(); }`. Asserts single violation.\n4. **`AwaitPattern_IsClean`** — `[Fact]`: correct `async Task` pattern with `await` inside `[AvaloniaFact]` yields zero violations.\n5. **`BackendVenvDotResult_IsNotReported`** — `[Fact]`: `new BackendVenv.Result(null)` inside `[Fact]` is an object creation, not member access → zero violations.\n6. **`GetAwaiterGetResult_InStaticHelper_NotInTestMethod_IsNotReported`** — `[Fact]`: `private static void Helper() { … .GetAwaiter().GetResult(); }` plus a `[Fact]` test method that doesn't use it. The helper is not a test method → zero violations.\n7. **`AllowMarker_WithReason_Suppresses`** — `[Fact]`: `.Result` line with `// vr-allow-sync-over-async: justified` → zero violations.\n8. **`BareAllowMarker_StillReported`** — `[Fact]`: `.Result` line with `// vr-allow-sync-over-async:` (no reason) → still reported.\n9. **`AllTestProjectCsFiles_HaveNoSyncOverAsync`** — the live enumeration gate `[Fact]`: walks `tests/VisualRelay.Tests/*.cs` (excluding `bin/` and `obj/`), calls `SyncOverAsyncGuard.FindViolations`, asserts zero. Uses `RepoSetup.Root` and the same `IsBuildArtifact` helper.\n\nTest sorting: by path (ordinal) then line — verified in the live gate assertion message format.\n\n### 3. Edit `tools/VisualRelay.Guards/Program.cs`\n\nAdd `\"sync-over-async\"` to the `sub switch` dispatch. The handler:\n- Resolves the repo root via `GuardRepoRoot.Resolve()`\n- Walks `tests/VisualRelay.Tests/**/*.cs` excluding `bin/`/`obj/`\n- Reads each file, calls `SyncOverAsyncGuard.FindViolations`\n- Prints violations to stderr and returns exit code 1 if any found, 0 otherwise\n- Also update the usage string at top and the `Unknown` error message to list `sync-over-async`\n\nThis makes `./visual-relay guards sync-over-async` work as a standalone check. The primary enforcement is via the live gate inside `dotnet test`, which is part of `./visual-relay check`.\n\n### 4. Wiring\n\nNo changes needed to `CheckCommand.cs` or `GuardRunner.cs` — the sync-over-async guard is enforced as a test (`AllTestProjectCsFiles_HaveNoSyncOverAsync`), which runs as part of `dotnet test` → `./visual-relay check`. The standalone `./visual-relay guards sync-over-async` is a convenience for manual inspection.\n\n### Key design decisions\n\n- **Syntax-only (Option A)**: No `SemanticModel` / type resolution. Scoping to `[Fact]`/`[AvaloniaFact]`/`[Theory]`/`[AvaloniaTheory]` method bodies avoids false positives on static helpers (MainWindowViewModelTests, ObsidianSummaryWriterTests) by construction.\n- **`.Result` flagging**: Inside test methods, any `.Result` member access is flagged. The codebase's existing `.Result` uses are all `new BackendVenv.Result(...)` (object creation) or `Task.FromResult(...)` (invocation with different name), so this produces zero false positives on the current suite. Future false positives on non-Task `.Result` properties can be suppressed with the allow-marker.\n- **General portability**: The guard keys on C# syntax patterns (`[Fact]`, `.Result`, `.GetAwaiter()`, `.Wait()`) — no VR-specific symbols. Works for any Avalonia/xUnit codebase.",
  "manifest": [
    "+tools/VisualRelay.Guards/SyncOverAsyncGuard.cs",
    "+tests/VisualRelay.Tests/SyncOverAsyncGuardTests.cs",
    "tools/VisualRelay.Guards/Program.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1, untracked deleted: 2.

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/SyncOverAsyncGuardTests.cs"], "rationale": "All 9 tests pass. The SyncOverAsyncGuard is a syntax-only Roslyn guard modelled on RealSleepGuard. It flags .Result/.GetAwaiter().GetResult()/.Wait() inside [Fact]/[AvaloniaFact]/[Theory]/[AvaloniaTheory] test method bodies. The live enumeration gate AllTestProjectCsFiles_HaveNoSyncOverAsync confirms the existing suite is clean. The guard keys on C# syntax patterns only — no VR-specific symbols — so it ports to any Avalonia/UI codebase." }

## Stage 6 - Implement

{ "summary": "Created SyncOverAsyncGuard (syntax-only Roslyn guard) plus runner, wired into Program.cs dispatch. All 9 tests pass including the live enumeration gate confirming the existing suite is sync-over-async clean." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "SyncOverAsyncGuard is implemented and verified. All 9 tests pass including the live enumeration gate AllTestProjectCsFiles_HaveNoSyncOverAsync, confirming the existing test suite is free of sync-over-async deadlock patterns. The guard detects .Result/.GetAwaiter().GetResult()/.Wait() inside [Fact]/[AvaloniaFact] test method bodies, supports // vr-allow-sync-over-async: <reason> suppression, and is wired into ./visual-relay guards sync-over-async." }

## Stage 9 - Verify

{
  "summary": "Added SyncOverAsyncGuard — a syntax-only Roslyn guard modelled on RealSleepGuard that detects sync-over-async deadlock patterns (.Result, .GetAwaiter().GetResult(), .Wait()) inside [Fact]/[AvaloniaFact] test method bodies. The guard is enforced by a live enumeration gate (AllTestProjectCsFiles_HaveNoSyncOverAsync) running as part of dotnet test, and is wired into ./visual-relay guards sync-over-async. All 9 tests pass; the existing suite is verified clean.",
  "commitMessages": [
    "feat(guards): add SyncOverAsyncGuard to detect test-method sync-over-async deadlocks",
    "test(guards): add SyncOverAsyncGuard with live enumeration gate and CLI runner",
    "fix(tests): prevent sync-over-async deadlocks in headless-UI test methods via static guard",
    "feat: add SyncOverAsyncGuard — Roslyn syntax guard for .Result/.Wait() in [Fact] bodies"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

