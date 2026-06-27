# Manifests can claim gitignored paths; the failure surfaces only at stage-11 commit

2026-06-11 04:08, drop-vestigial-kimi-suffix run: stage 6 edited the repo-root
`swival.toml` (a gitignored runtime artifact that PrepareAsync regenerates per
attempt) and listed it in its manifest. Stages 7–10 all passed; stage 11's
committer then failed `git add` ("paths are ignored by one of your .gitignore
files") and flagged a run whose 20 other manifest files were perfectly
committable. The committer refusing to force-add ignored paths is correct —
the gap is that nothing validates manifests against ignore rules until the
very last stage, hours after the mistake.

## Goal

A manifest entry matching the target repo's gitignore rules is rejected when the
manifest is PRODUCED (stage-6 acceptance), with a corrective retry telling the
agent which paths are ignored runtime artifacts — not at stage 11. Stage 11
keeps its hard failure as the backstop, but its flag reason should name the
offending path(s) explicitly (the current reason buries them after a git hint
line). Works for any target repo (`git check-ignore` semantics, no hardcoded
filenames).

## Approach (suggested)

- Where the implement stage's contract/manifest is accepted, run the candidate
  file list through `git check-ignore --stdin` (via the pinned GitInvoker);
  non-empty result → treat like a contract-shape failure (existing corrective
  retry machinery) with the ignored paths in the corrective prompt.
- Same validation wherever later stages can append manifest entries.
- Tests: manifest with an ignored path → corrective retry fires with the path
  named; clean manifest unaffected; stage-11 flag reason names offending paths
  when the backstop trips (simulate by bypassing the early check).
