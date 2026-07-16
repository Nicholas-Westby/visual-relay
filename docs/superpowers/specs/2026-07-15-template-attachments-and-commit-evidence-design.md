# Template attachments and measured commit-message evidence

Date: 2026-07-15
Status: approved (executed same day)

## Problem

The built-in `speed-up-automated-tests` template ran for the first time
(task `speed-up-automated-tests`, sealed commit `db9aecd`). The run worked, but
the review of its output found five distinct failures around the template's
"commit-message evidence" requirement and the follow-up tasks it authored:

1. **Evidence bullets were pre-filled into the authored follow-up tasks.** Each
   of the four follow-up tasks ends with a `### Commit-message evidence` block
   containing a literal bullet with *predicted* numbers (e.g. `- test time
   dropped from 119s to 113s, saving 6s`). The template meant "your eventual
   commit message must carry a measured bullet"; the run read it as "write the
   bullet now". Predicted numbers presented as measurements are exactly the
   fabrication the template's measure-first rules exist to prevent.
2. **The run's own sealed commit carried no evidence bullet** even though the
   template demanded one and the Plan stage drafted it. Two pipeline causes:
   - The stage-10 (Verify) system prompt asks for "Conventional-Commit
     *subject* candidates" — subjects only, so the drafted body was dropped.
   - `RelayDriver.BuildCommitChain` sanitizes every candidate through
     `CommitMessageSanitizer.TrySanitizeSubject`, which strips body bullets by
     design. Even a compliant multi-line candidate would have been flattened.
3. **Stale paths.** Follow-up tasks cite
   `llm-tasks/speed-up-automated-tests/timings-baseline.txt`, but task
   completion archives the folder to
   `llm-tasks/completed/speed-up-automated-tests/`. The citations were dead on
   arrival.
4. **Follow-up-task quality defects** (details in "Derived task revisions"):
   a serialization-inducing `[Collection]` prescription, a false claim about
   `RealSleepGuard`'s scan scope, a contradictory Headless guardrail, and a
   split prescription that re-pays the very cost it wants to remove.
5. **The commit-message instructions lived only as prose in one template.**
   Nothing reusable carries them into each authored follow-up task.

## Design

### A. Generic template attachments

A template `<id>.md` may have a sibling directory `<id>/` in the same
templates layer. Every top-level file in it (dotfiles skipped) is an
*attachment*. Creating a task from that template copies each attachment into
the new task folder `llm-tasks/<slug>/` beside `<slug>.md`.

- `TaskTemplate` gains `IReadOnlyList<TaskTemplateAttachment> Attachments`
  where `TaskTemplateAttachment` is `(string FileName, byte[] Content)`.
  Bytes, not text, so images and other binary attachments work.
- **Built-in layer**: attachments are embedded resources with logical name
  `VisualRelay.Core.task-templates.<id>/<fileName>`. The `/` separator makes
  the id/attachment boundary unambiguous (ids themselves cannot contain dots
  in the built-in layer — unchanged constraint). `TaskTemplates.Load` now
  enumerates manifest resource names by prefix instead of a hardcoded list:
  names without `/` and ending `.md` are templates; names with `/` are
  attachments of the id before the `/`.
- **User/repo layers**: for `<dir>/<id>.md`, attachments come from
  `<dir>/<id>/`. Unreadable attachment files are skipped (template still
  loads), matching the existing never-break-the-dialog rule.
- **Override semantics**: a higher layer that overrides `<id>.md` replaces the
  template *including* its attachment set (no cross-layer merging).
- **UI**: `CreateNewTaskAsync` writes the selected template's attachments into
  the task folder after the markdown. Selecting a template and then typing a
  custom body still copies attachments — the dropdown selection is the intent
  signal. Blank has no attachments, so the default flow is unchanged.
- Sandbox: the user templates dir is already granted recursively to task
  sandboxes; subdirectories need no new grant.

### B. Speed-up template rewrite

- New attachment `packaging/task-templates/speed-up-automated-tests/`
  `commit-message-evidence.md`: a fill-in-the-blanks instruction sheet — the
  exact bullet shape with `<before>/<after>/<delta>/<scope>` blanks, measured
  numbers only, exactly one evidence bullet, ≤ 20 words, and the rule that the
  filled bullet goes in the *commit message body*, never in a task file.
- The template body now: tells the run to follow the attached sheet for its
  own commit; requires copying the sheet into every follow-up task folder it
  authors; requires each follow-up to end with a short section pointing at the
  sheet; explicitly forbids pre-filling predicted numbers anywhere; and tells
  follow-ups to cite the baseline at its post-archive path
  `llm-tasks/completed/<parent-id>/timings-baseline.txt` while quoting the
  relevant numbers inline so each follow-up stays self-contained.

### C. Pipeline: let evidence bullets reach the sealed commit

- Stage-10 (Verify) prompt: when the task states explicit commit-message
  requirements (like a required measured-evidence bullet), every candidate
  must append the required body bullets (blank line, `- ` bullets, ≤ 3, ≤ 20
  words each) after its subject. Otherwise, subjects as before.
- `CommitMessageSanitizer.TrySanitizeMessage`: like `TrySanitizeSubject` but
  preserves sanitized body bullets (same bullet sanitizer used by
  `FromRawOrFallback`). `BuildCommitChain` switches to it, so multi-line
  candidates survive to `git commit` while non-Conventional candidates are
  still dropped and the fallback subject is still appended.

### D. Derived task revisions (content only — tasks stay pending)

All four: replace the pre-filled evidence block with a "measure, then put the
bullet in the commit message" section referencing an attached copy of
`commit-message-evidence.md`; fix the baseline path to the `completed/`
location. Individually:

- `hoist-pipeline-test-shared-setup`: drop `[Collection("Pipeline")]` +
  `IClassFixture` (a shared collection would *serialize* ~90s of currently
  parallel tests; neither pattern exists in this repo). Prescribe the xUnit v3
  assembly fixture plus per-test `Clone()` instead.
- `inject-timeprovider-into-product-retry-delays`: `RealSleepGuard` scans the
  *test project only* — correct the false "source tree" claim and verify the
  five product sites by inspection instead.
- `merge-nocommit-contamination-tests-data-driven`: fix the self-contradictory
  Headless guardrail (the class is not in the Headless collection; the rule is
  "don't add any collection attribute").
- `split-key-setup-panel-ui-tests`: splitting while still booting `MainWindow`
  three times re-pays the dominant cost (app boot + cog-click polling loop) —
  and violates the repo's scope-down convention. Prescribe the established
  `SettingsTestHelpers.ShowScopedSettings` pattern for the three new facts;
  cog→settings wiring stays covered by the class's other tests. Add a measured
  bail-out: if the three facts don't beat the original test's time, don't land.

### E. Docs

README "Task Templates" section documents the attachment-directory convention.

## Testing

- `TaskTemplatesTests`: dir-layer attachments load (content, ordering,
  dotfiles skipped, unreadable skipped); built-in speed-up template exposes
  `commit-message-evidence.md`; overriding layer replaces attachments; pinned
  template content updated (bullet shape lives in the attachment, body points
  at it and bans pre-filled numbers).
- `RelayTaskWriter` / authoring tests: creating a task from a template with
  attachments materializes them; blank template creates only the markdown.
- `CommitMessageSanitizerTests`: `TrySanitizeMessage` preserves ≤ 3 bullets,
  sanitizes words/em-dashes, drops non-bullet body lines, returns null without
  a Conventional prefix.
- `RelayDriver` chain test: multi-line candidate keeps its bullets; fallback
  still appended.
