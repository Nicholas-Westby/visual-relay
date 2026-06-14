# Inject working directory into every stage prompt so agents never guess paths

Stage subagents receive no explicit statement of their working directory. Every stage
of every task begins with agents guessing—and getting wrong—a hardcoded absolute path
before recovering. Evidence from real runs:

- `cd /home/user` — No such file or directory
- `cd /workspace` — No such file or directory
- `cd /Users/tyler/Documents/Developer/Visual Relay` — No such file or directory
- `cd /Users/jonathan/Projects/nono-mono` — No such file or directory
- `cd /Users/nicholaswestby/Dev/visual-relay` — No such file or directory

Each failed guess triggers `pwd && ls` to recover, then a correct `cd BASEDIR`. This
costs 2–4 turns per stage × 11 stages × every task, billed at whatever tier the stage
runs on (including frontier). The fix is trivial: the harness already knows the real
path and passes it to swival as `--base-dir`; it just never tells the agent in the
prompt the agent actually reads.

## Current state (researched)

### How swival is launched and which cwd the agent runs with

`src/VisualRelay.Core/Execution/ProcessRunners.cs:46-65` — `BuildArguments` assembles
the swival CLI arguments. `invocation.TargetRoot` is passed as `--base-dir`:

```csharp
"--base-dir", invocation.TargetRoot,
"--system-prompt", invocation.Stage.SystemPrompt,
```

`ProcessCapture.cs:62` sets the OS-level working directory to the same value:

```csharp
process.StartInfo.WorkingDirectory = workingDirectory;
```

and `ProcessRunners.RunAsync.cs:100` passes `attemptInvocation.TargetRoot` as that
working directory. So the swival process launches with its OS cwd AND swival's own
`--base-dir` both pointing at `TargetRoot` — the repo root of the target project.

### How `TargetRoot` flows from the driver

`src/VisualRelay.Domain/StageInvocation.cs:7` — `TargetRoot` is the third field of
`StageInvocation`. `RelayDriver.VerifyFix.cs:229-261` (`BuildInvocation`) constructs
every invocation: the `rootPath` parameter (the path VR was opened against) is passed
directly as `TargetRoot`. For fix-verify the same `rootPath` is used
(`RelayDriver.VerifyFix.cs:91-93`).

### Where the user-visible prompt is assembled

`ProcessRunners.Helpers.cs:101-137` — `BuildPrompt(StageInvocation invocation)` builds
the string that becomes the final argument to swival (the user-turn prompt the agent
reads). It currently produces sections in this order:

1. `# Relay stage N: Name` + `Task: <taskId>`
2. `## Task input` — the task markdown
3. `## Manifest` — impacted file list
4. `## Task context` (optional)
5. `## Log sources` (optional)
6. `## Prior stages` — ledger
7. `## Failing verify output` (optional, only when `LastTestOutput` is set)
8. `## Verify command` (optional, only when `TestCommand` is set)

The working directory is **not** mentioned anywhere in this prompt. The agent must
infer it from context or guess.

### The system prompt (per-stage short directive)

`src/VisualRelay.Core/Execution/RelayStages.cs:39-52` — `SystemPromptFor(name)` returns
a one-line instruction per stage (e.g. `"Investigate the codebase; record findings and
constraints. Do not edit files."`). This is passed as `--system-prompt` to swival
(`ProcessRunners.cs:56`). It is a separate swival mechanism from the user-turn prompt
built by `BuildPrompt` — both are agent-visible, but the system prompt is shorter and
stage-scoped. Either location would carry the injection, but `BuildPrompt` is the right
place: it is already where `## Verify command` is injected for stage 10, it is a single
C# method, it already has `invocation.TargetRoot` accessible via the `invocation`
parameter, and adding a `## Working directory` section here keeps the system prompt
short and stage-purpose-focused.

### Where `TestCommand` is currently injected (the precedent)

`ProcessRunners.Helpers.cs:131-133`:

```csharp
if (!string.IsNullOrWhiteSpace(invocation.TestCommand))
{
    parts.AddRange(["", "## Verify command", "Run this exact command to reproduce and confirm the fix:", invocation.TestCommand]);
}
```

`TestCommand` is populated only for stages 10 (Fix-verify) via `RelayDriver.VerifyFix.cs:92`
and stage 9 indirectly (`RelayDriver.cs:99` calls `BuildInvocation` without `testCommand`,
so stage 9 gets `null`). Working directory, by contrast, is needed by every stage — it is
the correct place to make it unconditional.

### The "BASEDIR" trace redaction — does it exist in this codebase?

Exhaustive `grep -r "BASEDIR"` across the entire repo returns zero matches. The term
"BASEDIR" appears in the problem description as a conceptual label for the path that is
redacted in trace output. Code inspection of the event pipeline confirms: `FileRelayEventSink`
(`Logging/FileRelayEventSink.cs:44-74`) logs events verbatim without path replacement;
`ObservableRelayEventSink` (`Services/ObservableRelayEventSink.cs`) forwards events
unchanged to the UI; `RelayTraceTailer`/`RelayTraceParser` read trace JSONL files from
swival verbatim and forward them to the UI; `TrimForTrace` in
`ProcessRunners.Helpers.cs:221-224` truncates content at 1500 chars but does not
replace paths.

**Conclusion: there is no BASEDIR redaction in the C# harness today.** The real path
flows through trace events, log lines, and the UI without substitution. The term "BASEDIR"
in the problem statement refers to what agents *would like* to know, not to a token in
VR's code. Adding a `## Working directory` line to `BuildPrompt` therefore has no
interaction with any redaction mechanism — the real path already appears in other
pipeline outputs.

### Relevant test patterns

`tests/VisualRelay.Tests/SwivalSubagentRunnerTests.cs:141-171` demonstrates the pattern
for asserting that a section appears in the captured prompt:

```csharp
var invocation = SwivalTestHelpers.Invocation(repo.Root) with { TestCommand = "..." };
var result = await runner.RunAsync(invocation);
var captured = await File.ReadAllTextAsync(Path.Combine(repo.Root, "prompt-capture.txt"));
Assert.Contains("## Verify command", captured, StringComparison.Ordinal);
Assert.Contains("bunx biome format && bun test", captured, StringComparison.Ordinal);
```

The same fake-swival `last="${@: -1}"` trick captures the final argument (the user prompt)
to a file, which the test then reads. An identical pattern covers the working-directory
assertion.

`BuildPrompt` is a public static method tested directly through
`RunAsync` (no separate unit tests call it directly). The integration tests using a fake
swival binary are the right vehicle.

## What to build

### 1. Add `## Working directory` to `BuildPrompt` in `ProcessRunners.Helpers.cs`

In `BuildPrompt` (`ProcessRunners.Helpers.cs:101-137`), add a fixed section immediately
after the header block (after `Task: <taskId>` and before `## Task input`). Using
`invocation.TargetRoot`, which is already in scope:

```csharp
$"Working directory: {invocation.TargetRoot}",
```

Position it near the top so agents see it before reading task details. The section
header and phrasing should be unambiguous and match the instruction-following style
of existing sections:

```
Working directory: /abs/path/to/repo
```

No conditional guard — every stage benefits. `TargetRoot` is always set (it is a
required `StageInvocation` field).

Optionally: if `invocation.TestCommand` is set (currently only stage 10), also emit a
brief `Verify command: <cmd>` line in the same top block so agents don't need to scroll
to the bottom to find it. This is additive — do NOT remove the existing `## Verify
command` section, which provides narrative context ("Run this exact command...").

### 2. Write a failing test first (TDD)

In `tests/VisualRelay.Tests/SwivalSubagentRunnerTests.cs` (or a new
`SwivalSubagentRunnerPromptTests.cs`), add a test that captures the raw prompt and
asserts the working-directory line is present for every stage. Use the same
fake-swival `last="${@: -1}"` pattern already in the file:

```csharp
[Fact]
public async Task BuildPrompt_EveryStage_ContainsWorkingDirectoryFact()
{
    // verify the line appears in the actual prompt swival receives —
    // not just in BuildPrompt's return value — by capturing it via fake swival.
    foreach (var stage in RelayStages.All.Where(s => s.Kind != "driver"))
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(repo.Root, "fake-swival-wdir",
            """
            #!/usr/bin/env bash
            last="${@: -1}"
            printf '%s' "$last" > prompt-capture.txt
            printf '```json\n{"summary":"ok"}\n```\n'
            """);
        var runner = new SwivalSubagentRunner(TestConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady);
        var invocation = SwivalTestHelpers.Invocation(repo.Root) with { Stage = stage };

        await runner.RunAsync(invocation);

        var captured = await File.ReadAllTextAsync(Path.Combine(repo.Root, "prompt-capture.txt"));
        Assert.Contains($"Working directory: {repo.Root}", captured, StringComparison.Ordinal);
    }
}
```

This test must fail before the `BuildPrompt` change and pass after it. The "driver"
stage (Stage 11 — Commit) runs in-process with no swival invocation, so it is excluded
from the loop.

### 3. Ensure no existing tests break

`SwivalSubagentRunnerTests.cs:173-194` asserts `DoesNotContain("## Failing verify
output", ...)` for Stage 1 — that test is unaffected because the working-directory
line is unconditional and distinct. No test currently asserts the absence of a
`Working directory:` line, so no existing negative assertion will break.

## Done when

- Every stage prompt (stages 1–10; stage 11 is driver-only, no swival invocation)
  contains the line `Working directory: <abs-path>` near the top, sourced from
  `invocation.TargetRoot`, the same value passed as `--base-dir` and as the OS cwd.
- The new test (`BuildPrompt_EveryStage_ContainsWorkingDirectoryFact`) was written
  first, failed against the current code, and passes after the `BuildPrompt` change.
- No agent output in real runs contains a `cd /home/user`, `cd /workspace`, or
  `cd /Users/<someone>/...` probe before the correct `cd` (observable in trace output).
- There is no BASEDIR redaction mechanism to worry about: path appears in traces already,
  and the injection does not change that.
- `./visual-relay check` is green; no existing tests regress.
- Changed files: `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs` (the one
  `BuildPrompt` addition, ~3-5 lines) and one test file (~30 lines). Both stay under
  300 lines changed.
- Conventional Commit subject: `fix(harness): inject working directory into every stage prompt`
