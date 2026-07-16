## Task: Decode git's C-quoted paths everywhere git output is parsed

Visual Relay parses file paths out of git output (`git ls-files --others`, etc.)
as raw text lines. With git's default `core.quotePath=true`, any path containing
a byte outside printable ASCII is emitted **C-quoted**: wrapped in double quotes
with octal escapes. Example from a real run — a macOS screenshot attached to a
task (macOS screenshot names contain U+202F, a narrow no-break space, before
"PM"):

```
"llm-tasks/fix-visual-relay-timing-bug/Screenshot 2026-07-15 at 9.37.05\342\200\257PM.png"
```

That quoted string — including the surrounding literal quotes and backslash
escapes — is stored and compared as if it were the path. Every downstream
consumer then misbehaves, because the string starts with `"` and does not
correspond to any real file. Visual Relay is a general-purpose tool that runs
on arbitrary repositories; non-ASCII filenames (macOS screenshots, accented
characters, CJK, emoji) are common, so this must be fixed at the decoding
layer, not worked around per call site.

### Evidence (from the 2026-07-15 drain, all verified)

1. `GitCommitter.CaptureUntrackedSnapshotAsync` (`src/VisualRelay.Core/Execution/GitCommitter.Untracked.cs:16-42`)
   splits `git ls-files --others --exclude-standard` output on newlines and
   stores raw lines. No unquoting anywhere in the repo.
2. `IsUnderTasksDir` (`GitCommitter.Untracked.cs:53-69`, duplicated in
   `WorktreeResetter.cs:108-124`) does an ordinal prefix check against
   `tasksDir + "/"`. The quoted string starts with `"` so the llm-tasks
   exemption **fails exactly for these files**. Result: two tasks
   (`merge-nocommit-contamination-tests-data-driven`, `split-key-setup-panel-ui-tests`)
   were flagged `sealed commit is missing authored files: `"llm-tasks/fix-visual-relay-timing-bug/Screenshot …\342\200\257PM.png"``
   over another queued task's attachment that ASCII-named siblings in the same
   folder were correctly exempted from.
3. Auto-include TOCTOU gate (`GitCommitter.cs:141-151`): `File.Exists` on the
   quoted path returns false, so the path is silently skipped from staging
   (misread as "vanished"), guaranteeing the post-commit
   `FindUncommittedAuthoredFilesAsync` check then flags it.
4. `WorktreeResetter.ResetAsync` (`WorktreeResetter.cs:47-52`): `File.Exists`
   on the quoted path is false, so the delete silently no-ops — yet the file is
   still reported in the drain log as removed
   (`reset-removed … 1 untracked file(s): "llm-tasks/…\342\200\257PM.png"`).
   The log lied twice on 2026-07-16 (05:03 and 05:29 UTC).
5. `FlaggedWorkStore.CaptureAsync` (`FlaggedWorkStore.cs:83-88`): `git rm
   --cached -q -- <quoted-string>` cannot match, so pre-existing untracked
   files with non-ASCII names are wrongly bundled into flagged-work snapshots
   (both bundles from that drain are ~365 KB because they contain the 387 KB
   screenshot).

### What to build

1. Decide the decoding strategy at ONE choke point. Two sound options:
   - Run path-producing git commands with `-c core.quotePath=false` (git then
     emits raw UTF-8 bytes), or
   - use `-z` (NUL-delimited) variants where available and split on `\0`.
   Either way, add a shared helper (e.g. `GitPathOutput.ParseLines`) that all
   call sites use, including a C-unquote fallback for any remaining quoted
   output, so a future call site cannot silently regress.
2. Audit EVERY site that parses paths from git output and route it through the
   helper: `GitCommitter.Untracked.cs`, `GitCommitter.cs` (manifest gitignore
   check output at line ~84 also renders quoted paths into messages),
   `WorktreeResetter.cs`, `FlaggedWorkStore.cs` (`ls-files -u` parse at
   RestoreAsync), and any `status`/`diff --name-only`/`ls-tree` parses found by
   grep.
3. Paths that reach user-facing messages (flag reasons, drain log) must be the
   real path, not the octal-escaped form.
4. Mind macOS NFC/NFD: the existing comment in `IsUnderTasksDir` explains why
   prefix checks stay ordinal on relative paths — keep that property; decoding
   must produce the same byte form `git ls-files` reports for round-tripping
   back into `git add`/`git rm --cached` pathspecs.

### Constraints

- Repo-agnostic: no assumptions about llm-tasks layout; the fix is in git
  output decoding, not in special-casing this filename.
- Do not change what is exempted/committed beyond making the existing rules
  apply correctly to non-ASCII paths.
- Keep files under the 300-line guard; a new small helper file is fine.

### Tests (red first)

- A `TestRepository`-based test creating an untracked file whose name contains
  U+202F (and one with emoji/CJK) inside the tasks dir: `CaptureUntrackedSnapshotAsync`
  must return the decoded path, `IsUnderTasksDir` must exempt it, and
  `FindUncommittedAuthoredFilesAsync` must NOT report it as missed.
- `WorktreeResetter` test: a non-ASCII-named untracked file authored mid-run is
  actually deleted from disk, and the returned removed-list names the real file.
- Round-trip test: snapshot → write pre-run-untracked.txt → read → compare
  against a fresh capture is stable (set difference empty when nothing changed).

### Verification

- `./visual-relay check` fully green including the new tests.
