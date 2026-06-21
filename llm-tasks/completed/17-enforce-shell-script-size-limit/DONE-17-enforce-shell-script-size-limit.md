# Enforce the shell-script size limit (flip the guard on at 20)

The advisory shell-size guard from **task 12** has been reporting offenders while tasks 13–16
(and the parallel `claim-authorship-strip-claude-trailers`) converted every chunky shell
script to thin wrappers + C#. The only shell left is now thin wrappers, the irreducible
`visual-relay` bootstrap, and the two intentionally-kept git hooks — all ≤ 20 logic lines.
This task **locks it in**: make the guard fail the build on any tracked shell script over the
limit.

This design is decided — implement exactly this, no alternatives:

- Enforcement is a **guard-as-test** in `VisualRelay.Tests` (the house idiom), so it runs in
  `./visual-relay test` and `./visual-relay check`. **No pre-commit hook, no CI job** — the
  same reasoning the InspectCode gate used
  (`llm-tasks/10-adopt-inspectcode-standards-repo-wide`): per-commit `dotnet` latency + SDK
  dependency aren't worth it; `check` is the gate.
- The limit is the single global `VISUAL_RELAY_SHELL_LINE_LIMIT` = **20** (already the task-12
  default). **No allowlist.** 20 is a ceiling, not a target — if a script is over, convert
  more of it to C#; changing the one global limit is a deliberate, separate decision, never a
  per-file carve-out.

> **Sequencing — do this LAST (task 6 of 6).** Depends on tasks 12–16 **and**
> `claim-authorship-strip-claude-trailers` (which thins `me.sh`) all being landed. If
> `./visual-relay guards` still lists any script when you start, finish that script's
> conversion task first — this task must land green, not by relaxing the limit.

## Current state (researched)

- **The guard exists** from task 12: `tools/VisualRelay.Guards` with pure
  `ShellScriptClassifier` / `ShellScriptLineCounter` / `ShellSizeGuard`, reporting via
  `./visual-relay guards`, currently exit-0 (advisory). `VISUAL_RELAY_SHELL_LINE_LIMIT`
  default 20.
- **The enforcing idiom to mirror** is `SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines`
  (`tests/VisualRelay.Tests/SplitGuardVerificationTests.cs:49-65`): enumerate, count, collect
  `violations`, `Assert.Empty(violations)` with a per-file message. Repo root is
  `RepoSetup.Root`; the test project `ProjectReference`s `tools/VisualRelay.Guards` to reuse
  the pure logic (same pattern as the existing `VisualRelay.DrainQueue` reference,
  `VisualRelay.Tests.csproj:44`).
- **By now the tracked shell set is small:** the `visual-relay` bootstrap (task 13, ≤ 20),
  the `test.sh`/packaging thin wrappers (13/16), `me.sh` (the claim task), and the two git
  hooks. The guard `.sh` files and `backend.sh` are gone (15/14).
- **The two git hooks stay as shell — do not convert them.** `.githooks/pre-commit` (~18
  logic lines) and `.githooks/commit-msg` (~8) already conform to the 20-line limit, and
  converting them would be a regression, not an improvement: git invokes them directly on
  every commit (a C# port adds a per-commit `dotnet` invocation for trivial logic), and
  `pre-commit` is deliberately dependency-free so it runs under the nono sandbox during a
  relay run (it avoids even `jq` — see its header comment and the launcher's sandbox model).
  They are the one category of shell that is *correct* as shell; the guard simply confirms
  they fit.

## What to build

TDD — the failing-then-passing test *is* the deliverable.

1. **Confirm the tree is clean:** run `./visual-relay guards`; it must list nothing at limit
   20. If it lists a script, fix it in its conversion task (or thin its wrapper) — do **not**
   raise the limit to hide logic.
2. **Add the enforcing guard-as-test** `ShellScriptSizeGuardTests` in `VisualRelay.Tests`:
   get the tracked file list (`git ls-files` via the same seam the tool uses) from
   `RepoSetup.Root`, run `ShellSizeGuard.FindViolations(..., limit)` with the limit read from
   the same default the tool uses (so the two never diverge), and `Assert.Empty` with a message
   listing each `path: N logic lines (limit 20)`.
3. **Flip the tool to non-advisory:** `tools/VisualRelay.Guards` `Program.cs` now exits
   non-zero when there are violations (remove the task-12 "always exit 0"). Keep
   `./visual-relay guards` as the ad-hoc runner; optionally add it as a fast pre-build step in
   `check`, but the test is authoritative.
4. **Verify the gate bites:** temporarily fatten a thin wrapper past 20 and confirm the test
   fails (and `./visual-relay guards` exits non-zero); revert.

## Done when

- `ShellScriptSizeGuardTests` fails when any tracked shell script exceeds 20 logic lines
  (proven by the temporary-fattening check) and passes on the converted tree.
- `tools/VisualRelay.Guards` exits non-zero on violations; `./visual-relay guards` reflects it.
- **No allowlist exists.** The single global `VISUAL_RELAY_SHELL_LINE_LIMIT` = 20 is the only
  knob.
- Every tracked shell file passes: the `visual-relay` bootstrap, `me.sh` (claim task), the
  `test.sh`/packaging wrappers, and the two git hooks.
- `./visual-relay check` is green; changed C# files < 300 lines; Conventional Commit subject
  e.g. `feat(guards): enforce the shell-script size limit (20) repo-wide`.
- Coordination: terminal task of the 12 → 17 series; must not land until 12–16 +
  `claim-authorship-strip-claude-trailers` are all in.
