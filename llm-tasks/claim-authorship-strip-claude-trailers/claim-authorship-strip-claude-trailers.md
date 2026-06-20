# Claim authorship: also strip Claude trailers (port `me.sh` to C#)

`me.sh` rewrites the last N commits so their author/committer become the current git user
(preserving author dates, idempotent). It does **not** touch the commit *message*. We want it to
also remove any **trailer that mentions "Claude"** so an authored history carries no machine
attribution. Two real examples to strip:

```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01G8FrQ7mD2TzsgXbofvnu74
```

This design is decided — implement exactly this:

- The whole claim+strip algorithm moves into **testable C# in `VisualRelay.Core`**, exercised by a
  new `tools/VisualRelay.ClaimAuthorship` entrypoint. `me.sh` becomes a **thin wrapper** that `exec`s
  the tool (the direction the in-flight "thin `.sh` wrappers" task is taking — see the coordination
  section at the bottom; this task already thins `me.sh`, so that task must not double-convert it).
- The match rule is **any trailer whose key or value contains "claude" (case-insensitive)**. This
  covers both examples and future variants (`Claude-*:` keys, `claude.ai` / `anthropic` URLs that sit
  next to the word "Claude"). Human `Co-Authored-By:` lines and prose are left alone (see below).
- The current `me.sh` behavior contract is **preserved exactly**: default 5 / `-N`, `--root` fallback,
  `CLAIM_EMAIL` / `CLAIM_NAME` override, author-date preservation, hook bypass, empty commits allowed,
  idempotent re-runs.

## Current state (researched)

- **`me.sh` is a standalone bash tool, no tests, no callers.** The only references to it in the repo
  are its own header/usage (`me.sh:3,17,33,65`); nothing in `src/`, `tools/`, or `tests/` invokes it.
  It is run manually from a repo's working directory and operates on that repo.
  - The per-commit work is a single `git rebase … --exec` one-liner (`me.sh:116`): for each walked
    commit, if author email ≠ claim → `git commit --amend --no-verify --allow-empty --reset-author
    --date=<orig author date> --no-edit`; elif committer ≠ claim → same amend without `--reset-author`;
    else skip. `--no-edit` keeps the message — this is exactly what we are extending.
  - Identity resolution (`me.sh:63-82`): `CLAIM_EMAIL`/`CLAIM_NAME` env override (email **must contain
    `@`**, else exit 64; name defaults to the local-part of the email) and, in that branch, exports
    `GIT_AUTHOR_*` + `GIT_COMMITTER_*` so the amend uses them; otherwise `claim_email` comes from
    `git var GIT_AUTHOR_IDENT` ("Name <email> ts tz") and the amend uses the repo's configured
    identity. `CLAIM_EMAIL` is always exported for the per-commit comparison (`me.sh:87`).
  - Range/usage (`me.sh:34-47`): `claim_count` default 5, `-N` validated against `^-[1-9][0-9]*$`,
    bad usage → exit 64. `--root` fallback when `HEAD~N` does not resolve (`me.sh:53-57`).
  - Flag rationale (`me.sh:104-112`): `--date` preserves author date; `--allow-empty` tolerates probe
    commits; `--no-verify` skips hooks because the rewrite is metadata-only by design.
- **Git seam to reuse.** `GitInvoker : IGitInvoker` (`src/VisualRelay.Core/Execution/GitInvoker.cs:12`)
  pins a stable git binary and sanitizes env; `RunAsync(rootPath, arguments, ct, timeout?, environment?,
  …)` (`:38`) runs `git -C rootPath <args>` with the current env plus a caller overlay. Route **every**
  git call through it; never shell out to git directly.
- **CLI tool convention.** Tools live at `tools/VisualRelay.<Name>/` with a top-level-statements
  `Program.cs`, `OutputType=Exe`, `net10.0`, `ProjectReference` to `VisualRelay.Core` (RunTask also
  references Domain) — see `tools/VisualRelay.RunTask/Program.cs` (arg parse → usage to stderr →
  numeric exit code) and `tools/VisualRelay.RunTask/VisualRelay.RunTask.csproj`. Register the new
  project in `VisualRelay.slnx`.
- **Thin-wrapper precedent.** The `visual-relay` launcher does `_require_dotnet` (`visual-relay:122`)
  then `dotnet run --project "$SCRIPT_DIR/src/.../*.csproj" -- "$@"` (`visual-relay:499`), with a
  published-exe fast path (`:494-496`). `dotnet run` does **not** change the working directory, so a
  thin `me.sh` that execs the tool still operates on the **caller's** repo.
- **`.gitignore` already ignores `.relay-scratch/`** (alongside `TestResults/`, `artifacts/`). The
  integration test can build its throwaway git repo under there (or under the OS temp dir) so it is
  never tracked. No new ignore entry is required if `.relay-scratch/` is used.
- **Conventional Commit subjects look like trailers.** Subjects in this repo are `feat(x): …` /
  `fix(sandbox): …` — i.e. `key: value`-shaped. The stripper MUST NOT treat the subject (or any line
  that is not in the trailing trailer block) as a trailer. See the trailer-block rule below.

## What to build

TDD — write the failing test(s) first for each unit.

1. **`ClaudeTrailerStripper.Strip(string message) → string`** (pure, no I/O; new
   `src/VisualRelay.Core/Authorship/`). Unit-test this first.
   - **Trailer-block detection (git semantics):** consider only the **last paragraph** of the message
     — the final run of non-blank lines that is **preceded by a blank line** (so a single-line or
     no-blank-line message has *no* trailer block, protecting Conventional Commit subjects). Treat that
     paragraph as a trailer block only if its lines are trailers: a line matching
     `^[A-Za-z0-9][A-Za-z0-9-]*:\s` starts a trailer; a line beginning with whitespace is a **folded
     continuation** of the trailer above it.
   - **Removal rule:** drop a trailer (its key line **and** any folded continuation lines) when the
     trailer's **key OR value contains "claude"** compared case-insensitively
     (`StringComparison.OrdinalIgnoreCase`). Keep all other trailers in their original order and exact
     text.
   - **Tidy:** if removals empty the trailer block, drop the now-trailing blank separator line(s); the
     result ends with a single trailing newline. The subject, body, and surviving trailers are
     byte-preserved.
   - **Intended consequence (document in an XML doc comment):** a non-Claude trailer whose value merely
     contains "claude" (e.g. `Reviewed-by: claude-fan <x@y>`) IS removed — this is "any trailer
     mentioning Claude", per the spec. Prose in the body that mentions Claude is NOT removed (it is not
     in the trailer block).
   - **Unit cases (must fail against today's absent code, then pass):** both examples removed;
     `Claude-Session:` and a `co-authored-by: Claude …` removed case-insensitively; human
     `Co-Authored-By: Jane <jane@…>` kept; mixed block keeps non-Claude trailers with spacing intact;
     a folded multi-line trailer value mentioning Claude removed whole; `feat(x): y` subject-only
     message unchanged; `subject\n\nClaude-Session: …` → `subject` (trailer + trailing blank gone);
     body paragraph mentioning "claude" kept; running the cleaned output through `Strip` again is a
     no-op (idempotent).

2. **`AuthorshipClaimer`** (new, in `Authorship/`, takes `IGitInvoker`) — orchestrates the rewrite
   in-process so it is fully testable against a real temp repo. Method shape e.g.
   `Task<ClaimOutcome> ClaimAsync(string repoRoot, int claimCount, string? claimEmail, string? claimName, CancellationToken ct)`.
   - **Identity:** if `claimEmail` is set, validate it contains `@` (else a usage error surfaced as
     exit 64 by the tool) and default `claimName` to the local-part; otherwise parse
     `git var GIT_AUTHOR_IDENT` → "Name <email> …" for **both** name and email. The resolved
     `(claimName, claimEmail)` is the target author **and** committer identity.
   - **Range:** `upstream = HEAD~claimCount` if `git rev-parse --verify HEAD~claimCount` succeeds, else
     treat as root (whole branch). Reject (fail fast, clear message) if the range contains a **merge
     commit** — out of scope, matching `me.sh`'s linear-history assumption. Require a clean working
     tree (surface a clear error if dirty).
   - **Rewrite (in-process replay via `GitInvoker`, no `git rebase --exec` self-callback):** list the
     range oldest→newest with `git rev-list --reverse` plus per-commit `%T %P %an %ae %aI %cn %ce %B`.
     A commit *needs change* iff author email ≠ claim, OR committer email ≠ claim, OR
     `Strip(message) ≠ message`. **Idempotency:** if **no** commit in range needs change, do nothing
     (no ref move). Otherwise find the oldest commit that needs change; everything before it stays
     untouched (sha-stable) and is the stable base; rebuild from that commit forward with
     `git commit-tree <tree> [-p <newParent>] -F <cleaned-message-tempfile>`, where `newParent` is the
     rewritten sha of the prior commit (or the stable original parent / none for a root commit). For
     each rebuilt commit set, via the git process environment: `GIT_AUTHOR_NAME/EMAIL` = original
     author when it already equals the claim, else the claim; `GIT_AUTHOR_DATE` = original author date
     (always preserved); `GIT_COMMITTER_NAME/EMAIL` = the claim. (`git commit-tree` runs no hooks and
     needs no `--allow-empty`, covering the `--no-verify` / `--allow-empty` intent.) Finally move the
     branch to the new tip: `git update-ref refs/heads/<branch> <newTip> <oldTip>` (resolve the branch
     via `git symbolic-ref --short HEAD`; update `HEAD` directly if detached). The new tip's tree equals
     the old tip's tree, so the working tree and index are untouched (no checkout needed).
   - **Invariants the design guarantees (assert these in tests):** (1) every commit in range ends with
     author email = committer email = claim email; (2) every author date is unchanged; (3) every
     Claude trailer is gone and all other content (subject, body, non-Claude trailers, human
     co-authors) is preserved; (4) a fully-claimed, Claude-trailer-free range is left byte-identical
     (no-op); (5) the `me.sh` contract (default 5 / `-N` / `--root` / `CLAIM_EMAIL`/`CLAIM_NAME`) holds.

3. **`tools/VisualRelay.ClaimAuthorship`** (new project; mirror `VisualRelay.RunTask`'s csproj and
   register in `VisualRelay.slnx`). `Program.cs`: parse optional `-N` (validate `^-[1-9][0-9]*$`,
   default 5, bad usage → `usage:` to stderr + **exit 64**); read `CLAIM_EMAIL` / `CLAIM_NAME` from the
   environment; resolve the repo from the **current working directory**; construct `GitInvoker` and
   `AuthorshipClaimer` and run; exit 0 on success, non-zero with a clear stderr message on failure
   (dirty tree, merge in range, invalid `CLAIM_EMAIL`, etc.).

4. **`me.sh` → thin wrapper.** Replace the body with a forwarder that keeps the shebang and
   `set -euo pipefail`, resolves its own directory only to locate the project, and `exec`s the tool on
   the caller's CWD repo, mirroring the launcher convention, e.g.:
   `exec dotnet run --project "$SCRIPT_DIR/tools/VisualRelay.ClaimAuthorship/VisualRelay.ClaimAuthorship.csproj" -- "$@"`
   (a published-exe fast path like `visual-relay:494-496` is optional and may be left to the thin-`.sh`
   task). Remove all bash rebase/identity logic. Keep a short header comment that documents the
   `me.sh [-N]` + `CLAIM_EMAIL`/`CLAIM_NAME` contract and points at the C# home. Forward args/env
   verbatim (`"$@"`); arg/`@`-validation now lives in the tool.

5. **Tests** (`tests/VisualRelay.Tests`): the `ClaudeTrailerStripper` unit cases from step 1, **plus**
   an integration test for `AuthorshipClaimer` that builds a throwaway git repo under a git-ignored
   scratch dir (e.g. `.relay-scratch/claim-authorship-tests/<unique>`, already ignored; or the OS temp
   dir) and cleans it up. It seeds commits with a **foreign author/committer** (e.g. "Managed via Tart")
   and a mix of trailers — Claude `Co-Authored-By:`, `Claude-Session:`, a **human** `Co-Authored-By:`,
   and a non-Claude trailer — then runs the claimer and asserts all five invariants above, including a
   **second run is a no-op** (HEAD sha unchanged). Use `GitInvoker` against the temp repo (real git).

## Done when

- `ClaudeTrailerStripper` unit tests and the `AuthorshipClaimer` temp-repo integration test pass and
  **fail against today's code** (today nothing strips trailers and `me.sh` is bash).
- All five invariants are demonstrated by tests; the second-run no-op (idempotency) is explicitly
  asserted.
- `me.sh` is a thin wrapper containing no rebase/identity logic; running `./me.sh [-N]` on a repo with
  foreign-authored commits carrying Claude trailers claims authorship **and** removes the trailers,
  with the `-N` / `--root` / `CLAIM_EMAIL` / `CLAIM_NAME` / exit-64 behavior unchanged from today.
- `tools/VisualRelay.ClaimAuthorship` builds and is registered in `VisualRelay.slnx`.
- **Manual spot-check (note it explicitly if not automated):** in a throwaway git repo, run `me.sh`
  against commits with assorted real-world trailer shapes (folded values, mixed human/Claude
  co-authors, a Conventional Commit subject with a colon, an already-claimed commit) and confirm the
  outcome by eye — broader than the automated cases.
- `./visual-relay check` is green (build + format + tests).
- Conventional Commit subject, e.g. `feat(authorship): strip Claude trailers and port me.sh claim
  logic to C#`. Flag in the commit body: `me.sh` is now a thin wrapper over
  `VisualRelay.ClaimAuthorship`; the match rule is "any trailer mentioning Claude" (so a non-Claude
  trailer whose value contains "claude" is intentionally removed); coordination with the in-flight
  thin-`.sh` task (below).

## Coordinate with the in-flight "thin `.sh` wrappers" task

A separate task that converts the repo's `.sh` files into thin wrappers calling out to C# is being
written and may not be committed yet (it may arrive from the VM side — the repo is shared between the
host and the VM). Coordination, baked here because the implementer sees only one task at a time:

- **This task already converts `me.sh` into a thin wrapper.** The thin-`.sh` task must **not**
  double-convert `me.sh`; treat `me.sh` as done.
- If the thin-`.sh` task defines a canonical entrypoint convention (a shared CLI, a `visual-relay`
  subcommand, or a published-exe fast path), relocate/rename `VisualRelay.ClaimAuthorship` and update
  `me.sh` to match it rather than introducing a competing pattern.
