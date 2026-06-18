# Fix: sandboxed verify merges `-c` and command into one arg — `/bin/sh` can't parse it

The sandboxed verify step (stage 9/10 gate) runs the test command through
`nono run -p vr-guard --allow-cwd -- /bin/sh -c "<command>"`. But the `-c` flag
and the command string are combined into a **single argument** in the argument
list, and the `IEnumerable<string>` overload of `ProcessCapture.RunAsync` does
**no splitting** — so `/bin/sh` receives the literal string
`-c "bun test --timeout 15000"` as one argument, fails to parse it, prints its
usage text, and exits with code 2. Every sandboxed verify is guaranteed red
regardless of whether the tests actually pass.

Confirmed from logs and reproduced by experiment — not a hypothesis.

## Confirmed incident

Drain `20260618060811` (JobFinder, `bypassSandbox` = `false` — the default).
All 3 tasks flagged identically; the drain halted after 3 consecutive
non-convergent flags.

### Evidence from `.relay/<task>/run.log` (fix-timing-estimates-2)

The LLM subagent ran `bun test --timeout 15000` **inside** the sandbox and got
**6586 pass, 7 skip, 0 fail** (exit 0). Seconds later, the orchestrator's own
verify ran the same command and got exit code 2:

```
07:09:24  agent: run_command ["bun","test","--timeout","15000"] → 6586 pass  0 fail  (exit 0)
07:09:31  verify_retry reason=first-run-nonzero
07:09:33  verify_result exitCode=2 check=red reason=…/bin/sh: - : invalid option…
07:09:33  stage_done name=Fix-verify
07:11:10  verify_retry reason=first-run-nonzero         ← second attempt (retry)
07:11:12  verify_result exitCode=2 check=red            ← identical failure
07:11:12  flagged reason=verify non-convergent: working tree unchanged, same failure persists
```

The convergence guard (correctly) bailed on attempt 2 because the tree hash was
unchanged and the distilled reason was identical — the agent made no edits
because the tests were already green.

### Evidence from `.relay/<task>/stage10-attempt1.verify-output.txt`

The full captured output (all 3 tasks, both attempts — 6 files, all identical
tail):

```
  Applying sandbox...

/bin/sh: - : invalid option
Usage:	/bin/sh [GNU long option] [option] ...
	/bin/sh [GNU long option] [option] script-file ...
GNU long options:
	--debug
	--debugger
	…
Shell options:
	-irsD or -c command or -O shopt_option		(invocation only)
	-abefhkmnptuvxBCHP or -o option

Command exited with code 2.

No path denials were observed during this session.
The failure may be unrelated to sandbox restrictions.
```

Note: `No path denials were observed` — the sandbox is NOT blocking anything.
The command never ran. The `nono` banner and credential WARNs are benign noise
(identical on the agent's successful run).

### The same pattern hit all 3 tasks

```
fix-job-thomson-reuters-senior-software-engineer-ii  → flagged (exit 2)
fix-jobs-scored-zero-3                                → flagged (exit 2)
fix-timing-estimates-2                                → flagged (exit 2)
→ DRAIN-HALTED: 3 consecutive flagged tasks
```

### Why previous drains didn't hit this

The nono-wrapped verify was added in commit `6d69e24` (2026-06-16) and the
`-lc`→`-c` change in `264c50f` (same day). The June 11–12 drains predate the
feature — verify ran through `ShellTestRunner` directly (no nono wrapper), which
uses the **string** overload of `ProcessCapture.RunAsync` and works. The June 17
drain used `bypassSandbox: true` (the operational workaround from the
`harness-fix-verify-nono-dotnet-runtime` task), which also bypasses
`SandboxedTestRunner` entirely. The June 18 drain was the first to run with
`bypassSandbox: false` AND the nono-wrapped verify — exposing the bug.

## Root cause — CONFIRMED by experiment

### The bug: `-c` and command are one argument

`SandboxedTestRunner.ResolveLaunch()` (line 73 of
`src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`) builds the argument
list for `nono run … -- /bin/sh <FLAG_AND_COMMAND>`:

```csharp
var args = new List<string>(prefix)
{
    "/bin/sh",
    $"-c \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
};
return ("nono", args);
```

The second element is a **single string** `-c "bun test --timeout 15000"` — the
`-c` flag and the command are merged with a space and wrapping quotes into one
`List<string>` entry.

### Why the `IEnumerable<string>` overload doesn't split it

`SandboxedTestRunner.RunAsync()` passes these args to
`ProcessCapture.RunAsync(fileName, args, …)` — the `IEnumerable<string>`
overload. That overload adds each element to `ProcessStartInfo.ArgumentList`
individually (**no** string parsing, **no** splitting):

```csharp
// ProcessCapture.cs, lines 27–33
var startInfo = new ProcessStartInfo(fileName);
foreach (var argument in arguments)
{
    startInfo.ArgumentList.Add(argument);
}
```

So `nono` receives `/bin/sh` and `-c "bun test --timeout 15000"` as two
post-`--` arguments. nono passes them through to the sandboxed process.
`/bin/sh` receives the single argument `-c "bun test --timeout 15000"`, tries to
parse it as option flags, sees `- ` (dash + space) as an invalid option, prints
its usage, and exits 2.

### Why the bypass path works (and masked the bug in tests)

`ShellTestRunner` (the bypass/inner runner) calls the **string** overload:

```csharp
// ShellTestRunner.cs
ProcessCapture.RunAsync("/bin/sh", $"-lc \"{command.Replace(...)}\"", …)
```

The string overload creates `new ProcessStartInfo(fileName, arguments)` where
`arguments` is a single string. .NET's `ProcessStartInfo(string, string)`
constructor **parses and splits** the arguments string (honoring quotes), so
`/bin/sh` receives `-lc` and `bun test` as **separate** arguments — works
correctly.

The bug is specific to the sandbox path because it uses
`ArgumentList.Add` (no splitting) with a pre-merged string.

### Experiment reproducing the exact error

```bash
# /bin/sh with -c and command as SEPARATE args (what SHOULD happen)
$ /bin/sh -c 'echo hello'           # → exit 0, prints "hello"

# /bin/sh with -c and command as ONE arg (what the code produces)
$ /bin/sh '-c "echo hello"'         # → exit 2
/bin/sh: - : invalid option
Usage: /bin/sh [GNU long option] [option] ...
```

The second form produces the **exact** error from the verify-output.txt files.

### Why the existing test missed it

`SandboxedTestRunnerArgumentTests.cs` asserts the **buggy** shape:

```csharp
Assert.Equal("-c \"bun test\"", args[6]);  // expects merged string!
```

The test validates that `-c` and the command are a single combined argument —
encoding the bug as the expected behavior. The test passes because it only
checks the argument list shape, never actually executes `/bin/sh`.

## What to fix

In `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`,
`ResolveLaunch()`, the `ShellTestRunner` branch (sandbox-enabled path): pass
`-c` and the command as **separate** list entries instead of one merged string:

```csharp
// Before (buggy):
var args = new List<string>(prefix)
{
    "/bin/sh",
    $"-c \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
};

// After (fixed):
var args = new List<string>(prefix)
{
    "/bin/sh",
    "-c",
    command
};
```

Because the `IEnumerable<string>` overload adds each entry to
`ArgumentList` verbatim (no splitting, no quote-stripping), `-c` and `command`
must be separate entries. No manual quote-escaping is needed —
`ArgumentList.Add` handles each argument as a literal.

**Check the bypass path too**: `ResolveLaunch()` also has a `BypassSandbox`
branch that returns the same merged `-lc "…"` string. That path is currently
dead in production (bypass delegates to `inner.RunAsync`, never calling
`ResolveLaunch`), but it should be fixed for consistency and to prevent a
future regression. However, the bypass path uses `ShellTestRunner.RunAsync`
which calls the **string** overload (which splits), so the bypass path works
despite the merged string. Fix it only if it doesn't break the string-overload
behavior — otherwise leave it and add a comment.

## Tests

### Update the existing unit test

`tests/VisualRelay.Tests/SandboxedTestRunnerArgumentTests.cs`:
`ShellMode_SandboxEnabled_TransformsIntoNonoWrappedShell` currently asserts:

```csharp
Assert.Equal("-c \"bun test\"", args[6]);  // BUG: expects merged string
```

Update to assert **separate** entries:

```csharp
Assert.Equal("-c", args[6]);
Assert.Equal("bun test", args[7]);
```

### Add a regression test

Add a test that verifies the `-c` flag and command are separate entries for a
command with spaces and quotes (e.g. `bun test --timeout 15000`), confirming
`args` contains `"-c"` and `"bun test --timeout 15000"` as distinct elements
— not a single merged `-c "bun test --timeout 15000"`.

### Add an execution-backed test (the coverage gap)

The existing tests only check argument **shape** — they never execute the
command. Add a test (using a simple `/bin/sh -c 'echo ok'` or `true` command)
that actually runs through `SandboxedTestRunner` with sandbox enabled and
asserts exit code 0. This is the test that would have caught the bug. If nono
is not available in the test environment, gate it behind
`VR_RUN_NONO_INTEGRATION=1` (the existing opt-in pattern from
`NonoRealBuildTests`), but **run it** before considering the fix done.

## Done when

- `SandboxedTestRunner.ResolveLaunch()` passes `-c` and the command as
  **separate** `List<string>` entries (not one merged string).
- The updated unit test asserts separate entries.
- An execution-backed test (opt-in integration or a mock that verifies the
  args reach `/bin/sh` as separate tokens) confirms the command actually runs.
- `./visual-relay check` is green; changed files stay under 300 lines.
- Conventional Commit subject for the fix.

## Notes

- The `DirectExecTestRunner` branch of `ResolveLaunch()` is not affected — it
  splits the command into parts and adds each separately, which is correct.
- The agent's own test runs (inside Swival) are not affected — Swival launches
  `bun test` directly, not through `/bin/sh -c`.
- The nono credential WARNs in the verify output (`.ssh`, `.aws`, etc.) are
  benign red herrings — identical on the agent's successful run.
