# Surface nono sandbox denials in verify artifacts and flag reasons

nono can name the exact denied path: its failure footer prints "Sandbox
denial: N paths blocked" plus fix flags, and `--diagnostics-json` emits a
machine-readable record (observed 2026-07-18: `{"operation":
"file-write-create", "target": "/Volumes/Tera/.TemporaryItems/…/
NSIRD_swift-build_…"}` with remediation hints). But `BuildNonoPrefix` always
adds `--silent` (deliberately — the footer is a known red herring that fills
the agent's tail window), so this evidence is discarded. Net effect in the
2026-07-19 patternsmith drain: three escalating fix-verify agents and the
final flag all saw only "setup check failure"; the denial that explained
everything was suppressed on every attempt, and root-causing it required
manual re-runs outside the harness. Compounding this, the distilled flag
reason never names WHICH setup check failed or what command it ran
(RelayDriver.VerifyObservability.cs:37 emits the bare string "setup check
failure"; the GUI banner gives the operator nothing to act on).

## Prescribed approach

Keep `--silent` (the human footer stays suppressed). Add `--diagnostics-json`
to the verification-runner prefix, parse the trailing JSON session object out
of the captured output, STRIP it so agents and artifacts still see clean
command output, and surface the denials in three places: the per-attempt
verify-checks JSON, the combined failure text fed to the next fix-verify
agent, and the distilled reason used for `verify_result` events and flag
reasons. First determine empirically whether `--silent` suppresses the
`--diagnostics-json` stderr output; if it does, drop `--silent` for
verification runs only and strip the footer during parsing instead — the
observable contract below stays the same either way.

### Steps

1. `BuildNonoPrefix`: new flag so the SandboxedTestRunner path requests
   diagnostics; swival agent runs unchanged.
2. SandboxedTestRunner: extract the trailing session-JSON from the run's
   output (tolerant parser: last balanced `{…}` containing `"denials"`;
   absent, truncated, or malformed → no diagnostics, never an error), remove
   it from the output, and return denials as structured data alongside the
   existing TestRunResult (extend the result type or add an out-of-band
   accessor — pick whichever keeps ITestRunner implementations honest).
3. SetupCheckResults: add per-check denial info; `ToSummaryLines` renders
   e.g. `✗ guard: red (sandbox denial: /Volumes/Tera/.TemporaryItems/…)`;
   persist through TryPersistVerifyChecksJson.
4. Distilled reason: replace the bare "setup check failure" with the failing
   check name, its command, and the first denial path when present —
   `setup check failure: guard 'swift build' (sandbox denial: <path>)` —
   truncation-safe (flag reasons keep only the first 200 chars of line one).
5. Fix-verify prompt data: append a `--- Sandbox denials ---` section to the
   combined failure output (BuildFailureOutput/BuildFullFailureOutput) so the
   next agent sees the denial without re-deriving it.

## Tests (red first)

- JSON-tail parser: output with trailing session JSON (denials present /
  empty), output with no JSON, JSON split across the tail, malformed JSON —
  correct extraction and stripping in each case.
- SetupCheckResults serialization and `ToSummaryLines` with and without
  denials.
- Distilled-reason construction: guard-red-with-denial, bootstrap-red,
  test-green-guard-red; assert the command name and denial path appear and
  the line stays within flag-reason truncation.
- Arg-shape test: verification prefix contains `--diagnostics-json`; swival
  prefix does not.

## Verification

`./visual-relay check` green. Manual: run a task whose guard hits a denied
path; the stage-11 verify-checks JSON records the denial, the flag banner
names the check, command, and path, and the persisted verify output contains
no leftover diagnostics JSON.
