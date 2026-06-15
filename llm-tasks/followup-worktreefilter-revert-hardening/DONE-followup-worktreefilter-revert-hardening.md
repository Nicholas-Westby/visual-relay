# Harness follow-up: fix data-loss defects the stage-5 revert rewrite introduced

An adversarial review of commit `76e60a5` (the `followup-stage5-discard-dataloss` fix) found that while it
closed its 6 target defects, the **rewrite of the tracked-revert from a single batched `git checkout` into a
per-path loop introduced NEW data loss** — at least one CRITICAL, permanent, and verified-live. The stage-5
`WorktreeFilter` DELETES untracked files and REVERTS tracked files; it must be conservative. Every defect
below was reproduced by the reviewer in throwaway git repos.

**TDD is mandatory here.** Round 1 shipped these regressions precisely because its tests only covered the
*happy* direction of each case. For THIS task, write a FAILING test for every dangerous branch listed under
"Tests" FIRST (stage 5), then implement. A fix without the matching red-first test does not count as done.

## Current state (researched)

> **Freshness contract:** Locate every anchor by searching for the quoted snippet — never by line number. If a
> snippet no longer exists, treat this as stale and re-verify against the current `WorktreeFilter.cs` before building.

All anchors are in `src/VisualRelay.Core/Execution/WorktreeFilter.cs` unless noted. Caller is
`src/VisualRelay.Core/Execution/RelayDriver.Stage5.cs` (`HandleStage5Async`).

### Defect A — CRITICAL, verified — staged rename whose endpoint is a testFile → permanent loss
Staged renames are parsed by `AddNameStatusLines`, which adds **both** names:
```csharp
// Rename: capture both old and new names.
var oldName = parts[1].Trim();
var newName = parts[2].Trim();
```
Then the non-test filter excludes only the testFile endpoint:
```csharp
var nonTestTracked = dirtyTracked
    .Where(p => !testSet.Contains(NormalizeRepoRelativePath(p))
```
So for `git mv b.txt c.txt` with `b.txt` a declared testFile: `b.txt` is excluded (it's a test) but `c.txt`
is NOT, so `nonTestTracked=[c.txt]`. The revert loop's `git checkout HEAD -- c.txt` fails (c.txt absent from
HEAD), so it runs `git rm --cached c.txt` + `File.Delete(c.txt)`. **Nothing restores `b.txt`**, which `git mv`
already removed → final state `D b.txt`, file gone. This is WORSE than round-0 (the batched checkout failed
and left the rename intact/recoverable). The mirror case `git mv prod.cs my.Tests.cs` (dest is the testFile)
leaves `my.Tests.cs` staged+on-disk next to a restored `prod.cs` — a polluted index for stage 6.

### Defect B — HIGH, verified — transient/timeout checkout misread as "absent from HEAD" → deletes a real HEAD file
The revert loop treats ANY failure as proof the path isn't committed:
```csharp
if (checkoutResult.ExitCode != 0 || checkoutResult.TimedOut)
{
    // Path absent from HEAD (staged rename destination, new staged file).  Unstage and delete.
    var rmResult = await GitAsync(rootPath, ["rm", "--cached", "--", rel], cancellationToken);
    ...
    if (File.Exists(fullPath)) File.Delete(fullPath);
}
```
A `git checkout` that fails for a TRANSIENT reason — `index.lock` race, a virtio-fs `EIO`/`EMFILE` on the host,
or a watchdog `TimedOut` — is misclassified as "absent from HEAD", so a **committed production file is deleted**.
Worse, `git rm --cached` then succeeds (exit 0), so `revertErrors` stays empty → **no Error surfaced, run proceeds.**

### Defect C — HIGH, verified — `core.quotePath` (default true) breaks non-ASCII / space / control-char paths
None of the enumeration calls disable path-quoting:
```csharp
var unstagedDiff = await GitAsync(rootPath, ["diff", "--name-only"], cancellationToken);
... ["diff", "--cached", "--name-status", "-M"] ...
... ["ls-files", "--deleted"] ...
... ["ls-files", "--others", "--exclude-standard"] ...
```
With `core.quotePath=true`, a file `café.txt` is emitted as the literal token `"caf\303\251.txt"` (octal +
surrounding quotes). Untracked path: `Path.Combine(root, "\"caf\303\251.txt\"")` is a nonexistent path →
`File.Delete` no-ops → the real junk file **silently leaks into stage 6**. Tracked path: checkout AND rm both
fail on the quoted token → un-reverted production edit PLUS a spurious revert-failure flag. The quoted token
also never equals the real path, defeating the testSet/artifact/tasks-dir guards.

### Defect D — HIGH, verified — blanket `OrdinalIgnoreCase` over-preserves on case-sensitive Linux
```csharp
var testSet = new HashSet<string>(
    testFiles.Select(NormalizeRepoRelativePath),
    StringComparer.OrdinalIgnoreCase);
```
Applied on ALL hosts. On case-sensitive Linux (a real VR run target — keep VR platform-agnostic), a dirty
production file `src/widget.cs` case-folds onto a declared test `tests/Widget.cs`… more concretely, any
production path that differs from a testFile only by case is excluded from revert → the agent's production
edit survives into the red-gate, defeating the filter. Also inconsistent with the `Distinct(StringComparer.Ordinal)`
two lines below and with `IsInternalArtifact` (Ordinal).

### Defect E — MEDIUM, verified — `git rm --cached` on an absent path exits 128 → spurious flag
In the failure branch, `git rm --cached -- <rel>` on a path already unstaged (or never staged) returns
`fatal: pathspec ... did not match`, exit 128 → folded into `revertErrors` → Stage5 flags a correctly-cleaned tree.

### Defect F — MEDIUM — `File.Delete` calls bypass the new Error channel; error path half-mutates
Both deletes are unwrapped:
```csharp
if (File.Exists(fullPath)) File.Delete(fullPath);   // in the revert loop
...
if (File.Exists(full)) File.Delete(full);           // step 4, untracked
```
A locked/read-only/raced file throws (`IOException`/`UnauthorizedAccessException`), unwinding past the new
`WorktreeFilterResult.Error` to the generic `RelayDriver` catch-all (flagged as a stage-0 `exception:`, not the
stage-5 worktree-filter failure). And the revert-error early-return happens AFTER some paths were already
reverted/deleted and BEFORE step-4 untracked deletion runs at all → the tree is left half-filtered.

## What to build

Keep the method contract and happy-path behavior; make the destructive operations conservative and correct.
**Stay toolchain- and platform-agnostic.** Priorities (fix in this order):

1. **Defect A — never destroy a rename endpoint that is a testFile.** When a parsed rename's OLD **or** NEW
   name normalizes into `testSet`, treat the whole rename as test-related: exclude BOTH endpoints from
   `nonTestTracked` (leave the rename intact). Only when NEITHER endpoint is a testFile may the rename be
   reverted — and then restore the OLD name (`git checkout HEAD -- <old>`) and unstage/delete the NEW name, so
   no content is lost.
2. **Defect B — only delete on POSITIVE confirmation the path is not in HEAD.** On checkout failure, probe with
   `git cat-file -e HEAD:<rel>`: exit ≠ 0 (truly absent) → safe to unstage+delete; exit 0 (path IS in HEAD →
   checkout failed for another reason) → record a revert error, do NOT delete. Treat `checkoutResult.TimedOut`
   (and a timed-out probe) as a hard Error — never delete on timeout.
3. **Defect C — disable path quoting.** Pass `-z` (NUL-delimited, never quoted) to all four enumeration calls and
   split on `\0`, OR add `-c core.quotePath=false`. If you use `--name-status -z`, note its format: each entry is
   `status\0path\0`, a rename is `Rnn\0old\0new\0`. Decode before any `Contains`/`Path.Combine`/git use.
4. **Defect D — host-gate the comparer.** Use `OrdinalIgnoreCase` only on case-insensitive hosts
   (`OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()`), else `Ordinal`; use the SAME comparer for the
   `Distinct(...)` calls. Normalization (the `\`/`./`/`+` handling) is correct and stays for all hosts.
5. **Defects E & F — no false flags, no half-mutation, route I/O errors through Error.** Pass `--ignore-unmatch`
   to `git rm --cached` (or treat exit 128 / "did not match" as benign). Wrap both `File.Delete` calls in
   try/catch and fold failures into the error list. Do NOT early-return mid-filter: complete BOTH the tracked
   and untracked phases (accumulating errors), THEN return `Error` if any — so the tree is fully filtered or
   fully reported, never half. `TrackedDiscarded` must list only paths actually reverted/removed.

`HandleStage5Async` must still flag on a non-null `Error`, and should record the ledger inventory of what was
discarded even on the error path (don't skip the note on early return).

NOTE (out of scope here — file as a separate follow-up if confirmed): the raw `+`-prefixed `testFiles` entry is
still not stripped downstream of WorktreeFilter — in the manifest merge / `hasImpl` check in `Stage5.cs` and in
`RedGate.ComputeStripSet` — so a `+tests/Foo.cs` entry can still poison the manifest/red-gate even though the
file is now preserved on disk. Do not fix that here; keep this task to the WorktreeFilter revert/delete safety.

## Tests (write each as a FAILING test FIRST, in `WorktreeFilterTests.*`)

- **Rename source is a testFile**: `git mv b.txt c.txt`, `testFiles=[b.txt]` → after filter, the test content
  survives (no permanent loss); assert the file at the rename's new name exists and `b.txt` is not left as a
  dangling deletion.
- **Rename dest is a testFile**: `git mv prod.cs my.Tests.cs`, `testFiles=[my.Tests.cs]` → no dirty/duplicate
  index pollution; the rename is left intact.
- **Rename, neither endpoint a testFile** (regression-guard the legit revert): both endpoints fully reverted,
  no content lost.
- **Transient checkout failure on an in-HEAD path** → the file is NOT deleted and an `Error` is returned (inject
  a failure, or assert via a path that is in HEAD but checkout is made to fail; at minimum assert the
  `cat-file -e` gate prevents deletion of an in-HEAD file).
- **Non-ASCII / spaced path** (`"café file.txt"`), tracked and untracked → tracked reverted, untracked deleted,
  and a non-ASCII **testFile preserved** (quotePath handled).
- **Case-sensitive vs insensitive comparer**: a production path differing only by case from a testFile is
  reverted on a case-sensitive host (gate or abstract the host check so the test is deterministic in CI).
- **`git rm --cached` on an absent path** → no spurious flag (clean tree stays unflagged).
- **`File.Delete` throws** (e.g. point at a read-only/locked path or inject) → folded into `Error`, not a
  generic stage-0 exception; assert no half-mutation invariant where practical.

## Done when
- Defects A–F are fixed; each has a red-first test that now passes.
- The existing `WorktreeFilter`/`RelayDriverStage5` tests stay green and the full suite passes; no sleeps/long timeouts.
- No platform-specific behavior is hard-coded beyond the host-gated case comparer; VR stays general.
