# Steer coding-stage agents to run targeted tests, not the full gate

During implementation (Implement, Fix, Fix-verify), Swival agents self-verify by running
the project's full gate command — `./visual-relay check` here, which runs format + build
+ 722 tests + screenshots, taking 100–200 s per invocation. Evidence from real runs:
task 05 invoked the full gate **8 times** (~44 % of its 40-min wall-clock, including
5 timeout aborts); task 06 invoked it **6 times = 80–85 % of a 25-min Implement stage**.
The full gate is already run authoritatively by the harness at stage 9 (Verify) and
implicitly in the Commit gate (stage 11). Running it mid-stage is pure overhead and
the dominant wall-clock cost on most tasks.

The root cause is prompt ambiguity: coding-stage agents receive no explicit
test-verification command. The `AGENTS.md` tells contributors "Run the full gate before
considering work done: `./visual-relay check`" (`AGENTS.md:11-12`), and agents follow it.
The harness already has all the ingredients to pass a targeted command — it injects a
verify command into stage-10 (Fix-verify) and already uses `TestFileCommand`/`{files}`
substitution for the Author-tests gate — but it does not extend this to Implement
or Fix, and it does not instruct agents to avoid the full gate during coding.

## Current state (researched)

### Stage system-prompt strings

`src/VisualRelay.Core/Execution/RelayStages.cs:46-50` — the three coding stages carry
only minimal instructions:

```
"Implement" => "Implement the change within the manifest files."
"Fix"       => "Resolve every blocker and warning from review."
"Fix-verify"=> "Fix failures from the pinned suite. Run the exact verify command
               shown in the prompt and confirm it passes (exit 0) before returning
               success — do not run a self-selected subset of tests."
```

Fix-verify already says "run the exact verify command shown in the prompt". Implement
and Fix say nothing about how to verify at all, so agents fall back to whatever the
project's `AGENTS.md` tells contributors, which is the full gate.

### How TestCommand reaches the stage prompt

`StageInvocation` (`src/VisualRelay.Domain/StageInvocation.cs:18`) carries an optional
`string? TestCommand`. When non-null, `BuildPrompt` emits a `## Verify command` section:
"Run this exact command to reproduce and confirm the fix: `<command>`"
(`src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs:131-133`).

The main stage loop at `src/VisualRelay.Core/Execution/RelayDriver.cs:99` calls
`BuildInvocation(…)` **without** passing `testCommand:` — so `TestCommand` is `null`
for all stages 1–9 in the main loop, and no `## Verify command` section appears.

The Fix-verify loop (`src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs:91-93`)
calls `BuildInvocation(…, testCommand: config.TestCommand, …)` — so Fix-verify (stage 10)
gets the `## Verify command` section with the **full** test command. The system prompt
tells the agent to run it exactly, but `config.TestCommand` is the full suite.

The harness's own authoritative test execution at Verify (stage 9) and Fix-verify
(stage 10) runs `config.TestCommand` directly via `_dependencies.TestRunner.RunAsync`
(`RelayDriver.cs:222`, `RelayDriver.VerifyFix.cs:160`). Quality is not reduced by what
the agent runs mid-stage; the harness result is always the ground truth.

### TestFileCommand / {files} substitution

`RelayConfig` (`src/VisualRelay.Domain/RelayConfig.cs:6`) carries `TestFileCommand`,
loaded from `testFileCmd` in `.relay/config.json`
(`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:129`).

At stage 5 (Author-tests) the harness already builds a targeted command from the
manifest's test files:
```csharp
// RelayDriver.cs:146
var command = config.TestFileCommand.Replace("{files}", string.Join(' ', testFiles), …);
```
This targeted command is passed to `AuthorTestGate.RunAsync` for the harness-run
Author-tests verification. The pattern already exists and is exercised; it just is not
extended to coding stages.

VR's own `.relay/config.json` has:
```json
"testFileCmd": "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false"
```
There is no `{files}` token in VR's own `testFileCmd` — the project's dotnet test
command does not accept file paths directly. However, `dotnet test` supports `--filter`
and fully qualified test class names. Other repos may configure `testFileCmd` with a
genuine `{files}` substitution (e.g. `bun test {files}` is the default for JS repos,
`pytest {files}` for Python). The harness must handle both: repos with a meaningful
`{files}` substitution get a targeted command; repos without one (VR itself) fall back
to `config.TestCommand`.

A targeted command is "meaningful" when `config.TestFileCommand` differs from
`config.TestCommand` (i.e. it contains `{files}`) and the manifest contains at least
one test file.

### AGENTS.md instruction the agents follow

`AGENTS.md:11-12`:
> Run the full gate before considering work done: `./visual-relay check`
> (file-size guard, format verification, build, tests, screenshot render).

This is contributor guidance that coding-stage agents follow verbatim in the absence of
a stronger prompt instruction. The harness prompt must explicitly override it.

## What to build

### 1. Compute a targeted test command from the manifest at the start of a run

In `RelayDriver.cs`, after the manifest is populated at stage 4 (or each time the
manifest changes), compute:

```csharp
// Returns config.TestFileCommand with {files} replaced by manifest test files,
// or config.TestCommand when there is no {files} substitution or no test files.
private static string BuildTargetedTestCommand(RelayConfig config, IReadOnlyList<string> manifest)
{
    if (!config.TestFileCommand.Contains("{files}", StringComparison.Ordinal))
        return config.TestCommand;          // no file-parameterised command configured
    var testFiles = manifest.Where(IsTestFile).ToList();
    if (testFiles.Count == 0)
        return config.TestCommand;          // no test files in manifest → fall back
    return config.TestFileCommand.Replace("{files}", string.Join(' ', testFiles),
        StringComparison.Ordinal);
}
```

`IsTestFile` is the same predicate already used in the stage-5 gate
(`RelayDriver.cs:141`, `IsImpl`/not-impl files); the inverse of `IsImpl` selects test
files. Expose the helper or reuse the existing private one.

Compute the targeted command right after `manifest.AddRange(clean)` at stage 4
(`RelayDriver.cs:127`). Store it in a local variable alongside `manifest`.

### 2. Pass the targeted command into Implement (stage 6) and Fix (stage 8)

At `RelayDriver.cs:99`, the main loop calls `BuildInvocation` without `testCommand:`.
Change it so stages 6 (Implement) and 8 (Fix) receive the targeted command:

```csharp
var testCommandForCodingStage =
    stage.Number is 6 or 8 ? targetedTestCommand : null;

var invocation = BuildInvocation(
    rootPath, runId, taskId, taskDirectory, config, stage,
    input, ledger, manifest,
    testCommand: testCommandForCodingStage,
    pinnedSwivalProfileContent: pinnedSwivalProfileContent);
```

This causes `BuildPrompt` to emit a `## Verify command` section for those stages, with
the targeted (or fallback full) test command. No other stages are affected.

### 3. Pass the targeted command into Fix-verify (stage 10) instead of the full command

At `RelayDriver.VerifyFix.cs:91-93`, change:

```csharp
// Before:
input, ledger, manifest, lastTestOutput: failingTestOutput, testCommand: config.TestCommand,

// After:
input, ledger, manifest, lastTestOutput: failingTestOutput, testCommand: targetedTestCommand,
```

The harness still runs `config.TestCommand` authoritatively at line 160 regardless of
what the agent was asked to verify. Passing the targeted command only guides what the
agent self-verifies during implementation; the harness result is the ground truth.

### 4. Update the system prompts for Implement and Fix to prohibit the full gate

In `RelayStages.cs`, revise the three coding-stage system prompts:

```csharp
"Implement" =>
    "Implement the change within the manifest files. " +
    "Verify your changes using ONLY the targeted test command shown in the " +
    "## Verify command section of the prompt. Do NOT run the project's full " +
    "check, lint, or format gate (e.g. `./visual-relay check`) during " +
    "implementation — the harness runs the full gate at the Verify stage.",

"Fix" =>
    "Resolve every blocker and warning from review. " +
    "Verify your changes using ONLY the targeted test command shown in the " +
    "## Verify command section of the prompt. Do NOT run the project's full " +
    "check, lint, or format gate during implementation — the harness runs the " +
    "full gate at the Verify stage.",

"Fix-verify" =>
    "Fix failures from the pinned suite. Run the exact verify command shown in " +
    "the ## Verify command section of the prompt and confirm it passes (exit 0) " +
    "before returning success. Do NOT run a self-selected subset of tests and do " +
    "NOT run the project's full check, lint, or format gate — the harness runs " +
    "the full gate.",
```

The wording is general (no mention of `./visual-relay check` or any language-specific
tool) so it applies to every target repo.

### 5. Add `StageInvocation.TargetedTestCommand` (optional, for clarity)

Alternatively, add a second optional field `TargetedTestCommand` to `StageInvocation`
(`StageInvocation.cs:18`) to keep the targeted command separate from the full
`TestCommand`, and update `BuildPrompt` accordingly. This is cleaner but requires
touching the record; it is acceptable to reuse the existing `TestCommand` field with
the targeted value as described in steps 2–3.

### 6. Tests (write failing tests first — TDD)

All tests live in `tests/VisualRelay.Tests/VisualRelay.Tests.csproj`.

**a. `BuildTargetedTestCommand` unit tests** (new test class, e.g.
`TargetedTestCommandTests.cs`):

- When `testFileCmd` contains `{files}` and the manifest has test files → returns
  the substituted command (file list space-joined).
- When `testFileCmd` has no `{files}` → returns `testCmd` unchanged.
- When `testFileCmd` has `{files}` but the manifest has no test files → returns
  `testCmd`.
- When `testFileCmd` has `{files}` and the manifest has both test and impl files →
  only test files appear in the substitution.

**b. Prompt injection tests** (extend existing `ProcessRunnersHelpersTests.cs` or
`BuildPromptTests.cs`):

- `BuildPrompt` for a stage-6 / stage-8 invocation with a non-null `TestCommand`
  emits a `## Verify command` section.
- `BuildPrompt` for a stage-6 / stage-8 invocation with a null `TestCommand` does
  NOT emit the section (regression guard for other stages).

**c. Integration / pipeline tests** (extend `RelayDriverTests.cs` or a dedicated
`TargetedTestIntegrationTests.cs`):

- When the manifest contains test files and `testFileCmd` has `{files}`, the
  invocations for stages 6, 8, and 10 receive a `TestCommand` that matches the
  substituted targeted command; stage 9's harness-side `TestRunner.RunAsync` still
  receives `config.TestCommand` (full suite).
- When `testFileCmd` has no `{files}`, the invocations for stages 6, 8, and 10
  receive `config.TestCommand` as the prompt verify command (unchanged fallback).

## Done when

- **Agents instructed:** stages 6, 8, and 10 carry a `## Verify command` section in
  the prompt with the targeted test command (or the full test command as fallback when
  no `{files}` substitution is configured); the system prompt for each stage explicitly
  prohibits running the project's full check/lint/format gate during coding.
- **Targeted command built correctly:** `BuildTargetedTestCommand` correctly substitutes
  manifest test files into `testFileCmd` when `{files}` is present; falls back to
  `config.TestCommand` when the pattern is absent or the manifest has no test files.
  Unit tests cover all four cases described above.
- **Full gate authoritative at Verify:** the harness runs `config.TestCommand`
  (unchanged) via `TestRunner.RunAsync` at stages 9 and 11 (`RelayDriver.cs:222`,
  `RelayDriver.VerifyFix.cs:160`, `RelayDriver.CommitGate.cs:45`). No change to those
  call sites. Quality is not reduced: the full gate result always decides pass/fail.
- **General / language-agnostic:** no repo-specific tool names appear in the changed
  system prompts or harness logic. The mechanism is driven entirely by the project's
  `testCmd`/`testFileCmd` config.
- **Tests pass first:** failing tests for `BuildTargetedTestCommand` and prompt
  injection written before the implementation; they fail against the unmodified harness
  and pass after.
- `./visual-relay check` green; all changed files stay under 300 lines;
  Conventional Commit subject(s), e.g.:
  `feat(driver): steer coding stages to verify with targeted tests, not the full gate`.
