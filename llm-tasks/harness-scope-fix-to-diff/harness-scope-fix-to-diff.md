# Harness: stop the file-size guard forcing reflow of unrelated code

On a real run (2026-06-14) a task whose only needed change was a **one-line fix** spent
**8m10s / 50 turns** in the Fix stage and, to clear the deterministic file-size guard,
**reformatted unrelated code** — compacting `RelayQueueController.cs` from 332 → 255 lines —
turning a ~6-line functional change into a 121-line diff. This "reflow tax" inflates cost,
diff size, review surface, and incidental-breakage risk on essentially every task that
happens to touch an already-large file.

The deterministic guard `tools/guards/check-file-size.sh` (the `guardCmd` value) flags any
`.cs`/`.axaml` file over a line limit (default 300). The driver runs it through a
**baseline-aware** path (`RelayDriver.RepoGuards.cs`) that is *supposed* to ignore
pre-existing debt — but it does not, because of how the guard reports. Each violation line
**embeds the file's current line count**:

```
file too large: <path> has <N> lines (limit <L>)
```

The baseline comparison in `RunGuardCheckAsync` diffs violation lines **as exact strings**
(`OutputLineSet` → `ExceptWith`). When the agent edits a pre-existing oversize file and its
count changes from 332 → 333, the baseline string `... has 332 lines ...` no longer equals
the working string `... has 333 lines ...`, so the violation is classified **NEW** and turns
stage 9 red. The agent's only way to make that line disappear is to drop the file **under the
threshold** — i.e. reflow/compact code it merely touched. That is the exact mechanism behind
the observed tax: the guard is nominally baseline-aware but the count in the message defeats
the diff for any edited-but-already-oversize file.

This spec fixes that at the **guard layer** (count-stable output so the baseline diff actually
excludes pre-existing oversize files), and adds a **belt-and-suspenders prompt instruction**
telling the coding stages to make minimal, diff-scoped edits and not reflow unrelated code.
It does **not** disable or weaken the guard: a file the task pushes **over** the threshold
(or a **new** oversize file) still fails, exactly as today.

## Current state (researched)

**Freshness contract.** Every anchor below is identified by a **quoted code/text snippet**,
never by line number — line numbers drift and MUST NOT be used to locate anything. Before
editing, `grep`/search for the quoted snippet; if a quote no longer matches verbatim, STOP and
re-derive the anchor from surrounding context rather than guessing. Read current committed
source with `git show HEAD:<path>` (HEAD is immutable; a live run may be editing the working
tree).

### The guard script — `tools/guards/check-file-size.sh`

A generic line-count check (no toolchain specifics). It enumerates source and prints, per
oversize file, a message **containing the live line count**:

```bash
limit="${VISUAL_RELAY_FILE_LINE_LIMIT:-300}"
...
  lines="$(wc -l < "$file" | tr -d ' ')"
  if (( lines > limit )); then
    echo "file too large: $file has $lines lines (limit $limit)" >&2
    failed=1
  fi
```

The enumeration is `find src tests tools \( -name '*.cs' -o -name '*.axaml' \) -not -path
'*/bin/*' -not -path '*/obj/*' | sort`. It flags **all** oversize files on every run (it is
not change-aware on its own); change/baseline awareness is entirely the driver's job.

### The baseline-aware guard path — `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs`

`RunGuardCheckAsync` runs the guard on the working tree, and when `baselineVerify` is true and
the guard failed, stashes, re-runs the guard on the clean tree, and returns only the lines NOT
in the baseline:

```csharp
var newLines = OutputLineSet(workingOutput);
newLines.ExceptWith(OutputLineSet(baselineOutput));

if (newLines.Count == 0)
    return (null, workingOutput, false); // all pre-existing
```

`OutputLineSet` trims each line and stores it in a **case-sensitive exact-string** set:

```csharp
foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
{
    var trimmed = line.Trim();
    if (trimmed.Length > 0)
        set.Add(trimmed);
}
```

**This is the bug surface.** The set diff is exact-string. Because the guard message embeds
the count (`has <N> lines`), an edited-but-already-oversize file produces a *different* string
in working vs. baseline → `ExceptWith` does not remove it → it is reported as a new violation.
`IntegrateGuardAsync` then returns `GuardFailed: true` and the count-bearing line is fed into
the Fix-verify loop. The pre-existing-only path (which appends the ledger note
`> **Note**: pre-existing guard violations detected (not caused by this task).`) is only
reached when **every** working line already appeared verbatim in the baseline — which never
happens once a touched oversize file's count shifts.

> **Conclusion on the question "is the guard baseline-aware?":** the *mechanism* is
> (`baselineVerify` defaults to `true` — see `RelayConfigLoader.cs` `BaselineVerify: true`),
> but it is **defeated for edited oversize files** because the guard's per-file message is not
> line-count-stable. So pre-existing oversize files the task merely touches **do** pressure
> the agent today. The fix is to make the comparison count-insensitive.

### The Fix-verify feedback (what the agent actually sees)

`BuildFailureOutput` (same file) frames guard output verbatim:

```csharp
parts.Add("--- Guard check output ---\n" + guardOutput);
```

`SwivalSubagentRunner.BuildPrompt` (`src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs`)
surfaces it under a `## Failing verify output` section (the `invocation.LastTestOutput` branch).
There is **no prose** telling the agent *how* to fix a guard failure — only the raw guard lines.
So when a count-bearing line leaks through, the agent infers "shrink this file."

### The coding-stage system prompts — `src/VisualRelay.Core/Execution/RelayStages.cs`

`RelayStages.SystemPromptFor(string name)` is the **sole** producer of stage system-prompt text
(delivered via swival `--system-prompt` in `ProcessRunners.cs`; there is no append path). The
relevant arms today, verbatim:

- **Implement** (stage 6):
  ```
  "Implement the change within the manifest files. " +
  "Verify your changes using ONLY the targeted test command shown in the " +
  "## Verify command section of the prompt. Do NOT run the project's full " +
  "check, lint, or format gate (e.g. `./visual-relay check`) during " +
  "implementation — the harness runs the full gate at the Verify stage."
  ```
- **Fix** (stage 8):
  ```
  "Resolve every blocker and warning from review. " +
  "Verify your changes using ONLY the targeted test command shown in the " +
  "## Verify command section of the prompt. Do NOT run the project's full " +
  "check, lint, or format gate during implementation — the harness runs the " +
  "full gate at the Verify stage."
  ```
- **Fix-verify** (stage 10):
  ```
  "Fix failures from the pinned suite. Verify by running ONLY the command shown " +
  "in the ## Verify command section of the prompt — run exactly that one command " +
  "and nothing else — and confirm it passes (exit 0) before returning success. " +
  "Do NOT run the project's full check, lint, format, build, or screenshot gate " +
  "(e.g. `./visual-relay check`), and do NOT broaden the command to a fuller " +
  "gate — the harness runs the full gate at its own stage."
  ```

**Negative finding (confirmed by repo-wide grep over `src/**/*.cs`):** there is **no**
existing minimal-edit / no-reflow / line-budget / "keep files small" instruction in any
agent-facing prompt. Any such instruction is net-new.

### Existing guard tests to mirror — `tests/VisualRelay.Tests/RelayDriverRepoGuardTests.cs`

`RelayDriverRepoGuardTests` already covers the gate end-to-end with a `CommandDispatchTestRunner`
that routes `check-file-size.sh` vs `dotnet test` to separate `ScriptedTestRunner`s:

- `GuardRed_NewViolations_EntersFixVerifyWithOutput` (baselineVerify:false — every failure new)
- `GuardRed_PreExistingOnly_CommitsWithLedgerNote` (baselineVerify:true — working == baseline,
  asserts the ledger note)
- `NoGuardCmd_NoGuardInvocation`
- `GuardFixedInFixVerify_SealsGreen`

> **Note for the test author:** these scripted tests stub the guard with the string
> `ERROR: src/big.cs is 301 lines (limit: 300)`, which is **not** the real guard's wording
> (`file too large: ... has N lines (limit L)`). Tests pass because the driver treats guard
> output as opaque. The new regression test below MUST use the **real** message shape (or a
> shape where only the count differs between working and baseline) to exercise the count-drift
> bug — a stub with identical strings would not reproduce it.

There is no direct unit test of `OutputLineSet` / the baseline diff today; the behavior is only
covered through `RelayDriverRepoGuardTests`.

## What to build

Write failing tests first (TDD). All changes are in the VR harness; the guard stays a generic
line-count check (no toolchain specifics). Pick the cleanest of the two levers — **Lever A is
the load-bearing fix; Lever B is belt-and-suspenders** — but implementing both is recommended
and low-cost.

### Lever A (primary) — make the baseline guard diff count-insensitive

The goal: a pre-existing oversize file the task merely **touches** must NOT count as a new
violation just because its line count changed; only a file the task pushed **over** the
threshold (or a **new** oversize file) is new. Choose the cleaner of these two equivalent
implementations:

- **A1 (preferred — fix in the driver, guard-format-agnostic):** in
  `RelayDriver.RepoGuards.cs`, compare guard violation lines by a **count-normalized key**
  instead of the raw string. Add a small normalizer that, for each trimmed line, replaces
  runs of digits with a placeholder (e.g. `\d+` → `#`) before set membership — so
  `file too large: X has 332 lines (limit 300)` and `... has 333 lines ...` collapse to the
  same key and `ExceptWith` removes the pre-existing file. Apply the normalizer **only inside
  the baseline diff** (`OutputLineSet` / the `newLines.ExceptWith` step); keep `fullOutput`
  and the surfaced `newViolations` text as the **raw** lines so the ledger note and any
  genuinely-new violation still read naturally. This is general (any line-count-style guard
  benefits) and requires no change to the guard script. Document why digits are normalized
  (the count-drift footgun) in an XML-doc/code comment so it isn't "simplified" away.

  - Subtlety to preserve: a file pushed **over** the limit by the task (absent from baseline
    output entirely) must still be reported — normalization only collapses lines that have a
    **count-only-different twin** in the baseline; a file with no baseline twin at all is still
    new. Verify this holds: `ExceptWith` on normalized keys removes a working line only if some
    baseline line normalizes to the same key; a newly-oversize file has no such baseline line.

- **A2 (alternative — fix in the guard script):** make `check-file-size.sh` output
  **count-stable** by dropping the live count from the message, e.g.
  `file too large: <path> (over limit <L>)`. Then the existing exact-string diff already
  excludes touched-but-still-oversize files, because the line is identical in working and
  baseline. Smaller code change, but (i) loses the informative count in the operator-facing
  output and in any non-baseline (`baselineVerify:false`) consumer, and (ii) only helps this
  one guard, not line-count guards generally. **Prefer A1** unless the reviewer wants the
  guard message changed; if A2 is chosen, update README/AGENTS wording that quotes the old
  message and any test stub that asserts on `has N lines`.

Do not change the threshold, the enumeration, or the default `baselineVerify: true`. Do not
suppress genuinely-new violations.

### Lever B (belt-and-suspenders) — minimal-edit instruction in the coding-stage prompts

In `RelayStages.SystemPromptFor`, append one concise sentence to the **Implement**, **Fix**,
and **Fix-verify** arms instructing diff-scoped edits, e.g. (wording to taste, keep it short):

> "Make MINIMAL, diff-scoped edits: change only what the task requires and do NOT reformat,
> reflow, or compact unrelated code to satisfy size or style budgets."

Keep it additive (append to the existing string in each arm; don't restructure the switch).
This is cheap insurance against the agent shrinking files for other reasons (e.g. a future
guard, or self-imposed tidiness) and costs a handful of tokens per coding turn. If the reviewer
wants the absolute minimum change, ship Lever A alone and treat B as a follow-up — but A
without B still leaves the agent free to reflow for non-count reasons, so B is recommended.

### Safety notes

- **Don't weaken the guard.** A file the task pushes over the limit, or a new oversize file,
  must still turn stage 9 red and enter Fix-verify (covered by
  `GuardRed_NewViolations_EntersFixVerifyWithOutput` and the new test).
- **Keep raw output for humans.** Normalize only the **comparison key**, never the text shown
  in the ledger note or fed to the agent — operators/agents should still see the real count
  on a *genuine* new violation.
- **Generality.** The normalizer must not encode `.cs`/`.NET`/file-size specifics; digit
  normalization is a generic "ignore embedded counters" rule that helps any line-count guard.
  Don't hardcode the literal `file too large` string.
- **No behavior change when `baselineVerify:false`.** That path returns all output as new and
  must be untouched (the normalizer lives only inside the baseline branch).

### TDD test guidance (write first)

Add to `tests/VisualRelay.Tests/RelayDriverRepoGuardTests.cs` (mirror its
`CommandDispatchTestRunner` + `ScriptedTestRunner` setup, `InitGitRepo` for the stash
round-trip, `baselineVerify: true`):

1. **`GuardRed_PreExistingOversizeTouched_CountChanged_DoesNotBlock`** (the regression):
   working-tree guard returns `file too large: src/big.cs has 333 lines (limit 300)`;
   baseline guard returns `file too large: src/big.cs has 332 lines (limit 300)`
   (same file, count-only difference). Assert the task **Commits**, there is **no** stage-10
   invocation, and the ledger records the pre-existing note. **Without Lever A this FAILS**
   (the count-different line is treated as new → stage 9 red → stage-10 runs).

2. **`GuardRed_TaskPushedFileOverLimit_StillBlocks`** (guard remains meaningful): baseline
   guard returns **clean** (exit 0, file under limit pre-edit); working guard returns
   `file too large: src/touched.cs has 305 lines (limit 300)` (no baseline twin). Assert
   stage 9 goes **red** and a stage-10 invocation receives the violation — proving
   normalization does not swallow genuinely-new oversize files.

3. **`GuardRed_PreExistingOversize_PlusNewOversize_OnlyNewBlocks`** (optional, precision):
   baseline has `... big.cs has 332 ...`; working has both `... big.cs has 333 ...` (touched,
   pre-existing) and `... brand-new.cs has 350 ...` (new). Assert stage 9 red, and the
   stage-10 `LastTestOutput` contains `brand-new.cs` but **not** `big.cs`.

If A1 is implemented and the line-diff logic is extracted into a normalizer helper, add a
small direct unit test asserting two count-only-different lines collapse to one key while a
structurally-different line stays distinct.

For Lever B, add an assertion (or extend an existing `SystemPromptFor` test if one exists; if
not, a tiny new test) that `RelayStages.SystemPromptFor("Implement")` /
`"Fix"` / `"Fix-verify"` each contain the minimal-edit phrase (case-insensitive substring on a
stable keyword like `minimal` or `diff-scoped`), so the instruction can't be silently dropped.

## Done when

- A pre-existing oversize file that the task **only touches** (its line count shifting up or
  down) no longer turns stage 9 red and no longer enters Fix-verify; the ledger records the
  pre-existing-guard note instead. (New regression test #1 passes; previously failed.)
- A file the task pushes **over** the limit, or a **new** oversize file, **still** turns stage 9
  red and feeds the violation into the Fix-verify loop (tests #2/#3 + existing
  `GuardRed_NewViolations_EntersFixVerifyWithOutput` pass).
- The guard remains a generic line-count check with the default threshold and
  `baselineVerify: true` unchanged; `baselineVerify:false` behavior is unchanged.
- (Lever B, if shipped) Implement/Fix/Fix-verify system prompts each carry the minimal-edit /
  no-reflow instruction, asserted by test.
- `./visual-relay check` is green (format, file-size, InspectCode, build, full suite).
