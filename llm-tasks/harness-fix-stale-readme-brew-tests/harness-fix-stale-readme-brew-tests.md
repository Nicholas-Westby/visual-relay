# Harness: fix stale README install tests after the brew section was removed

## Problem

Commit `a423fe7 docs: remove brew section from README` removed the Homebrew install
section, but the `Installer5DocsTests` assertions that pin that section were not
updated. They now fail (`README.md` has **0** `brew` references — `grep -ic brew
README.md` → 0), each with `Assert.Contains() Failure: Sub-string not found`:

- `Readme_BrewInstall_IsMarkedNotYetAvailable`
- `Readme_BrewInstall_ReferencesTheTap`
- `Readme_ExplainsFormulaNotCaskRationale`
- `Readme_InstallSection_WarnsAgainstBrowserDownload`
- `Readme_SourceCheckoutPath_PrecedesBrew`

These are **genuine pre-existing failures, unrelated to the sandbox** — a real slice of
why the suite isn't green. (They are distinct from the 135+ sandbox profile-write
failures handled by `harness-isolate-profile-write-in-driver-tests`.)

## Current state (researched 2026-06-26 — re-grep anchors)

- `tests/VisualRelay.Tests/Installer5DocsTests.cs` — contains the 5 method names above
  (re-grep them; do not trust line numbers).
- `README.md` no longer has a brew/Homebrew install section.

## What to build

Reconcile the tests with the **current** docs — the commit deliberately removed brew, so
the README is the source of truth. Do **not** re-add the brew section to satisfy stale
tests.

- **Remove the now-obsolete brew assertions** (`Readme_BrewInstall_*`,
  `Readme_ExplainsFormulaNotCaskRationale`, and `Readme_SourceCheckoutPath_PrecedesBrew`
  insofar as it only encodes brew ordering).
- For any assertion that still encodes a **real, current** doc requirement, keep it but
  rephrase off the removed brew anchor — e.g. if the README still warns against a browser
  download in a non-brew form, keep `…WarnsAgainstBrowserDownload` pointed at the current
  wording; if the source-checkout instructions still exist, assert they exist without the
  "precedes brew" ordering. If a test now asserts nothing meaningful for the current
  README, delete it rather than leave a hollow check.

> Judgment call: if you believe brew install **should** come back (i.e. the docs removal
> was premature), STOP and flag it rather than guessing — but absent that signal, treat
> the current README as intended and update the tests.

## Done when

- The 5 `Installer5DocsTests.Readme_*` tests pass against the current `README.md` by
  updating/removing the stale assertions, with no test asserting content the README
  intentionally no longer contains.
- No brew section is re-added to `README.md`.
- `./visual-relay check` green; Conventional Commit subject (e.g.
  `test: drop stale README brew-install assertions`).

## Coordination

Independent of the sandbox fixes. This is the clearest slice of the ~30 non-profile-write
residual failures; after `harness-isolate-profile-write-in-driver-tests` lands, re-run
the suite under nono to confirm whether any residual **beyond these 5** are genuine
(vs. stage-1 cascade) and triage then.
