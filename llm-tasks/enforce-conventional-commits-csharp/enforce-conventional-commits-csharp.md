# Enforce Conventional Commits in C# (commit-msg hook) + rewrite history to conform

Replace this repo's bash `commit-msg` hook with a C# validator that ports the ruleset from
ai-sorcery's `check-commit-message.ts`
(<https://github.com/ai-sorcery/ai-sorcery/blob/main/plugins/sorcery/check-commit-message.ts>),
reconciled with this repo's existing Conventional Commit type set. Logic lives in testable C#
(`VisualRelay.Core`) exercised by a new `tools/VisualRelay.CheckCommitMessage` entrypoint; the
`.githooks/commit-msg` file becomes a **thin shell wrapper** that execs the tool. Then **rewrite
every existing commit** so the whole history passes the new validator.

This design is decided — implement exactly this. The four shaping decisions (already made with the
user): **(1)** full-fidelity port of the reference's body rules (hyphen-bullets-only, ≤3 bullets,
≤20 words/bullet, no em dashes); **(2)** the history rewrite is an **LLM semantic rewrite that
preserves every commit** (rephrase each body into ≤3 meaningful bullets, never drop/squash);
**(3)** restrict the commit **type** to the fixed canonical set; **(4)** the history-rewrite engine
is **self-contained** (do not depend on the [[claim-authorship]] task — see Coordination).

> ## ⚠️ Scope boundary — THIS repo only
> This enforcement is for **visual-relay's own repository only**, never for the repos Visual Relay
> processes at runtime. Do **not** add commit-message validation to target/processed repos. Leave
> `HookInstaller` (`src/VisualRelay.Core/Init/HookInstaller.cs`) installing **only** the `pre-commit`
> authority hook into target repos. Nothing in this task ships visual-relay's commit rules to any
> other repo. The new validator is wired solely through this repo's `.githooks/commit-msg` +
> `./visual-relay install-hooks`.

> ## ⚠️ Cite code by stable anchors, not line numbers
> This codebase changes fast and line numbers go stale. Throughout this spec, code is referenced by
> **file name + function/field/symbol name + a short code snippet**. When you research the current
> state, locate these by symbol/grep, not by the line numbers that may have drifted. Do the same in
> any commit messages or notes you write.

---

## Current state (researched)

### The thing being replaced
- **`.githooks/commit-msg`** is a `bash` hook. It checks the subject against one regex and nothing
  else: `^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._-]+\))?!?: .+`,
  printing a Conventional-Commits reminder and `exit 1` on mismatch. It does **not** check subject
  length, body shape, em dashes, file names, or anything the reference enforces.
- **Hooks are wired by `./visual-relay install-hooks`** (the `install-hooks)` case in the
  `visual-relay` launcher script): it runs `git config core.hooksPath "$SCRIPT_DIR/.githooks"` and
  `chmod +x` on `commit-msg`, `pre-commit`, `tools/guards/check-file-size.sh`, and `visual-relay`.
  `core.hooksPath` is per-clone git config (lives in `.git/config`), so it is **not** shared via the
  working tree — each machine must run `install-hooks` (relevant because the repo is
  [[repo-shared-with-vm]]). On at least one machine `core.hooksPath` is currently unset, so the hook
  may not even be active until `install-hooks` runs.
- **Published-exe fast path precedent.** The launcher prefers a prebuilt binary and only falls back
  to `dotnet run` — see the `PUBLISHED_INIT` published-exe fast path in `visual-relay` (and the
  `dotnet run --project …` fallback used by the `install-hooks`/`init` cases). The new wrapper must
  follow this so the hook never triggers a build at commit time (important under the `nono` sandbox
  during a run — see [[nono-vr-guard-writable-set]], [[nono-pycache-stall]]).

### What the reference (`check-commit-message.ts`) enforces — port all of it
Pre-processing (do this before any check, matching git + the reference):
1. Drop lines beginning with `#` (comments).
2. Strip trailing blank lines.
3. Strip the trailing **trailer block**: repeatedly pop the last line while it matches
   `^[A-Z][A-Za-z-]+:\s.+$`. (This exempts `Co-Authored-By:`, `Claude-Session:`, `Task:`,
   `Relay-Seal:`, `Signed-off-by:` etc. — all match.)
4. Strip trailing blanks again. The remaining lines are subject (line 0) + body.

Rules over the remaining text:
- **Em dashes** (`—`, U+2014): zero allowed anywhere in subject+body (trailers are already stripped,
  so an em dash in a trailer is not counted — faithful to the reference).
- **Subject**: ≤72 chars; must not end with `.`; the first character after the `type(scope):` prefix
  must be lowercase (reference regex `^(\w+(?:\([^)]+\))?:)\s*(\S)`, error if that char is `[A-Z]`).
- **Body** (only if there is more than the subject line): a blank line must separate subject and
  body; every non-blank body line must be a hyphen bullet (`^- `) — any **prose line is an error**;
  **≤3 bullets**; each bullet (text after `- `) **≤20 words** (split on whitespace).
- **Concept check** on the subject and each bullet: no **path-like token**
  (`/(?<!\s)\/(?!\s)/` — a `/` with non-whitespace on both sides) and no **changed-file basename**
  (from `git diff --cached --name-only`; only basenames that contain `.` **or** are ≥6 chars, matched
  with `\b…\b`).
- **`disallowed-commit-messages.txt`** at repo root (optional): each non-empty, non-`#` line is a
  case-insensitive substring that blocks the commit.
- **Output**: print `check-commit-message: N violation(s)`, then `  - <each violation>`, then a
  pointer to the rules doc; `exit 1`. Missing-file-arg usage error → `exit 2`.

**Reconciliations (deviations from the reference, all already decided):**
- The reference does **not** restrict the type word. We **do**: require the fixed canonical set
  `feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert`, optional `(scope)` where scope is
  `[a-z0-9._-]+`, optional `!` (breaking change), then `: ` and a non-empty description. A subject
  that lacks a valid prefix is an error (the reference silently allowed it; we reject it).
- Then additionally apply the reference's lowercase-after-prefix check.

### Where the driver's own commits come from (dogfooding — must keep working)
Visual Relay runs against **this** repo (the `.relay/` runtime state, the `pre-commit` nonce gate
"during an active Visual Relay run", and `GitCommitterTests.CommitMsgHooks` all prove it). So the
driver's sealed commits land here and will hit the new hook.
- **`GitCommitter`** commits via `git commit -m` — the `attemptMessage` built inside its
  `foreach (var candidate in commitMessages)` loop. This triggers `commit-msg`. That `attemptMessage`
  appends `Task:`/`Relay-Seal:` trailers, and the loop's `attemptEnv` sets `RELAY_COMMIT_TOKEN` +
  `RELAY_NONCE` to the active-lock nonce on the commit process; git passes that environment through
  to the `commit-msg` hook, exactly as `.githooks/pre-commit` relies on.
- **Candidate-retry already exists** — the `foreach (var candidate in commitMessages)` loop in
  `GitCommitter` tries each candidate via `git commit -m`, and on hook rejection records the error and
  tries the next; all rejected → `GitCommitResult.Failed("commit rejected: …")`. This is the
  mechanism that lets VR satisfy **any** target repo's own `commit-msg` hook (see
  `llm-tasks/DONE-require-commit-message-options.md`), and it must stay intact.
- **`CommitMessageSanitizer`** (`src/VisualRelay.Core/Execution/CommitMessageSanitizer.cs`) already
  does most of the structural work, spread across `FromRawOrFallback`, `SanitizeSubject`, `Truncate`,
  and `HasConventionalPrefix`: a `MaxSubjectLength = 72` const, the same `Types` set, em-dash→hyphen
  via `.Replace("—", "-")` on both subject and bullets, a trailing-period strip, word-boundary
  truncation to 72, a `.Take(3)` bullet cap, and a conventional-prefix gate. **Gaps vs. the strict
  validator:** no lowercase-after-prefix normalization, no ≤20-words/bullet cap, no scope-charset
  check, and `HasConventionalPrefix` doesn't accept `!` (`feat!: …`). These are the only structural
  gaps to close.

### Seams & conventions to reuse
- **Git seam.** Route every git call through `IGitInvoker`/`GitInvoker`
  (`src/VisualRelay.Core/Execution/GitInvoker.cs`) and its
  `RunAsync(rootPath, args, ct, timeout?, environment?, …)` method; never shell out to git directly.
- **CLI tool convention.** Tools live at `tools/VisualRelay.<Name>/` with top-level-statements
  `Program.cs` (arg-parse → usage to stderr → numeric exit code), `OutputType=Exe`,
  `TargetFramework=net10.0`, `ProjectReference` to `VisualRelay.Core` — see
  `tools/VisualRelay.RunTask/Program.cs` and its `.csproj`. Register the new project in
  `VisualRelay.slnx`.
- **File-size guard.** `tools/guards/check-file-size.sh` fails any `*.cs`/`*.axaml` under
  `src/`,`tests/`,`tools/` over **300 lines** (`VISUAL_RELAY_FILE_LINE_LIMIT:-300`). Split new code
  into focused files to stay under it.
- **Test runner.** Use `./test.sh` (persists logs/trx and prints failing test names —
  [[test-failure-observability]]).

---

## What to build

TDD throughout — write the failing test(s) first for each unit. Keep every new file ≤300 lines.

### 1. The validator (pure, IO-free) in `VisualRelay.Core`
New module `src/VisualRelay.Core/CommitLint/`. Split across small files, e.g.:
- **`CommitRules`** — the single source of truth for constants/regexes: the canonical `Types`,
  `MaxSubjectChars = 72`, `MaxBullets = 3`, `MaxBulletWords = 20`, the subject regex, the trailer
  regex, the path-token regex. **`CommitMessageSanitizer` (§3) must consume these same constants** so
  generator and validator never drift.
- **`CommitMessagePreprocessor`** — comment/trailer/blank stripping → `(subject, bodyLines)`.
- **`CommitMessageValidator`** — orchestrates checks, returns `IReadOnlyList<Violation>` (each with a
  human message mirroring the reference's wording). Signature is pure, e.g.
  `IReadOnlyList<Violation> Validate(string message, CommitLintContext context)`, where
  `CommitLintContext` carries the inputs the pure core must not fetch itself: the **changed-file
  basenames**, the **disallowed substrings**, and a **rule tier** flag (see below). All git/file IO
  happens in the tool (§2), not here.
- Helper checkers (`SubjectRules`, `BodyRules`, `ConceptCheck`) as needed to respect the size guard.

**Two rule tiers** (this is how "harden the sanitizer" coexists with "other repos may differ"):
- **Structural** (always enforced): type set + scope charset + optional `!`, ≤72 subject, no trailing
  period, lowercase-after-prefix, blank-line-before-body, bullets-only, ≤3 bullets, ≤20-word bullets,
  no em dashes.
- **Contextual** (enforced for human/dev commits; **skipped** for the driver's in-run sealed commit):
  no changed-file basenames, no path-like tokens, and the `disallowed-commit-messages.txt` blocklist.
  Rationale: VR's real commits here reference file names constantly, and other repos often *want*
  file names — so we never lossily scrub them from generated messages; instead this repo's hook
  relaxes exactly these contextual checks for the automated sealed commit.

Unit-test every rule (mirror the reference's cases): a fully valid message passes; each violation is
flagged exactly once with the right message; trailers are exempt; `#` comments are stripped; a
breaking-change `feat!: …` passes; a bad scope (`feat(App): …`) is flagged; disallowed-substring
hit; em dash in subject and in body; path token; ≥6-char/has-dot basename match; >3 bullets;
>20-word bullet; uppercase-after-prefix; missing blank line; prose body line; >72 subject; trailing
period; and the **contextual tier off** case (a filename in a bullet passes when the tier flag says
"driver", fails when "human").

### 2. The tool + thin shell wrapper
New `tools/VisualRelay.CheckCommitMessage/` (registered in `VisualRelay.slnx`), modes:
- **Hook mode** `<commit-msg-file>`: read the file; resolve repo root via `GitInvoker`; collect
  changed-file basenames from `git diff --cached --name-only`; read `disallowed-commit-messages.txt`
  if present; decide the **rule tier**: if `RELAY_COMMIT_TOKEN` is set in the environment **and**
  equals the `nonce` in `<repoRoot>/.relay/ACTIVE/info.json` (the same comparison `.githooks/pre-commit`
  makes), use the **driver** tier (skip contextual rules); otherwise the **human** tier (all rules).
  Run the validator; print violations like the reference; `exit 1` on any, else `0`. No file arg →
  usage to stderr, `exit 2`. The validator must do **no network and write nothing** (read-only +
  `git` reads only) so it is sandbox-safe mid-run.
- **History-lint mode** `--check-history [<range>]`: validate every commit in range (default whole
  branch). Derive each commit's changed-file basenames from its **own** diff
  (`git diff-tree --no-commit-id --name-only -r <sha>`). History mode enforces the **full** ruleset
  (no driver relaxation — rewritten history must be genuinely clean). Print per-commit violations;
  non-zero exit if any.

Replace **`.githooks/commit-msg`** with a thin `bash` wrapper that: resolves the script/repo dir;
prefers a **published binary** (mirror the `PUBLISHED_INIT` fast path in `visual-relay`) and falls
back to `dotnet run --project "$SCRIPT_DIR/tools/VisualRelay.CheckCommitMessage" -- "$1"`; passes the
commit-msg file path; never forces a build. Update the **`install-hooks)` case in `./visual-relay`**
to also **publish** the tool so the fast path exists, and keep `chmod`-ing the new `commit-msg`. Do
not change `pre-commit` behavior.

### 3. Make the driver's generated commits conform (dogfooding)
Harden `CommitMessageSanitizer` so its output **always passes the validator's structural tier**,
consuming the shared `CommitRules` constants (delete its private duplicate `MaxSubjectLength`/`Types`).
Close the gaps from Current state: lowercase the first word after the prefix; cap each bullet at
≤20 words (trim gracefully on a word boundary, don't hard-cut mid-word); validate the scope charset;
accept `!` in the prefix gate. Add a test asserting `FromRawOrFallback(messyRaw, taskId)` and
`TrySanitizeSubject` outputs validate clean (structural tier) across messy inputs (uppercase-after-prefix,
em dashes, >3 bullets, >20-word bullets, trailing period, no-prefix → fallback).

**Do not** add the contextual rules to the sanitizer (no filename scrubbing) — those are handled by
the hook's driver-tier relaxation, and adding them would degrade the messages VR writes into other
repos. Leave the candidate-retry loop (the `foreach (var candidate in commitMessages)` loop in
`GitCommitter`) and VR's deference to each target repo's own hook intact; that is what keeps VR
working with repos that have "way different standards."

### 4. One-time history rewrite (all 452 commits, nothing dropped)
Build a **self-contained** in-process rewrite engine in `VisualRelay.Core/CommitLint/` (e.g.
`HistoryRewriter`, taking `IGitInvoker`). It does **not** reuse the [[claim-authorship]] task's
engine. Three steps:

1. **Export.** Walk the range oldest→newest (`git rev-list --reverse` + per-commit
   `%T`, `%P`, author/committer name/email, `%aI`/`%cI`, `%B`). Emit one record per commit
   (sha, tree, parents, identities, dates, raw message) to a location the implementing agent reads.
2. **Rewrite (LLM, semantic — preserve every commit).** For each commit, the implementing agent
   rewrites the message to pass the **full** validator: a valid `type(scope): …` subject ≤72,
   lowercase after the prefix, no trailing period, no em dashes; body as **≤3 hyphen bullets, ≤20
   words each**, faithfully summarizing the original body's content. Because the contextual rules
   apply in history mode, **rephrase file-specific changes by component/behavior rather than bare
   file names or paths**. Never squash, drop, or merge commits — one rewritten message per original
   commit.
3. **Replay + verify.** Validate every rewritten message (full ruleset) **before** writing anything;
   abort with a clear report if any still fails. Then rebuild from root with
   `git commit-tree <tree> [-p <newParent>…] -F <msgfile>`, preserving the original **author**
   identity and **author date** via `GIT_AUTHOR_*`; set committer per a stated policy. Move the
   branch ref last (`git update-ref refs/heads/<branch> <newTip> <oldTip>`; update `HEAD` if
   detached). New tip's tree equals the old tip's tree, so the working tree and index are untouched.
   **Idempotent:** if every commit already validates and no message changes, do nothing.
   **Merges:** support multiple `-p`, or fail fast with a clear "merge commits out of scope" message
   (the repo is linear per `AGENTS.md`). **Safety:** require a clean working tree; create a backup
   ref/tag (e.g. `git tag backup/pre-conform <oldTip>`) before moving the branch; document that this
   rewrites all SHAs (force-push implications if the branch is published).

Test the engine against a **temp git repo** (pattern from `GitCommitterTests`): seed commits with
non-conforming messages and varied authors/dates; export → supply rewritten messages → replay; assert
messages now validate clean, author identity + author dates preserved, branch ref moved, working tree
& index unchanged, re-run is a no-op, and a merge commit triggers the clear failure. (The actual
452-commit rewrite of this repo is run once by the implementer, not in the test suite. Recount with
`git rev-list --count HEAD` at implementation time — the codebase moves fast, so 452 is a snapshot.)

### 5. Docs, guards, wiring
- Add **`docs/commit-messages.md`** documenting the full ruleset, examples, the type set, and the
  driver-tier relaxation; the tool's error output points here (the reference points at its SKILL.md).
- Update the **Conventional Commits lines in `README.md` and `AGENTS.md`** (currently "…enforced by
  the `commit-msg` hook once `./visual-relay install-hooks`") to describe the fuller ruleset and link
  `docs/commit-messages.md`.
- `disallowed-commit-messages.txt` is **opt-in**: absent by default; the validator reads it only if
  present. (Optionally seed it later with overly-generic scopes; not required here.)
- Keep `tools/guards/check-file-size.sh` green (all new files ≤300 lines).
- Optionally add a `tools/guards/`-style **history-conformance guard** that runs
  `--check-history` (e.g. as a `pre-push` guard, following the pre-push precedent in the
  inspectcode-standards task), rather than a unit test over the whole history. Going forward, the
  `commit-msg` hook keeps new commits clean.

---

## Done when

- `CommitMessageValidator` + `CommitRules` exist in `VisualRelay.Core/CommitLint/`; unit tests cover
  every rule (both tiers) and are green via `./test.sh`.
- `.githooks/commit-msg` is a thin wrapper that execs `tools/VisualRelay.CheckCommitMessage` (no rule
  logic in bash); `./visual-relay install-hooks` publishes the tool and wires `core.hooksPath`.
  Committing a non-conforming message to **this** repo is rejected with the reference-style violation
  list; a conforming one is accepted.
- `CommitMessageSanitizer` output passes the validator's structural tier (test proves it); the
  driver's in-run sealed commit is **accepted even when a bullet names a file** (contextual tier
  relaxed via `RELAY_COMMIT_TOKEN`), while an equivalent **human** commit naming a file is rejected.
- **Other repos are unaffected:** `HookInstaller` still installs only `pre-commit`; nothing ships
  visual-relay's rules elsewhere; the sanitizer change is additive and the candidate-retry path is
  intact.
- A `HistoryRewriter` exists with the temp-repo tests above; running it over this repo rewrites all
  commits so `--check-history` reports **zero violations**, author identity + author dates preserved,
  nothing squashed/dropped, idempotent, with a `backup/pre-conform` tag created first.
- `tools/guards/check-file-size.sh` is green; `docs/commit-messages.md` added; `README.md`/`AGENTS.md`
  updated; the new project is registered in `VisualRelay.slnx`; the full suite passes via `./test.sh`.

---

## Coordination

- **[[authoring-llm-task-specs]] note:** the implementing LLM sees only this file. Everything needed
  is above; do not rely on chat context. Locate the cited symbols by grep/search, not line number.
- **Overlap with `llm-tasks/claim-authorship-strip-claude-trailers/`** (the [[claim-authorship]]
  task). That task **also rewrites all of history** (claims authorship, strips any trailer mentioning
  "Claude") and converts `me.sh` into a C# `commit-tree` replay engine. Decisions for this task:
  - **Independent engines.** Build this task's `HistoryRewriter` standalone; do not block on or import
    that task's `AuthorshipClaimer`.
  - **No concurrency.** Both rewrite from root, but the queue drains tasks **sequentially**
    (`RelayTaskRepository.ListAsync` orders by `Id`; the drain runs one task at a time), so the two
    rewrites never run at once. Whichever runs last wins on the final SHAs.
  - **Either order converges.** This task's message conformance and that task's authorship/trailer
    rewrite are orthogonal: the validator strips trailers before checking, so stripping Claude
    trailers cannot break conformance, and re-authoring does not change message text. Running them in
    either order yields history that is both authored and conformant. Re-running `--check-history`
    after both is the final check.
  - **Don't clobber each other's shell→C# conversions.** That task thins `me.sh`; this task thins
    `.githooks/commit-msg`. Different files — leave the other's wrapper alone.
- **Per-machine wiring** ([[repo-shared-with-vm]]): `core.hooksPath` is per-clone, so each machine
  must run `./visual-relay install-hooks`; don't assume it's already set.
- **Sandbox** ([[nono-vr-guard-writable-set]], [[nono-pycache-stall]]): the hook runs during
  sandboxed runs — the validator must do no network and write nothing, and the wrapper must use the
  published binary (no build at commit time).
