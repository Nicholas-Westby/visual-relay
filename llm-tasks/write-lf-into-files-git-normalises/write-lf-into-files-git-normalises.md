# Write LF into files git normalises

`Environment.NewLine` is CRLF on Windows. Any file this codebase writes into a
git working tree whose `.gitattributes` says `text=auto eol=lf` therefore goes to
disk with the wrong endings: `git add` normalises the committed content to LF, the
working tree keeps CRLF, and the file reads as permanently modified afterwards.

On a codebase developed on macOS the call is invisibly correct everywhere, which
is why the whole class only appears on the Windows arm.

## Evidence

Windows PC, 2026-09-18, measured directly.

- `VERSION` is the proven instance and is now fixed (`VersionHelper` writes `"\n"`).
  The pre-commit hook bumps it on every commit, so every Windows commit left the
  file dirty until someone ran `git checkout VERSION`.
- CRLF alone is sufficient: writing the SAME content back with CRLF and no other
  change made git report the file modified. So it is the endings, not the bump.

## Current state

37 `Environment.NewLine` uses across `src/` and `tools/`. Most are console output
or log lines, where it is correct. The ones that write files are the question, and
these were named on the machine that can see the effect:

- `RelayConfigWriter` (five sites)
- `FormatBaselineCheck`
- `RelayDriver.Artifacts`, `RelayDriver.Snapshot`, `RelayDriver.NeedsReview`
- `KeyEnvFile`
- `RestartHandoff`, `DrainCircuitBreaker`

## What is actually unknown

Whether any of those files are TRACKED where they are written. Most land under
`.relay/`, and the external-repo recipe puts `.relay/` into `.git/info/exclude`, so
in a target repo they are untracked and the endings never reach git. That is why
none of them is proven: the defect needs the file to be both written by us and
seen by git, and only `VERSION` has been shown to be both.

So this is not "fix 37 call sites". It is: for each write site, establish whether
the file can be tracked in the repository it is written into, and use `"\n"` where
it can. A write that lands outside a repository, or inside an ignored directory,
is fine as it is.

## Tests

- A fact that `VersionHelper` writes LF whatever the platform (it can assert on the
  bytes, so it runs everywhere rather than only on Windows).
- For any site changed, the same shape: assert the written bytes, not the string.

## Verification

Only Windows shows the symptom, so the acceptance test is there: commit, then
`git status` is clean rather than showing a modified file.

## Rejected alternatives

- **Replace every `Environment.NewLine`.** Wrong for console output and log files,
  where the platform's convention is what a reader wants.
- **A guard banning `Environment.NewLine` in write paths.** The rule cannot be
  stated precisely enough to avoid false positives on log writers, and a guard that
  cries wolf gets suppressed.
