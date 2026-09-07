# Gate author-tests by the repository's test layout

Stage 5 (Author-tests) is asked to write failing tests only, and the harness is
supposed to prove they fail before Stage 6 writes the fix. In the nine-repo
evaluation of 2026-09-06 that proof was missing more often than it was present,
and the harness never said so. The gate's scope is whatever the model chose to
call a test file, the gate is skipped or vacuous in four silent ways, and Stage 5
writes no `verify_result` event, so from the artifacts a computed red cannot be
told from an asserted one. This task makes the red gate honest for every
repository: rigid path gating where a language keeps tests in separate files,
an as-is red check (plus an optional model audit) where a language keeps unit
tests inside the implementation file, and a visible "unproven" status whenever
neither could be established.

## Evidence

- In 7 of 9 repos (cJSON, click, commander.js, rack, mux, swift-argument-parser,
  ky) the ledger carries `Worktree filter (stage 5): discarded tracked
  reverted: 1`: the model edited a production file during Stage 5 and the
  harness reverted it afterwards. The swift-argument-parser trace shows an
  `edit_file` on `Sources/ArgumentParser/Utilities/Platform.swift` inside the
  Stage 5 turn, followed by "Now run targeted command second (and final) time":
  the model implements the fix to watch the tests go red then green (3 of 3
  tasks). rack task 03 "briefly implemented the fix inside the test-authoring
  stage".
- colored (Rust, unit tests in `#[cfg(test)] mod tests` blocks inside the
  implementation files) shows the other failure: the model declared
  `src/control.rs` as its test file, changed the production code in it ("changed
  normalize_env so an empty Ok(value) returns None" in its own rationale), and
  nothing was reverted because the file was on its own list. All three colored
  tasks recorded no Stage 5 check at all.
- rack ran with the bootstrap placeholder test command; all three tasks recorded
  Stage 5 `check: green` while the model's prose claimed it watched the tests
  fail and then pass.
- The hard flag "author-tests passed after implementation files were stripped"
  fired zero times in 27 tasks. What saved rack 03 and the colored tasks was an
  unrelated net: the early-implementation detector noticed the fix was already
  present and routed Stage 6 into confirm mode.

## Current state (researched, at 947a70c5)

- `RelayStages.cs:13-17` concedes the sandbox has no partial-write mode, so
  Stage 5 may write anywhere and `WorktreeFilter.DiscardNonTestEditsAsync`
  enforces test-only edits after the fact. Stage 5's contract is
  `{ "testFiles": string[], "rationale": string }`; its prompt (`:55-61`) says
  the tests must fail before implementation and must be checked with the
  targeted command only.
- `RelayDriver.Stage5.cs:37,45-46` hands the model's `testFiles` verbatim to
  `WorktreeFilter.DiscardNonTestEditsAsync` (`WorktreeFilter.cs:43-57,129-207`),
  which reverts every other dirty tracked file and deletes every other new file.
  `TestPathClassifier` (`Execution/TestPathClassifier.cs`, generic directory and
  filename heuristics plus repo-specific glob overrides) is used by Stage 4, the
  code-change gate and the targeted-command builder, but never to check the
  model's list.
- The red gate (`RelayDriver.Stage5.cs:95-150`, `AuthorTestGate.cs:28`,
  `RedGate.cs:14-62`) is mechanical when it runs: it strips manifest files that
  are not on `testFiles` back to HEAD, runs the targeted test command as a
  subprocess and reads the exit code. It silently does not run, or passes
  vacuously, when: (1) no manifest file is an implementation file outside
  `testFiles` (`hasImpl`, line 95; colored); (2) nothing was stripped and the run
  is green, accepted as "Already-resolved ... accepted green regression
  coverage" (lines 138-145; rack 03); (3) the test command is the bootstrap
  placeholder (rack); (4) the gate command is unusable, exit 127 or "no tests
  found" (`IsGateUnusable`, lines 188-207), which only warns.
- `PublishVerifyResultAsync` (`RelayDriver.VerifyObservability.cs:21,67`) is
  called for stages 9, 10 and 11 only (`RelayDriver.cs:211`,
  `RelayDriver.VerifyFix.cs:189`, `RelayDriver.CommitGate.cs:54`). Stage 5's
  check reaches status.json and the seal through the generic
  `RecordStageAsync` path that also carries self-reported checks such as
  Review's verdict.
- `EarlyImplementationDetector` (`RelayDriver.Stage5.cs:159-169`) already uses
  the real path classifier to decide whether the fix landed early; it is the
  only place the classifier meets Stage 5's output.
- Bootstrap (`Init/ProjectBootstrapper.cs`, `TestCommandDetector.cs`,
  `GuardCommandDetector.cs`, `FormatCommandDetector.cs`) already detects the
  toolchain from marker files and writes `.relay/config.json` through
  `RelayConfigWriter`; a `test_command_placeholder` event and
  `testCommandIsPlaceholder` on `/state` exist since decdf0ea.

## Prescribed approach

Two facts decide how Stage 5 can be gated, and both are properties of the
language, not of the repository: whether tests conventionally live in separate
files, and which filename or directory conventions those files follow. So the
harness carries a small, data-driven catalog of languages (attached as
`test-layout-catalog.md` in this task's directory; its buckets are reproduced
below), init detects the repository's languages and writes the resulting
defaults into `.relay/config.json` where an operator can change them, and
Stage 5 gates each file by the strategy its language calls for. A repository
of only markup, style, data or template files gets the separate-file strategy
with no extras, because its tests (Playwright, for example) still live in a
dedicated directory.

The gate must never be silent again. Every outcome is one of `red` (proven),
`unproven` (with a reason) or a hard failure, each with a `verify_result`
event exactly like stages 9-11, so an operator or an evaluator can read from
run.log what command ran, what it returned and what was stripped.

### Config

One new object in `.relay/config.json`, always written by bootstrap so it is
visible and editable:

```json
"authorTests": {
  "detectedLanguages": ["rust", "python"],
  "inlineTestExtensions": [".rs"],
  "extraTestPathGlobs": [],
  "diffAudit": "auto"
}
```

- `detectedLanguages`: informational, what init detected, so an operator can see
  why the defaults are what they are.
- `inlineTestExtensions`: extensions whose files may legitimately carry tests
  next to implementation. Bootstrap fills it from the INLINE bucket for the
  languages it detected; empty when none. Files with these extensions are never
  reverted for being "not a test" and are gated by the as-is red check.
- `extraTestPathGlobs`: repo-specific test path globs, reusing the classifier's
  existing override mechanism (find the current key and keep it working; if it
  already exists under another name, keep that name and do not add a second).
- `diffAudit`: `auto` (default) runs the model audit only when an inline-capable
  or suspect file was edited; `always` runs it on every Stage 5 diff; `off`
  disables it.

Reading a config without the object yields the defaults (nothing detected, no
inline extensions, `auto`), so existing repositories keep working.

### Init detection

A `TestLayoutDetector` run by bootstrap after the test-command detection:

1. Enumerate tracked files with `git ls-files`, never a filesystem walk. Drop
   paths with a vendored or generated segment (`node_modules`, `vendor`,
   `third_party`, `dist`, `build`, `out`, `target`, `bin`, `obj`, `.venv`,
   `venv`, `Pods`, `_build`, `deps`, `generated`, `coverage` and the like) and
   lockfiles or generated files (`*.lock`, `package-lock.json`, `*.pb.go`,
   `*.g.dart`, `*_pb2.py`, `*.generated.*`).
2. Strong signals: toolchain marker files at the root and up to three
   directories deep (monorepos keep `Cargo.toml` under `packages/*`). The
   markers that decide an inline membership are `Cargo.toml`, `build.zig` or
   `build.zig.zon`, `dub.json` or `dub.sdl`, `info.rkt`, `Scarb.toml`,
   `main.roc` and `pack.pl`. A marker means the language is present regardless
   of counts.
3. Weak signals: extension counts. Without a marker, a language counts as
   present at or above a floor of about 3 files or 2 percent of the counted
   files; measure the floor on the evaluation repos and put the numbers in the
   commit message. Keep it low for INLINE languages (one `.rs` file with an
   inline test module already breaks path gating) but never zero.
4. Disambiguate the collisions that matter for the inline list: `.pl` is Perl
   (`cpanfile`, `Makefile.PL`, a `t/` directory) or Prolog (`pack.pl`, or file
   content starting with `:- module(` or containing `:- begin_tests(`); when in
   doubt treat it as Perl. `.d` next to a same-stem `.o` is a Makefile depfile,
   not D. The other collisions in the catalog (`.m`, `.v`, `.st`) only move
   languages between SEPARATE and NONE and do not change the inline list.
5. `inlineTestExtensions` is the union of the extensions of every present
   INLINE language, per extension rather than a repo-wide flag, so a Rust and
   Python repository treats `.rs` files as inline-capable and `.py` files as
   separate.
6. Nothing detected, or only NONE languages: write the object with empty lists;
   the classical path gate applies. That is the right default for a static site
   or an infrastructure repository, whose Playwright or Molecule tests live in
   a directory.

The catalog's list of patterns the generic classifier misses goes into the
classifier itself, not into per-repo config, wherever a false positive would be
rare and harmless (a misclassified implementation file is merely kept instead of
reverted, and the scope check still reports it). Start with: every rule becomes
case-insensitive (`Tests/` for SwiftPM, `*.Tests.ps1` for Pester); stem
suffixes `-tests`, `_tests`, `-spec`, `_specs`; stem prefixes `tst_` and
`test-`; stem suffix `_SUITE`; extensions `.t`, `.bats`, `.feature`, `.robot`,
`.plt`, `.pf`; double extensions `*.t.sol`, `*.tftest.hcl`,
`*.testclasses.abap`; camelCase directory segments ending in `Test` or `Tests`
(`commonTest/`, `androidTest/`); directory segments ending in `.Tests`,
`.Test`, `.UnitTests`, `.IntegrationTests`, `.Specs`; directories `e2e/`,
`integration/`, `acceptance/`, `cypress/`, `playwright/`, `testdata/`,
`fixtures/`, `__fixtures__/`, `__mocks__/`, `__snapshots__/`, `features/`,
`molecule/`, `testbench/`. Leave out the ambiguous ones (a bare `test` stem, a
PascalCase `Test` prefix such as `TestRunner.cs`) unless a test proves they
cannot misfire.

### Stage 5 gate

1. Scope check: classify each `testFiles` entry as `separate` (classifier
   match, including extra globs), `inline-capable` (extension in
   `inlineTestExtensions`) or `suspect` (neither). Suspect entries are kept, not
   reverted, but an `author_test_scope_suspect` warn event lists them and the
   ledger says so. The worktree filter keeps reverting everything outside the
   list, as today.
2. The gate always runs when `testFiles` is non-empty and the command is
   usable. Remove the `hasImpl` short-circuit. Strip manifest implementation
   files that are outside `testFiles` (today's behavior), then run the targeted
   command as-is.
3. Outcomes:
   - exit code non-zero: `check: red`.
   - green with something stripped: today's hard failure, unchanged.
   - green with nothing stripped and at least one inline-capable or suspect
     file edited: the model implemented inside its test files or wrote tests
     that do not exercise the change. Re-ask Stage 5 once with a precise
     message that says exactly that and asks for the implementation to be
     removed. Still green: `check: unproven` with reason `green before
     implementation`, a warn event, and the task continues (Stage 10 still
     gates the end result).
   - green with nothing stripped and only separate files: today's
     "already-resolved" branch becomes `check: unproven` with reason
     `no implementation to strip`, plus the warn event. No more silent green.
   - unusable command or placeholder: `check: unproven` with the reason, plus
     the existing warn.
4. Every outcome publishes a Stage 5 `verify_result` through
   `PublishVerifyResultAsync` (command, exit code, stripped files, per-file
   scope verdicts), and status.json carries the check and reason.
5. Model audit (`diffAudit`): when it applies, one cheap-tier call receives the
   Stage 5 diff and answers `{ "implementationHunks": [ { "file": string,
   "reason": string } ] }`. Any hunk reported triggers the same single re-ask
   as a green-before-implementation; the audit never flags on its own and its
   answer is logged as an `author_test_audit` event. Keep the prompt generic:
   it describes hunks that change behavior versus hunks that only assert it.
6. Stage 5's static prompt gains two generic sentences: "Put new tests in a
   separate file from the code under test whenever the test framework allows
   it; when the convention keeps unit tests inside the implementation file, add
   only tests there and no implementation." and "Do not add documentation tests
   or any other test inside an implementation file unless that is where the
   language keeps its unit tests." Nothing language-specific in any prompt;
   the language knowledge lives in the catalog.

### Steps

1. Catalog: `Init/TestLayoutCatalog.cs`, data only (language, extensions,
   marker files, bucket, extra test path patterns), populated from the attached
   `test-layout-catalog.md`; a test pins the bucket membership so a change is
   deliberate.
2. Detector and config: `Init/TestLayoutDetector.cs`, the `authorTests` config
   object with defaults and round-trip, bootstrap wiring, docs for the keys.
3. Classifier gaps: fold the listed patterns into `TestPathClassifier`, with a
   test per pattern and a test that the rules are case-insensitive.
4. Gate: scope check, always-run, outcomes, `verify_result` for Stage 5, the
   re-ask, status.json fields. Keep files at or under 300 lines; split
   `RelayDriver.Stage5.cs` if needed.
5. Audit: the cheap-tier call behind `diffAudit`, its contract and event.
6. Prompt sentences and the prompt tests.
7. Docs: config keys in README.md and docs; new events in
   docs/relay-artifacts.md; a TROUBLESHOOTING.md entry for repositories with
   unusual layouts (what to put in `extraTestPathGlobs` and
   `inlineTestExtensions`, and how to opt a language such as OCaml or Erlang
   into inline handling).

### Tests

- Catalog: every INLINE language has at least one extension and one marker; no
  extension appears in two buckets; the membership snapshot test.
- Detector, on temp repos: Rust only gives `[".rs"]`; Go only gives `[]`; Rust
  plus Python gives `[".rs"]`; HTML plus CSS only gives `[]`; a `Cargo.toml`
  under `packages/` counts; vendored directories are ignored; below-floor noise
  (one `.rs` sample in a Python repo's docs) does not count; a `.pl` Perl
  repo does not become inline-capable.
- Classifier: one test per added pattern; `Tests/FooTests.swift` and
  `Foo.Tests.ps1` classify as tests; `TestRunner.cs` does not (unless proven
  safe).
- Gate: a suspect entry warns and is kept; the gate runs when the manifest has
  no separate implementation file; green-before-implementation re-asks once
  then records `unproven`; stripped-and-green still fails hard; an unusable
  command records `unproven`; a Stage 5 `verify_result` is published in every
  case; an inline-capable file is never reverted.
- Audit: `auto` runs only with inline-capable or suspect edits; a reported hunk
  triggers the re-ask; `off` never calls the model.
- Prompt: both sentences are present and the portability test passes.
- Config: a config without `authorTests` loads with defaults; a config with it
  round-trips.

### Verification (through the Control API)

Run one task each against a Rust repository whose unit tests are inline (for
example colored, see /Users/admin/Dev/vr-work/colored.prep.md) and a Go
repository with sibling test files (gorilla/mux, see mux.prep.md). Expect:
bootstrap writes `[".rs"]` for the first and `[]` for the second; every Stage 5
outcome appears in run.log as a `verify_result` with command and exit code; the
Rust run records `red` (or a re-ask followed by `unproven` with its reason,
never a silent green); the Go run records `red` through the classical strip.
Put the observed events in the commit message body.

## Out of scope

Hunk-level separation of tests from implementation inside one file (it needs
language knowledge); changes to Stage 10; any per-language wording in prompts.

## Appendix: the buckets (full table in test-layout-catalog.md next to this file)

INLINE, the only bucket that changes the default: Rust (`.rs`; `Cargo.toml`),
Zig (`.zig`; `build.zig`, `build.zig.zon`), D (`.d`, `.di`; `dub.json`,
`dub.sdl`), Racket (`.rkt`, `.rktl`; `info.rkt`), Cairo (`.cairo`;
`Scarb.toml`), Roc (`.roc`; `main.roc`), Prolog (`.pl`, `.pro`, `.P`, `.plt`;
`pack.pl`; uncertain, PlUnit documents embedded test blocks as the primary
style with a `.plt` sidecar as the alternative).

Deliberately not inline, although each has an inline mechanism an operator may
opt into through `inlineTestExtensions`: OCaml (`ppx_inline_test`), Erlang
(EUnit behind `-ifdef(TEST)`), Move (in-module `#[test]`), Nim
(`when isMainModule`, `runnableExamples`), Lean 4 (`#guard`), C++ (doctest in
the implementation unit), Odin (`@(test)` procedures), JavaScript and
TypeScript (Vitest in-source).

SEPARATE (82 languages, gated by path): the mainstream compiled and scripting
languages, Python, C, C++, Java, C#, Go, JavaScript, TypeScript, Ruby, PHP,
Swift, Kotlin, Scala, Haskell, Elixir, Erlang, OCaml, Dart, Lua, Perl, R,
Julia, and the rest listed in the catalog.

NONE (110, never decide the strategy): markup and style (HTML, CSS, Sass,
Markdown), data and config (JSON, YAML, TOML, XML), query languages (SQL,
GraphQL, SPARQL, KQL), templates (Jinja, Razor, ERB and friends), shaders and
hardware description, build files (Make, CMake, Starlark, Nix, HCL), most
shells, and languages where automated tests are rare (Scratch, LabVIEW,
COBOL, Assembly). A repository of only these gets the separate-file strategy
with no extras.
