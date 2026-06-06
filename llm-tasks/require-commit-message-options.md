# Require a list of candidate commit subjects and commit with the first one the repo accepts

Visual Relay's stage 9 ("Verify") optionally emits a `commitMessage` used as the
commit subject at stage 11. Two things make that fragile. First, the field is
**optional** and stage 9 runs on the weak `cheap` tier, so the model frequently
omits it and the commit subject collapses to the uninformative fallback
`chore(relay): <task-slug>`. Second, even when a subject is produced, it is a
*single* string: if the target repo's `commit-msg` hook rejects it (for example a
repo that forbids file names or paths in the subject), the committer has no
recourse short of re-running stages. This task makes stage 9 produce a
**required, ordered array of candidate subjects** and has the committer try them
in order, landing the commit on the first one the repo accepts — so strict,
repo-specific commit rules are absorbed without re-running anything.

## Current state (researched)

**Stage 9's contract makes the subject optional, on the weakest tier.** In
`src/VisualRelay.Core/Execution/RelayStages.cs:17` the stage is declared
`Stage(9, "Verify", "cheap", "some", "all", """{ "summary": string, "commitMessage"?: string }""")`.
The `?` marks `commitMessage` optional, and `"cheap"` is the lowest model tier —
the combination is the root cause of the frequent omission. The Verify system
prompt (`SystemPromptFor("Verify")`, `RelayStages.cs:49`) is just
`"Summarize the final state; the driver decides pass/fail mechanically."` — it
never asks for a commit subject at all, let alone several varied ones.

**The driver reads one optional string and sanitizes it.** In
`src/VisualRelay.Core/Execution/RelayDriver.cs:44` the driver holds
`string? commitMessage = null;`. At `RelayDriver.cs:146` (inside the
`stage.Number == 9` block) it does
`commitMessage = ReadOptionalString(json, "commitMessage") ?? commitMessage;`.
At commit time (`RelayDriver.cs:169-170`, guarded by `_options.CreateGitCommit`)
it computes `var subject = CommitMessageSanitizer.FromRawOrFallback(commitMessage, taskId);`
then `await GitCommitter.CommitAsync(rootPath, taskId, taskHash, subject, manifest, proofFiles, activeLock.Nonce, cancellationToken);`.
`ReadOptionalString` (`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs:70-73`)
reads a single string property; a sibling helper `ReadStringArray`
(`RelayDriver.Artifacts.cs:40-51`) already exists and returns
`IReadOnlyList<string>` of non-empty strings from a JSON array (returns `[]` when
the property is missing or not an array) — reuse it for the new array field.

**The sanitizer validates one subject and falls back to the slug.**
`CommitMessageSanitizer.FromRawOrFallback(raw, taskId)`
(`src/VisualRelay.Core/Execution/CommitMessageSanitizer.cs:8-32`) trims, sanitizes
the first line (`SanitizeSubject`, strips a trailing period, truncates to 72
chars), and only keeps it if `HasConventionalPrefix` matches one of the
Conventional types (`feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert`,
with `: ` or `(` after the type, `CommitMessageSanitizer.cs:57-60`); otherwise it
returns the fallback `chore(relay): {taskId}` (truncated). Per-candidate
sanitizing must reuse this validation rather than reimplement Conventional-Commit
checking.

**The committer writes exactly one commit and treats any non-zero exit as final.**
`GitCommitter.CommitAsync` (`src/VisualRelay.Core/Execution/GitCommitter.cs:5-83`)
verifies the repo, `git reset -q`, stages the manifest, runs `git add -u` and
force-adds proof files, then builds
`var message = $"{commitMessage}\n\nTask: {taskId}\nRelay-Seal: {taskHash}\n";`
(`GitCommitter.cs:69`) and runs `git commit -m message`. Crucially it sets the
run-authority env var: `commitEnv = { ["RELAY_COMMIT_TOKEN"] = commitToken }`
when `commitToken is not null` (`GitCommitter.cs:70-72`), passed through `GitAsync`
→ `ProcessCapture.RunAsync` on that one commit process so the repo's `pre-commit`
hook accepts the driver's commit. On a non-zero exit it returns
`GitCommitResult.Failed($"commit rejected: {commit.Output.Trim()}")`
(`GitCommitter.cs:74-77`) — there is no retry. A rejected commit leaves the staged
tree untouched, so retrying with a different subject is safe.

**The repo-side rule enforcer.** `.githooks/commit-msg` is a `bash` hook that
rejects any subject not matching the Conventional-Commit regex
`^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([a-z0-9._-]+\))?!?: .+`.
Other target repos may layer on stricter `commit-msg` rules (e.g. forbidding file
names or paths in the subject) that Visual Relay cannot know ahead of time. The
only way to satisfy an arbitrary repo rule is to try a candidate and fall back to
the next one on rejection.

## What to build

Write the failing tests first, then make them pass. One committed direction:
stage 9 produces a required, ordered, varied list of commit-subject candidates;
the driver sanitizes them into a fallback chain ending in the slug; the committer
tries each in order and lands on the first the repo accepts.

**1. Stage-9 contract requires `commitMessages` (array).** In
`RelayStages.cs:17` change the contract from
`{ "summary": string, "commitMessage"?: string }` to
`{ "summary": string, "commitMessages": string[] }` — **required**, ~3–5 entries,
ordered best-first. (Keep the field name plural; the legacy singular path is
handled defensively in the driver, see step 3.)

**2. Strengthen the Verify system prompt** (`SystemPromptFor("Verify")`,
`RelayStages.cs:49`). Beyond the existing summary instruction, require the model
to produce **several DISTINCT Conventional-Commit subject candidates, best-first,
deliberately varied** — explicitly instruct that the options differ in style:
some terse, and at least one that **avoids mentioning file names or paths** — so
that a strict repo `commit-msg` hook (e.g. one that forbids file names) can still
be satisfied by a later option. Keep it a single concise prompt string.

**3. Driver reads the array, sanitizes into a fallback chain.** In the
`stage.Number == 9` block (`RelayDriver.cs:142-151`), replace the single
`commitMessage`/`ReadOptionalString` read with a read of the `commitMessages`
array via the existing `ReadStringArray` helper
(`RelayDriver.Artifacts.cs:40-51`). For **resilience**, if `commitMessages` is
absent/empty but a legacy single `commitMessage` string is present, treat that
string as a one-element list (use `ReadOptionalString` as the fallback source).
Store the resulting raw candidate list on the driver instead of the single
`string? commitMessage` at `RelayDriver.cs:44`. Treat missing/empty as a **soft
failure**: it must still work (the slug fallback below guarantees a commit), but
the array is preferred.

At commit time (`RelayDriver.cs:169`), build the final ordered candidate list:
- Sanitize each raw candidate through the existing `CommitMessageSanitizer`
  validation (reuse it; add a per-candidate entry point on the sanitizer that
  returns the cleaned subject or signals "invalid" so invalid ones can be
  dropped — do not duplicate the Conventional-Commit logic).
- Drop candidates that fail validation; keep the order of the survivors.
- Append the existing `chore(relay): <slug>` (the current
  `FromRawOrFallback(null, taskId)` output) as the **final, guaranteed-Conventional
  fallback** so the list is never empty and always ends in something the stock
  `commit-msg` hook accepts.
Pass this ordered candidate list to `GitCommitter.CommitAsync` instead of the
single `subject`.

**4. `GitCommitter` tries each candidate in order.** Change
`GitCommitter.CommitAsync` (`GitCommitter.cs:5`) to accept the ordered candidate
list (e.g. `IReadOnlyList<string> commitMessages`) instead of a single
`string commitMessage`. Keep all staging/reset/proof logic unchanged. Then, for
each candidate in order:
- Build the full message with the same `\n\nTask: {taskId}\nRelay-Seal: {taskHash}\n`
  trailer (`GitCommitter.cs:69`).
- Run `git commit -m <message>` **with the `RELAY_COMMIT_TOKEN` env var set on
  every attempt** exactly as today (`GitCommitter.cs:70-73`) — the run-authority
  token must be present on each retry or the `pre-commit` hook will reject the
  driver's own commit.
- On exit code 0, resolve and return `GitCommitResult.Committed(sha)` immediately
  — first accepted candidate wins.
- On a non-zero exit (rejection, e.g. by the `commit-msg` hook), record the
  rejection reason and try the **next** candidate. The staged tree is unchanged by
  a rejected commit, so no re-staging is needed between attempts.
- Only if **all** candidates (including the slug fallback) are rejected, return
  `GitCommitResult.Failed(...)` with the **last** rejection reason.
Bound the loop strictly by the candidate count — do **not** mask unrelated commit
failures into a loop; a rejected `git commit` exit is the retry trigger, and the
list is finite, so there is no infinite-retry path.

## Done when

- Stage 9's contract is `{ "summary": string, "commitMessages": string[] }` with
  `commitMessages` **required** (no `?`), and the contract documents ~3–5
  ordered, best-first options.
- The Verify system prompt demands **several DISTINCT** Conventional-Commit
  subject candidates, best-first and deliberately varied, including at least one
  that avoids file names/paths.
- The committer commits using the **first accepted** candidate and, on rejection,
  falls back through the remaining candidates and finally the
  `chore(relay): <slug>` fallback; it succeeds on the first acceptance and only
  fails (with the last rejection reason) if every candidate is rejected.
- A candidate **rejected by a repo `commit-msg` hook causes the next to be tried**
  — covered by a test using a temp git repo whose `commit-msg` hook rejects a
  recognizable pattern (e.g. any subject containing a file-name pattern like a
  path or `*.cs`/`.cs` token), with a candidate list whose first option contains
  a file name and a later option does not, asserting the **later** option lands
  and the resulting commit subject is the accepted one.
- Invalid (non-Conventional) candidates are dropped during driver sanitization
  via the reused `CommitMessageSanitizer`, and the slug fallback is always present
  as the last entry so the list is never empty.
- **Legacy single `commitMessage`** string still works: a stage-9 payload with
  only `commitMessage` (no `commitMessages`) is treated as a one-element list and
  commits successfully.
- A stage-9 payload with **missing/empty** `commitMessages` (and no legacy
  string) still commits via the slug fallback (soft failure, not a run failure).
- `RELAY_COMMIT_TOKEN` is still set on **each** commit attempt (preserving the
  run-authority gate), verified by the committer tests — including the retry path,
  so a later candidate's commit still carries the token.
- All new/updated tests were written to **fail first** against current `main`,
  then pass.

Plus: `./visual-relay check` green; C#/shell files stay under 300 lines;
Conventional Commit subjects.
