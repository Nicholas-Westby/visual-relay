# Harden the command-guard middleware: a broken guard silently re-enables the bypass, and the shell-mode strip is mis-scoped

The just-shipped `command-guard-strip-hook-bypass` task (commit `60386d4`) added
`VisualRelay.CommandGuard`, wired as swival's `--command-middleware`, to strip git
hook-bypass flags (`--no-verify` / `-n`) so the commit-authority `.githooks/pre-commit`
re-engages during a run. A code-review pass over that code found five defects. The
headline one defeats the whole feature silently; two are scoping bugs in the shell-mode
strip that the unit suite doesn't cover; one is a required test that was never written;
the last is a low-severity over-strip. The squash backstop (`GitCommitter.Squash.cs`)
and the authority hook remain the floor, but the guard must not be able to silently
no-op the protection it was added to provide.

## Current state (researched)

### Defect 1 — HEADLINE: the guard can be silently INERT (fail-open re-enables the bypass)

The guard ships as a **framework-dependent** publish. `tools/VisualRelay.CommandGuard/VisualRelay.CommandGuard.csproj`
is a plain `Microsoft.NET.Sdk` `Exe` (`net10.0`, no `RuntimeIdentifier`, no
`SelfContained`), and `CommandGuardEnsurer.EnsureAsync` (`src/VisualRelay.Core/Execution/CommandGuardEnsurer.cs:49-59`)
publishes it with a bare `dotnet publish … -o command-guard/`. That produces a
framework-dependent apphost (`command-guard/VisualRelay.CommandGuard`) that needs a
discoverable .NET runtime — i.e. `DOTNET_ROOT` / the runtime install — to start.

Under the nono sandbox, swival's middleware subprocess does not necessarily inherit
`DOTNET_ROOT`. The review reproduced this: invoked under a bare environment the apphost
**exits 131 with EMPTY stdout** ("you must install .NET to run this application"
detection failing to reach a runtime). Per the original spec's own note
(`completed/command-guard-strip-hook-bypass/DONE-command-guard-strip-hook-bypass.md:43-46`),
swival treats ANY middleware failure — non-zero exit, empty/bad output — as **allow with
the ORIGINAL command**. So a `git commit --no-verify` sails straight through: the bypass
survives, **silently**, with no log and no rejection.

The wrapper does not catch this. `.githooks/command-guard:20-22` only falls back to
`dotnet run` when the published binary is **absent** (`if [[ -x "$PUBLISHED" ]]; then exec
"$PUBLISHED"; fi`). When the binary is present-but-broken it `exec`s it and is gone — the
`dotnet run` fallback at lines 25-27 is unreachable, and the final fail-open `printf
'{"action":"allow"}'` (line 30) is also unreachable. There is no path that detects an
empty/failed apphost result and recovers.

Net effect: on any host where the runtime isn't reachable from the sandboxed middleware,
the feature is a no-op and nobody is told.

### Defect 2 — shell-mode OVER-strip: `-n` wrongly removed from a later subcommand in a chain

`CommandGuardDecider.Shell.cs` (`StripShell`, lines 31-68) computes one `subIdx` /
`isCommit` for the whole token stream via `FindGitSubcommandIndex` (the first subcommand),
then strips `-n` for **every** token where `i >= subIdx.Value` (line 53) and `isCommit` is
true. In a chained command the scope leaks across the operator. Example:

    git commit -m x && git grep -n foo

`subIdx` points at `commit`, `isCommit` is true, and the `-n` belonging to `git grep`
(which is `--line-number`, not a bypass flag) sits at an index `>= subIdx`, so it is
stripped — silently corrupting the agent's `git grep` into `git grep foo`. The argv path
is not affected (argv is a single command), but the shell path is.

### Defect 3 — shell-mode UNDER-strip: `-n` survives unless `git` is the FIRST token

`DecideShell` (`CommandGuardDecider.Shell.cs:17-18`) bails to pass-through unless
`tokens[0].Text == "git"`. So a `git commit -n` that is **not** the first command in the
string is never inspected for the short-flag form. All of these pass `-n` through:

    echo hi; git commit -n -m x
    (git commit -n -m x)
    FOO=1 git commit -n -m x
    git grep foo && git commit -n -m x

(Note: `--no-verify` is matched **unconditionally** by `StripShell` regardless of position
— `tok.Text == "--no-verify"` at line 47 — so the long form is already caught in these
cases. This defect is specifically about the `-n` short form, which only fires inside an
`isCommit` segment that the current single-`subIdx` logic can't find past token 0.)

Fixing defect 2's operator-boundary scanning naturally fixes this too: once segments are
scanned independently, a `git commit` segment is detected wherever it appears, including
after `;`, `&&`, `(`, or an env-var prefix.

### Defect 4 — MISSING test: `CommandGuardEnsurer` has no coverage

The original spec explicitly required it
(`completed/command-guard-strip-hook-bypass/DONE-command-guard-strip-hook-bypass.md:110`:
"Ensure-step: publishes the binary to the expected path; a second call is a no-op when
current."). There is no `CommandGuardEnsurerTests` under `tests/VisualRelay.Tests/`
(the `CommandGuard/` test dir holds only the decider tests). The incremental no-op path
(`IsUpToDate`, `CommandGuardEnsurer.cs:102-120`) and the "binary published to the expected
path" contract are untested.

### Defect 5 — LOW: `--no-verify` stripped even as a value/filename

Both paths strip `--no-verify` unconditionally anywhere in a git argv: argv at
`CommandGuardDecider.cs:89-93` (`if (tok == "--no-verify")`) and shell at
`CommandGuardDecider.Shell.cs:47-51`. So `git commit -F --no-verify` (where `--no-verify`
is the message-file argument to `-F`) loses its filename. This is contrived and low-risk,
but the strip could be scoped to an **option position** the same way `-n` is, rather than
matching any token. Lower priority than 1-4 — note it, fix only if cheap.

## Goal

The command-guard can no longer silently fail open into the bypass it exists to block: a
broken/inert guard either still produces a correct decision (self-contained / wrapper
recovery) or fails CLOSED for `git commit` so the bypass cannot land. The shell-mode strip
is correctly scoped to the run of tokens that belong to an actual `git commit` — stripping
the bypass `-n` wherever a `git commit` segment appears in a chain (defect 3) while never
touching `-n` that belongs to a different subcommand (defect 2). `CommandGuardEnsurer`
gains the test the original spec required. The squash backstop and authority hook stay as
the floor and are unchanged.

## Approach (suggested)

### Defect 1 — make the guard impossible to silently no-op (pick a robust combination)

Any one of these closes the silent-bypass hole; combine for defense in depth. The result,
not the mechanism, is what matters.

- **(a) Publish self-contained.** Give the csproj a `RuntimeIdentifier` (or publish with
  `--self-contained -r <rid>`) so the apphost carries its own runtime and needs no
  `DOTNET_ROOT`. `CommandGuardEnsurer` must pass the matching publish flags and resolve
  the same output path. Downside: a larger gitignored `command-guard/` payload; verify the
  nono profile still permits the path.
- **(b) Wrapper detects a failed/empty apphost and recovers.** Instead of `exec`-ing the
  apphost (which replaces the shell and forfeits recovery), **capture** its stdout+exit; if
  the exit is non-zero OR stdout is empty/blank, fall back (`dotnet run`/`dotnet exec`, or
  re-run with `DOTNET_ROOT` exported). Only `exec`/print the result once a usable decision
  exists. Keep the wrapper ≤20 logic lines (shell-size gate) — push any real logic into a
  helper if needed, or keep it terse.
- **(c) Fail CLOSED for git commit specifically.** This is the safety net the original
  spec floated (`DONE-command-guard-strip-hook-bypass.md:135-137`). If the guard cannot
  produce a decision for a command that is a `git commit`, emit `{"action":"deny", …}`
  (with a steering reason) rather than allowing the original — so a broken guard blocks the
  commit instead of enabling the bypass. Non-git / non-commit commands still fail OPEN so a
  broken guard never wedges the agent's other work. Note the protocol: swival's deny
  carries an agent-visible `reason` (`DONE-…:24-27`), and `CommandGuardResult` currently
  models only allow/allow-rewrite (`src/VisualRelay.Core/CommandGuard/CommandGuardResult.cs`)
  — a deny verdict + its serialization in `tools/VisualRelay.CommandGuard/Program.cs:25-39`
  would need wiring if (c) is taken in C# rather than purely in the wrapper.

Recommended: **(a) + (c)** — self-contained removes the common failure mode, and
fail-closed-for-commit guarantees that even an unforeseen guard failure can never re-enable
the bypass. (b) is a fine alternative/addition if self-contained is undesirable.

### Defects 2 & 3 — operator-boundary segment scanning in shell mode

Rework `StripShell` (and the helper it shares, `FindGitSubcommandIndex` in
`CommandGuardDecider.Helpers.cs`) so it does not compute a single whole-stream `subIdx`.
Instead, walk the token list splitting at shell control operators — `&&`, `||`, `;`, `|`,
newline, and subshell `(` / `)` — and treat each segment independently:

- Recognize these operators as their own tokens (the tokenizer in
  `CommandGuardDecider.Shell.cs:125-179` currently splits only on whitespace/quotes, so
  `&&`/`;`/`|`/`(` may be glued to neighbors, e.g. `x&&git`; the segment scan must detect
  operator boundaries inside tokens or the tokenizer must emit them separately — handle
  both whitespace-separated and glued forms).
- Skip a leading **env-var assignment prefix** (`FOO=1 git commit …`) when deciding
  whether a segment's command is `git` (defect 3).
- Within a segment whose command is `git` and whose subcommand is `commit`, strip the
  bypass `-n` / combined `-n…` exactly as today, but **only** for tokens in that segment.
- `--no-verify` continues to be stripped in any `git` segment (keep current behavior,
  subject to defect 5's optional scoping).

Drop the `tokens[0].Text == "git"` early-out in `DecideShell` so segments anywhere in the
chain are considered; pass-through only when no segment yields an edit. Keep the existing
byte-exact surgical removal (the edit-list + reverse-sort splice in `StripShell:70-97`) so
spacing/quoting outside the removed flag is preserved.

### Defect 5 — optional: scope `--no-verify` to an option position

If cheap, only strip `--no-verify` when it sits where an option (not a value) is expected —
i.e. not immediately after a value-taking flag like `-F`/`--file`/`-m`/`--message`. Apply
identically to argv (`CommandGuardDecider.cs`) and shell. Skip if it complicates the
segment rework; note it as deferred rather than forcing it.

## Files

- `tools/VisualRelay.CommandGuard/VisualRelay.CommandGuard.csproj` — self-contained /
  RID publish settings (if approach (a)).
- `src/VisualRelay.Core/Execution/CommandGuardEnsurer.cs` — matching publish flags +
  output-path resolution; keep the incremental no-op.
- `.githooks/command-guard` — failed/empty-result detection + fallback, and/or
  fail-closed-for-commit (if approach (b)/(c) in the wrapper); stay ≤20 logic lines.
- `src/VisualRelay.Core/CommandGuard/CommandGuardDecider.Shell.cs` — operator-boundary
  segment scanning (defects 2 & 3).
- `src/VisualRelay.Core/CommandGuard/CommandGuardDecider.Helpers.cs` — segment/subcommand
  helpers; env-prefix skip.
- `src/VisualRelay.Core/CommandGuard/CommandGuardDecider.cs` — defect 5 (optional);
  shared logic if argv/shell converge on a segment helper.
- `src/VisualRelay.Core/CommandGuard/CommandGuardResult.cs` +
  `tools/VisualRelay.CommandGuard/Program.cs` — only if fail-closed deny is modeled in C#
  (approach (c)).
- `packaging/nono/vr-guard.json` — confirm the `command-guard/` path (and a larger
  self-contained payload) is still granted.
- tests (below).
- Split any file approaching 300 lines.

## Tests (TDD — write the failing tests first)

Strict TDD: each test must fail against the current tree, then pass after the fix.

### Shell-mode scoping (defects 2 & 3) — add to `CommandGuardDeciderTests.Shell.cs`

Over-strip (must NOT touch the second subcommand's `-n`):

- `git commit -m x && git grep -n foo` → `git commit -m x && git grep -n foo` (the `-n`
  on `git grep` is PRESERVED; if the whole string is otherwise unchanged, the verdict is
  pass-through).
- `git commit -m x; git log -n 5` → `git log -n 5` unchanged.
- `git commit -n -m x && git grep -n foo` → strips the commit's `-n`, keeps grep's `-n`:
  `git commit -m x && git grep -n foo`.

Under-strip (must NOW strip the commit's `-n` wherever it appears):

- `echo hi; git commit -n -m x` → `echo hi; git commit -m x`.
- `(git commit -n -m x)` → `(git commit -m x)`.
- `FOO=1 git commit -n -m x` → `FOO=1 git commit -m x`.
- `git grep foo && git commit -n -m x` → `git grep foo && git commit -m x`.
- Glued operator form, e.g. `true&&git commit -n -m x` → commit's `-n` stripped, byte-exact
  elsewhere.

Regression (already-green cases stay green): the existing `Shell_*` facts for
`git push -n` / `git merge -n` kept, `grep -n` / `sort -n` / `echo -n` untouched, combined
`-nm`/`-nmf`, `--no-verify` long-form, byte-exact spacing — all must still pass.

### `CommandGuardEnsurer` test (defect 4) — new `tests/VisualRelay.Tests/CommandGuard/CommandGuardEnsurerTests.cs`

- `EnsureAsync_PublishesBinaryToExpectedPath` — point at a temp repo root containing the
  `tools/VisualRelay.CommandGuard` source (or a fixture / scoped invocation); assert the
  returned path is `<root>/command-guard/VisualRelay.CommandGuard` and the file exists.
- `EnsureAsync_SecondCall_IsNoOpWhenCurrent` — after a first publish, a second call with no
  source change returns the same path WITHOUT re-publishing (assert via the `IsUpToDate`
  fast-path: e.g. binary mtime unchanged, or inject/observe that `dotnet publish` was not
  invoked the second time). Keep it hermetic and reasonably fast; if a real `dotnet publish`
  is too heavy for the suite, drive `IsUpToDate` / the path-resolution seam directly and
  document the publish itself as covered by the live smoke below.

### Fail-closed / inert-guard test (defect 1) — strongly preferred

- A test proving a broken/inert guard does NOT re-enable the bypass for a git commit.
  Depending on the chosen approach: assert the wrapper, given a simulated failed/empty
  apphost result, falls back or emits a `deny` for a `git commit` payload (and still `allow`
  for a non-git command); or, if modeled in C#, a `Decide`-level test that an unproducible
  decision for `git commit` yields deny while other commands yield allow. At minimum, assert
  the wrapper's recovery path is reachable when the binary is present-but-failing (the bug
  today is that it is not).

## Sequencing

Self-contained; complements (does not replace) the squash backstop and the authority hook.
A **live `run-task` smoke test is REQUIRED** before "done": the pipeline mocks the process
layer, so unit-green does not prove the middleware actually starts under nono. Run a real
`./visual-relay run-task <repo> <task>` and confirm (a) the command-guard middleware
launches inside the sandbox (the apphost is no longer inert — no exit-131/empty-stdout), and
(b) an agent's `git commit --no-verify` / `-n` on the project repo during the run is BLOCKED
end-to-end (hook rejection, or fail-closed deny), while a scratch repo's commit still works.
This is the only check that exercises the exact failure defect 1 describes.

## Done when

- Defect 1 closed: the guard cannot silently fail open into the bypass — it is
  self-contained and/or the wrapper recovers from a failed/empty apphost and/or it fails
  CLOSED for `git commit`; a broken guard can never re-enable `--no-verify`/`-n` on the
  project repo. Proven by the inert-guard test and the live smoke.
- Defect 2 fixed: `-n` belonging to a non-commit git subcommand later in a chain is
  preserved; the over-strip facts pass.
- Defect 3 fixed: a `git commit -n` segment anywhere in a chain (after `;`/`&&`/`(`, or an
  env-var prefix) has its bypass `-n` stripped; the under-strip facts pass.
- Defect 4 fixed: `CommandGuardEnsurerTests` exists and asserts publish-to-expected-path
  and the no-op-when-current behavior.
- Defect 5 addressed or explicitly deferred with a note.
- All prior `CommandGuard` tests still green (no regressions).
- Live `run-task` smoke confirms the middleware starts under nono and the bypass is blocked
  end-to-end.
- `./visual-relay check` green; files under 300 lines; Conventional Commit subjects.
