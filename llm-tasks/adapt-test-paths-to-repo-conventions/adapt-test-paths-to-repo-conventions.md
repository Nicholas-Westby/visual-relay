# Adapt Test-File Detection to Each Repo's Conventions

Visual Relay runs on arbitrary repos, but its notion of "this changed file is a test" is largely
hardcoded to one convention (a `tests/` directory plus three filename shapes). Repos name and
place their tests very differently (survey below). On a repo that doesn't match, classification
silently misfires — and three run-critical behaviors depend on it. Make test-file detection adapt
per repo: broaden the built-in heuristics using the survey evidence, and add a repo-specific
config override for the long tail.

## Current state (researched)

- **The classifier** — `src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs`,
  `private static bool IsTestFile(string path)`. Its complete rule set today (case-insensitive,
  backslash-normalized): path starts with `tests/` or contains `/tests/`; filename contains
  `.tests.`; filename (sans extension) ends with `_test`; filename contains `.spec.`.
  Note the file is at **289 lines — effectively at the 300-line guard ceiling**
  (`tools/VisualRelay.Guards`), so new classification logic must live in a **new file**, not here.
- **Consumer 1: targeted test runs** — `RelayDriver.Artifacts.cs`,
  `BuildTargetedTestCommand(RelayConfig config, IReadOnlyList<string> manifest)`: replaces the
  `{files}` token in `testFileCmd` with `manifest.Where(IsTestFile)`; falls back to the full
  `testCmd` when no manifest file classifies as a test. Missed conventions mean targeted runs
  silently degrade to full-suite runs (slow), and misclassified impl files could be passed to the
  test runner as test files.
- **Consumer 2: the TDD early-implementation gate** —
  `src/VisualRelay.Core/Execution/EarlyImplementationDetector.cs`
  (`ImplementationAlreadyUnderwayAsync(..., isTestFile: IsTestFile)`), invoked from
  `RelayDriver.cs` and `RelayDriver.Stage5.cs`. Test files are *excluded* when deciding whether an
  agent front-loaded implementation. On a repo whose tests don't match the heuristic (e.g. Jest's
  `Button.test.tsx`), the test files a stage legitimately writes are counted as *implementation*,
  and the gate misfires.
- **Consumer 3: the code-change gate** — `src/VisualRelay.Core/Execution/RelayDriver.CodeChangeGate.cs`:
  `.Any(f => IsImpl(f) && !IsTestFile(f))` — "did this stage change real code?" A tests-only
  change on a non-conforming repo wrongly counts as an implementation change.
- **Init-time detection** — `src/VisualRelay.Core/Init/TestCommandDetector.cs` uses
  `Directory.Exists(Path.Combine(rootPath, "tests"))` as its weakest-priority Python signal
  (`pytest`); a repo using `test/` (see pytorch below) misses even that.
- **Repo config precedent** — `.relay/config.json` already repo-scopes `testCmd`/`testFileCmd`;
  arrays are parsed in `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` via
  `OptionalStringArray(root, "boostTurnsTaskIds", [])` etc., with record fields on
  `src/VisualRelay.Domain/RelayConfig.cs`. Classification is the only piece that is *not*
  configurable today.
- **Existing philosophy** — `src/VisualRelay.Core/Execution/GitCommitter.Untracked.cs` /
  `GitCommitter.cs` comments: committing deliberately does *not* assume a `src`/`tests`/`tools`
  layout "since Visual Relay runs on any repo". Classification should live up to the same bar.

## Survey: where 40 popular repos actually keep tests

Gathered 2026-07-06 via the GitHub API (top-level listing per repo, plus targeted subdirectory
checks). Directory names are verbatim.

| Convention | Repos (language) |
| --- | --- |
| `tests/` top-level | TypeScript, django (`tests/` + `js_tests/`), flask, requests, rust-lang/rust, ripgrep, redis, laravel/framework, Avalonia (C#) |
| `test/` top-level (singular) | nodejs/node, express, next.js, pytorch, golang/go, kubernetes, PowerShell, jekyll, phoenix (Elixir), elasticsearch (+ `qa/`), llvm (`test/` + `unittests/` per project) |
| `spec/` top-level (RSpec) | mastodon, discourse (has `spec/` *and* `test/`) |
| `t/` top-level | git (`t/t0000-*.sh`) |
| `Tests/` capitalized | Alamofire (Swift); symfony nests `Tests/` inside each `src/Symfony/**` component |
| Nested per-package `tests/` | pandas (`pandas/tests/`), scikit-learn (`sklearn/**/tests/`), dotnet/runtime & aspnetcore (`src/<lib>/tests/`), tokio (per-crate `tests/` + `tests-integration/`, `tests-build/`, `stress-test/`) |
| `src/test/` (Maven/Gradle) | spring-boot (`core/spring-boot/src/{main,test,testFixtures}/`), guava (top-level `guava-tests/`, `integration-tests/`), elasticsearch modules, kafka-style JVM repos generally |
| Colocated test *files*, no test dir | golang/go & gin & hugo (`*_test.go` beside impl; `testdata/` fixture dirs), rust crates (inline `#[cfg(test)]` modules), react (`packages/*/src/__tests__/*-test.js`), vuejs/core & svelte (`packages/*/__tests__/`) |
| Suffix/prefix dir variants | serde (`test_suite/`), spring-boot (`integration-test/`, `smoke-test/`, `system-test/`, `test-support/`), hugo (`htesting/`, `testscripts/`), jekyll (`features/` cucumber), pandas (`_testing/`) |
| Basically everything at once | linux (`tools/testing/`, colocated KUnit), postgres (`src/test/`) |

Filename conventions in the wild: `*_test.go` / `*_test.py` (caught today); `test_*.py` (pytest
default — **missed**); `*.test.ts` / `*.test.tsx` / `*.test.js` (Jest default — **missed**;
only `.tests.` and `.spec.` are caught); `*-test.js` (react — **missed**); `*_spec.rb` (RSpec —
**missed**); `FooTest.java` / `FooTests.cs` / `FooTest.php` / `FooTests.swift` (JUnit/xUnit/
PHPUnit/XCTest suffix — **missed** by filename, saved only when they also sit under a `tests/`
dir); `t/tNNNN-*.sh` (git — **missed**).

Directory-name gaps vs. today's `tests/`-only rule: `test/`, `spec/`, `t/`, `__tests__/`,
`unittests/`, `src/test/…`, `testdata/` (Go fixtures), plus the suffixed families
(`*-test/`, `test-*/`, `tests-*/`, `*_test(s)/`, `test_suite/`). The existing checks are already
case-insensitive, which covers `Tests/`.

## What to build

1. **A dedicated classifier in a new file** (e.g.
   `src/VisualRelay.Core/Execution/TestPathClassifier.cs`), TDD-first, replacing the private
   heuristic. Layered design:
   - **Built-in directory rule:** a path is a test path when *any* path segment matches, case-
     insensitively: exact names `test`, `tests`, `spec`, `specs`, `t`, `__tests__`, `unittests`,
     `testdata`, `test_suite`, `testfixtures`, `htesting`; or a segment that starts with `test-`/
     `tests-`/`test_`, or ends with `-test`/`-tests`/`_test`/`_tests`. Segment matching (not
     substring) keeps `attestation/`, `contest/`, `latest/` from false-positiving.
   - **Built-in filename rules:** keep today's four; add `.test.` infix, `test_` prefix,
     `-test` suffix, `_spec` suffix (all case-insensitive, on the name sans final extension where
     that's the natural reading); add the PascalCase `…Test`/`…Tests`/`…Spec` suffix **only** for
     extensions where that is the ecosystem convention (`.java`, `.kt`, `.cs`, `.php`, `.swift`)
     to keep false positives down elsewhere.
   - **Repo override:** new optional `.relay/config.json` key `testPaths` — an array of
     root-relative globs (e.g. `["spec/**", "**/*_spec.rb"]`) that classify as test paths **in
     addition to** the built-ins. Parse via the `OptionalStringArray` pattern in
     `RelayConfigLoader`, new `RelayConfig` record field, default `[]`. (If during implementation
     a repo clearly needs to *suppress* a built-in match, an optional `nonTestPaths` deny-list is
     acceptable scope — otherwise skip it.)
2. **Wire all consumers through it:** `BuildTargetedTestCommand`, both
   `EarlyImplementationDetector` call sites (`RelayDriver.cs`, `RelayDriver.Stage5.cs`), and
   `RelayDriver.CodeChangeGate.cs`. Config is in hand at these call sites, so the configured
   globs can reach the classifier. Update `TestCommandDetector`'s weak Python signal to accept
   `test/` alongside `tests/`.
3. **One refinement worth making while here:** `BuildTargetedTestCommand` should pass only
   *runnable test code* to `{files}` — a classified-as-test path whose extension is non-code
   (the `NonCodeExtensions` set in `RelayDriver.Artifacts.cs`: `.md`, `.json`, `.yaml`, …, i.e.
   fixtures under `testdata/` or `tests/fixtures/`) must not be handed to the test runner, while
   still counting as test-related for the impl-exclusion gates. Two predicates (test-related vs.
   runnable-test-file) or an equivalent single design — your call, but cover it with tests.
4. **Test matrix** (new test file, xUnit `[Theory]`): drive the classifier with cases lifted from
   the survey table — at minimum one positive per row above (`test/foo.c`, `spec/user_spec.rb`,
   `t/t0001-init.sh`, `src/test/java/FooTest.java`, `packages/x/__tests__/y.js`,
   `Button.test.tsx`, `ReactHooks-test.js`, `test_config.py`, `fmt/errors_test.go`,
   `Sources/../AlamofireTests.swift`, `tokio/tests-integration/…`, `testdata/golden.json`
   [test-related but not runnable]) — and negatives (`attestation/keys.cs`, `contest.py`,
   `latest.ts`, `src/detest/foo.cs`, `protest/readme.md`). Plus: config `testPaths` globs
   classify; empty config preserves every current-behavior case (all four existing rules must
   still pass verbatim).

## Done when

- The classifier passes the full matrix; every convention row in the survey table classifies
  correctly out of the box, and `testPaths` handles an arbitrary extra convention.
- All three run-time consumers and `TestCommandDetector` share the classifier; no behavior change
  on repos that matched the old rules (this repo's own `tests/VisualRelay.Tests/**` keeps
  classifying identically).
- Fixture-style non-code files under test dirs are excluded from `{files}` expansion but excluded
  from impl gates too.
- `./visual-relay check` passes (guards, format, build, full suite).

## Guardrails

- **Bias toward impl when genuinely ambiguous**: misreading an impl file as a test weakens the
  TDD gates (`IsImpl` already documents fail-safe-toward-requiring-a-test; keep that spirit).
  Prefer segment-exact directory matches and the scoped filename rules above over anything
  fuzzier; the `testPaths` override exists for the long tail.
- `RelayDriver.Artifacts.cs` is at 289/300 lines — the classifier and its rules go in a new file;
  keep every touched file under the 300-line ceiling (`tools/VisualRelay.Guards`).
- Do not change `testCmd`/`testFileCmd` semantics or any config key names; `testPaths` is purely
  additive and optional. No settings-UI work in this task.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs.
