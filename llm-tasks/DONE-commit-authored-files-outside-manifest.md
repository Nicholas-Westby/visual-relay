# Don't silently drop run-authored files that are outside the stage-4 manifest

During self-hosting, the Author-tests stage sometimes creates a **new** test file that the
stage-4 Plan never listed in the manifest. The commit then **silently drops it**, so the run's
regression tests never land — the implementation commits without the tests that prove it. This
happened twice and had to be corrected by hand each time:

- `ac97daf` (test-command-default-timeout) dropped
  `tests/VisualRelay.Tests/VisualRelayTestCommandTimeoutTests.cs`.
- `e8a273a` (persist-stage-status-record) dropped
  `tests/VisualRelay.Tests/MainWindowViewModelTests.Status.cs`.

Both files were authored in stage 5, both were absent from the manifest, and both were left
untracked in the working tree after the run "succeeded."

## Current state (researched)

`GitCommitter.CommitAsync` (`src/VisualRelay.Core/Execution/GitCommitter.cs`) stages exactly
three sets after `git reset -q`:

1. **Manifest files** — `git add -A -- <manifest>` (`GitCommitter.cs:36-43`), via
   `ResolveManifestFilesToStageAsync` (`GitCommitter.cs:105-128`). `add -A` on a path adds even
   an untracked file, so a *new* file **does** commit **if it is in the manifest**.
2. **Tracked modifications/deletions** — `git add -u` (`GitCommitter.cs:50`). This stages edits
   to already-tracked files outside the manifest (a shared test double, a `.csproj`, the
   regenerated screenshot) — but `add -u` **does not stage new untracked files**.
3. **Proof files** — `git add -f -- <proofFiles>` (`GitCommitter.cs:60-67`): `ledger.md`,
   `<task>.seals`, `manifest.txt`, `status.json`.

The gap: a **brand-new untracked file that is not in the manifest** is staged by none of these
steps, so it never enters the commit — and nothing surfaces the omission. Stage 9 verifies the
working tree (which *has* the file), so the run goes green while the commit is missing it. Tasks
whose new test file *was* listed in the stage-4 manifest (`cap-and-degrade-long-test-runs`,
`resume-incomplete-run`) committed correctly — confirming the gap is specifically
untracked-and-unmanifested files.

## What to build (one direction — pick and justify in the ledger)

Make a run-authored file **never silently disappear**. Two acceptable shapes; choose one:

- **Auto-include**, bounded: snapshot the set of untracked files at run start (e.g.
  `git ls-files --others --exclude-standard`); at commit, stage any untracked file that did
  **not** exist at run start and lives under a tracked source root (`src/`, `tests/`, `tools/`).
  This keeps the manifest's safety role — do **not** blanket `git add -A .` (that would sweep
  unrelated scratch like editor files or agent notes).
- **Fail loudly**: at commit, if any untracked, non-ignored file under a source root was created
  during the run and is not in the manifest or proof set, fail the commit with a message naming
  the file(s) ("authored but unmanifested: <paths> — add them to the manifest"). A loud failure
  the driver flags is strictly better than a silent drop.

Either way:

- Preserve the existing behavior: manifest files, `git add -u` tracked modifications, and
  force-added proof files still commit exactly as today.
- Do **not** sweep in genuinely unrelated untracked files (pre-existing scratch, `.relay-scratch/`,
  `.swival/`, build output). Gitignored paths must stay excluded unless force-added as proof.
- Keep the `.relay/*` gitignore + force-add proof model intact.

## Done when

- A run that authors a new file outside the stage-4 manifest **either commits that file or fails
  with a message naming it** — it is never silently dropped. Covered by a test (GitCommitter- or
  driver-level: a new untracked file under a source root, absent from the manifest, ends up
  committed or surfaced as an error).
- Existing cases unchanged: manifest files, tracked modifications (`git add -u`), and proof files
  still commit; unrelated/ignored untracked paths are still excluded. Covered by tests.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional Commit subjects.
