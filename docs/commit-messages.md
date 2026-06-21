# Commit messages

Visual Relay's own repository enforces a Conventional-Commit ruleset on every
commit through the `commit-msg` hook (a thin wrapper that execs
`tools/VisualRelay.CheckCommitMessage`; the rules live in
`src/VisualRelay.Core/CommitLint/`). The ruleset is ported from ai-sorcery's
`check-commit-message.ts` and reconciled with this repo's fixed type set.

> Scope: this enforcement is for **visual-relay's own repository only**. The
> repos Visual Relay processes at runtime are never given these rules — only the
> `pre-commit` authority hook is installed into target repos.

Activate the hook on each clone (it is per-clone git config):

```sh
./visual-relay install-hooks
```

This wires `core.hooksPath` to `.githooks/` and publishes the validator so the
hook execs a prebuilt binary and never triggers a build at commit time.

## The ruleset

A message is preprocessed before any rule runs, exactly as git does: `#` comment
lines are dropped, trailing blank lines are stripped, and the trailing **trailer
block** (lines matching `^[A-Z][A-Za-z-]+:\s.+$`, e.g. `Co-Authored-By:`,
`Task:`, `Relay-Seal:`, `Signed-off-by:`) is popped. Trailers are therefore
exempt from every rule below.

### Structural rules (always enforced)

- **Type prefix.** The subject must start with one of the canonical types,
  optional `(scope)` where scope is `[a-z0-9._-]+`, an optional `!` for a
  breaking change, then `: ` and a non-empty description:

  `feat | fix | docs | style | refactor | perf | test | build | ci | chore | revert`

- **Subject length.** At most 72 characters.
- **No trailing period** on the subject.
- **Lowercase after the prefix.** The first character of the description must be
  lowercase.
- **No em dashes** (`—`, U+2014) anywhere in subject or body.
- **Body shape** (only when there is more than the subject line):
  - a blank line must separate the subject from the body;
  - every non-blank body line must be a hyphen bullet (`- …`) — no prose lines;
  - at most **3** bullets;
  - each bullet has at most **20** words.

### Contextual rules (human/dev commits; relaxed for the driver)

These are enforced for the commits you and other developers make, but skipped
for Visual Relay's own in-run sealed commit (see below):

- **No changed-file names.** The subject and each bullet must not name a file
  that the commit changes (basenames containing `.` or ≥ 6 characters, matched on
  word boundaries). Describe the change by behavior or component instead.
- **No path-like tokens.** No `/` with non-whitespace on both sides (e.g.
  `src/core/lock`). `a / b` with surrounding spaces is fine.
- **Disallowed substrings.** If `disallowed-commit-messages.txt` exists at the
  repo root, each non-empty, non-`#` line is a case-insensitive substring that
  blocks the commit. The file is opt-in and absent by default.

### Why a driver tier

Visual Relay runs against this very repository, and its sealed commits
legitimately reference file names and paths constantly. Lossily scrubbing those
out of generated messages would also degrade the messages Visual Relay writes
into other repos. So instead of stripping file names from the generator, the
hook relaxes exactly the contextual rules for the automated sealed commit:
when `RELAY_COMMIT_TOKEN` is set and equals the active-run nonce in
`.relay/ACTIVE/info.json` (the same comparison `.githooks/pre-commit` makes),
only the structural tier applies. Every structural rule still holds — the
`CommitMessageSanitizer` guarantees the driver's output passes it.

## Examples

Good:

```
feat(core): add a queue pause control

- introduce a pause action on the active lock
- surface the queued task count in the status bar
```

```
fix!: drop the legacy launcher path
```

Rejected (human tier), with the reasons:

```
Feat: Update GitInvoker.cs under src/core.

this needs a body bullet
```

- `Feat` is not a canonical lowercase type
- description starts uppercase and the subject ends with a period
- `GitInvoker.cs` names a changed file; `src/core` is a path-like token
- the body line is prose, not a hyphen bullet

## Checking the whole history

```sh
dotnet run --project tools/VisualRelay.CheckCommitMessage -- --check-history [<range>]
```

History mode enforces the **full** ruleset (no driver relaxation) and reports
per-commit violations; it exits non-zero if any commit fails. A self-contained
`HistoryRewriter` engine (`src/VisualRelay.Core/CommitLint/`) can rebuild every
commit via `git commit-tree` to make the whole history conform — preserving
author identity and dates, moving the branch ref last, idempotent, and backing
up the old tip at the `backup/pre-conform` tag first.
