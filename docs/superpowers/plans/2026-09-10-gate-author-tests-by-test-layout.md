# Gate Author-Tests by Test Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Stage 5's red gate honest for every repository: a data-driven language catalog, init-time detection written to `.relay/config.json`, a classifier that knows the common test-path conventions, a gate that always runs and always records `red`, `unproven` (with a reason) or a hard failure through a Stage 5 `verify_result` event, one model re-ask when the tests were green before any implementation was stripped, and an optional cheap-tier diff audit.

**Architecture:** Five layers, each testable alone. `TestLayoutCatalog` is data. `TestLayoutDetector` turns `git ls-files` into `authorTests` defaults at bootstrap. `TestPathClassifier` gains patterns. `AuthorTestScope` classifies the model's `testFiles` against the config, and the Stage 5 gate in new `RelayDriver` partials turns gate results into outcomes, events and a single re-ask. `AuthorTestDiffAuditor` is one cheap-tier call behind the `diffAudit` setting.

**Tech Stack:** C# / .NET 10, xunit v3, GitSim (in-memory git for tests), the existing scripted subagent-runner test doubles.

**Spec:** `llm-tasks/gate-author-tests-by-test-layout/gate-author-tests-by-test-layout.md` (binding) and `test-layout-catalog.md` beside it (data source). Research notes with file:line anchors: `/private/tmp/claude-501/-Users-admin-Dev-visual-relay/b4a8a6c0-864a-40c1-a57e-c9b1c0ef494f/scratchpad/notes/10-codebase.md` section E and Gotchas.

## Global Constraints

- Commits go directly on `main`, one per task, Conventional Commit subject at most 72 characters, lowercase after the prefix, no trailing period, no em dash anywhere, body of at most three `- ` bullets of at most 20 words each, no changed-file names or path-like tokens in subject or bullets. The pre-commit hook bumps `VERSION` on every commit. Stage only your own files.
- Every `*.cs` and `*.axaml` under `src/`, `tests/`, `tools/` stays at or under 300 lines. `RelayDriver.cs` is at 294 and `RelayDriver.Stage5.cs` at 208: new Stage 5 logic goes into new partials. `RelayConfigLoader.cs` (256) and `RelayConfigWriter.cs` (253) get new partials too.
- `./visual-relay test <ClassName>` runs one class; `./visual-relay test` the suite; `./visual-relay check` must exit 0 with `inspect-code: 0 findings` before each task's commit (it rewrites `docs/images/*.png`; run `git checkout -- docs/images` afterwards).
- Tests never use real git (inject `GitSim` from `tests/VisualRelay.GitSim`; `TestRepository.Create()` gives a temp root), real sleeps, wall-clock waits, `.Result`/`.Wait()`, or `new HttpClient`.
- `DeadConfigFieldGuard`: every `RelayConfig` field the loader parses must be read by name somewhere under `src/` or `tools/`.
- No language-specific wording in any prompt; language knowledge lives in the catalog.
- The repo-specific test-glob key stays `testPaths` (top level, existing; `RelayConfig.TestPaths`). The spec's `extraTestPathGlobs` is NOT added; `testPaths` is that mechanism.
- Event vocabulary (exact names): `verify_result` (existing, now also for stage 5), `author_test_scope_suspect` (warn), `author_test_gate_unusable` (existing warn), `author_test_unproven` (warn), `author_test_reask` (info), `author_test_audit` (info). Check values: `red`, `unproven`. Reasons (exact strings): `green before implementation`, `no implementation to strip`, `gate command unusable`, `placeholder test command`, `no test files declared`.
- `llm-tasks/**` specs are not edited by these tasks.

---

### Task 1: Test layout catalog

**Files:**
- Create: `src/VisualRelay.Core/Init/TestLayoutCatalog.cs` (types, API, the INLINE rows, the ambiguous-extension table), `src/VisualRelay.Core/Init/TestLayoutCatalog.Separate.cs` (SEPARATE rows), `src/VisualRelay.Core/Init/TestLayoutCatalog.None.cs` (NONE rows), `tests/VisualRelay.Tests/TestLayoutCatalogTests.cs`.

**Interfaces:**
- Consumes: nothing.
- Produces:

```csharp
public enum TestLayoutBucket { Inline, Separate, None }

/// One catalog row. Id is a lowercase slug ("rust", "objective-c", "visual-basic");
/// Extensions are lowercase with the leading dot and unique across the whole catalog;
/// Markers are file names ("Cargo.toml") or "*.ext" name patterns ("*.csproj"), unique across the catalog;
/// TestPathPatterns is informational text from the catalog column.
public sealed record TestLayoutLanguage(
    string Id, TestLayoutBucket Bucket,
    IReadOnlyList<string> Extensions, IReadOnlyList<string> Markers, IReadOnlyList<string> TestPathPatterns);

/// An extension two languages share, resolved by the detector, never listed in a row's Extensions.
public sealed record AmbiguousExtension(string Extension, string? DefaultLanguageId, string AlternativeLanguageId);

public static partial class TestLayoutCatalog
{
    public static IReadOnlyList<TestLayoutLanguage> All { get; }      // Inline ++ Separate ++ None
    public static IReadOnlyList<TestLayoutLanguage> Inline { get; }
    public static IReadOnlyList<AmbiguousExtension> Ambiguous { get; } // ".pl" perl/prolog, ".pro" null/prolog, ".p" null/prolog, ".d" d/null
    public static TestLayoutLanguage? ById(string id);
    public static TestLayoutLanguage? ByExtension(string extension);   // OrdinalIgnoreCase, null for unknown or ambiguous
    public static TestLayoutLanguage? ByMarker(string fileName);       // exact name or "*.ext" pattern, OrdinalIgnoreCase
    public static IReadOnlyList<string> InlineExtensionsFor(IEnumerable<string> languageIds); // union over Inline rows among the ids of Extensions plus Ambiguous entries naming that id, distinct, sorted Ordinal
}
```

**Data rules (binding):**
- Populate from `llm-tasks/gate-author-tests-by-test-layout/test-layout-catalog.md`. Bucket membership follows its section 2 exactly for INLINE (rust, zig, d, racket, cairo, roc, prolog). SEPARATE and NONE follow section 2, except that alias rows are merged into their primary (ecmascript into javascript, bourne-shell into bash, glsl-es into glsl, latex into tex, terraform-module into hcl, kotlin-multiplatform into kotlin, caml into ocaml, jscript dropped); say so in a comment at the top of the file that holds the merged row.
- Every extension appears in exactly one row. Resolve collisions by giving the extension to the mainstream language and dropping it from the other row: `.h` to c; `.m` to objective-c; `.v` to verilog; `.st` to smalltalk; `.r` to r; `.js` to javascript; `.sql` to sql (pl-sql keeps `.pks`/`.pkb`, transact-sql keeps none); `.bas` to basic; `.cl` to opencl; `.csh` to c-shell; `.tex` to tex; `.cls` to apex; `.php` to php; `.t` to perl; `.prg` to foxpro; `.yml`/`.yaml` to yaml; `.tf` to hcl. `.pl`, `.pro`, `.p`, `.d` are in `Ambiguous` only (prolog's row lists just `.plt`; d's row lists just `.di`).
- Every marker maps to one row; drop markers shared by several languages (`CMakeLists.txt`, `Makefile`, `meson.build`, `package.json` stays with javascript, `tsconfig.json` with typescript, `build.gradle` with java, `build.gradle.kts` with kotlin, `*.xcodeproj` and other directory markers dropped). The inline-deciding markers are mandatory: `Cargo.toml` (rust), `build.zig` and `build.zig.zon` (zig), `dub.json` and `dub.sdl` (d), `info.rkt` (racket), `Scarb.toml` (cairo), `main.roc` (roc), `pack.pl` (prolog).
- One row per line where possible; keep each partial under 300 lines (split SEPARATE or NONE into two partials if needed).

- [ ] **Step 1: Write the failing tests** in `TestLayoutCatalogTests`: every Inline row has at least one extension (counting Ambiguous entries naming it) and one marker; no extension appears in two rows; no Ambiguous extension appears in any row; no marker maps to two rows; all ids match `^[a-z0-9]+(-[a-z0-9]+)*$`; the Inline id set equals `{cairo, d, prolog, racket, roc, rust, zig}` (sorted snapshot); the Separate and None row counts are pinned as constants with a comment "change deliberately"; `ByExtension(".RS")` is rust; `ByExtension(".pl")` is null; `ByMarker("foo.csproj")` is csharp; `ByMarker("Cargo.toml")` is rust; `InlineExtensionsFor(["rust","python"])` is `[".rs"]`; `InlineExtensionsFor(["prolog"])` is `[".p",".pl",".plt",".pro"]`; `InlineExtensionsFor(["go"])` is empty.
- [ ] **Step 2: Run** `./visual-relay test TestLayoutCatalogTests` — expected FAIL (types missing).
- [ ] **Step 3: Implement** the three partials.
- [ ] **Step 4: Run** the class, then `./visual-relay test`, then `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** `feat(init): add the language test-layout catalog` with a body naming the bucket counts and the alias merges (no file names).

---

### Task 2: `authorTests` config, layout detection at bootstrap, scope classifier

**Files:**
- Create: `src/VisualRelay.Domain/AuthorTestsConfig.cs`, `src/VisualRelay.Core/Configuration/RelayConfigLoader.AuthorTests.cs`, `src/VisualRelay.Core/Init/RelayConfigWriter.AuthorTests.cs`, `src/VisualRelay.Core/Init/TestLayoutDetector.cs` (plus `TestLayoutDetector.Filters.cs` if over 300 lines), `src/VisualRelay.Core/Execution/AuthorTestScope.cs`, tests `AuthorTestsConfigLoaderTests.cs`, `RelayConfigWriterAuthorTestsTests.cs`, `TestLayoutDetectorTests.cs`, `AuthorTestScopeTests.cs`, `ProjectBootstrapperTestLayoutTests.cs`.
- Modify: `src/VisualRelay.Domain/RelayConfig.cs` (property), `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (the `with` block calls the new parser), `RelayConfigLoader.Defaults.cs`, `src/VisualRelay.Core/Init/ProjectBootstrapper.cs` and `ProjectBootstrapResult` (run the detector after the test command is resolved, write the object, expose the detection), `src/VisualRelay.App/ViewModels/MainWindowViewModel.Bootstrap.cs` (one status sentence: "Detected languages: rust (inline tests: .rs)." or "Detected languages: none."), `README.md` (document `authorTests` and `testPaths`), `tests/VisualRelay.GitSim` if `ls-files` is not yet simulated.

**Interfaces:**
- Consumes: `TestLayoutCatalog` (Task 1); `IGitInvoker.RunAsync(root, args, ct)`; `TestPathClassifier.IsTestRelated(path, testPaths)`; `RelayConfigWriter.ReadOrCreateConfig` pattern.
- Produces:

```csharp
// Domain
public sealed record AuthorTestsConfig(
    IReadOnlyList<string> DetectedLanguages,
    IReadOnlyList<string> InlineTestExtensions,   // lowercase, leading dot, distinct, sorted Ordinal
    string DiffAudit)                             // "auto" | "always" | "off"
{
    public const string DiffAuditAuto = "auto"; public const string DiffAuditAlways = "always"; public const string DiffAuditOff = "off";
    public static readonly AuthorTestsConfig Default = new([], [], DiffAuditAuto);
    public static bool IsValidDiffAudit(string? value);
}
// RelayConfig gains: public AuthorTestsConfig AuthorTests { get; init; } = AuthorTestsConfig.Default;

// Core/Init
public sealed record TestLayoutDetection(
    IReadOnlyList<string> DetectedLanguages,        // ordered by file count descending, then id
    IReadOnlyList<string> InlineTestExtensions,
    IReadOnlyDictionary<string, int> CountsByExtension,
    int CountedFiles);
public static class TestLayoutDetector
{
    public static Task<TestLayoutDetection> DetectAsync(string rootPath, IGitInvoker git, CancellationToken ct); // git ls-files, then Detect
    public static TestLayoutDetection Detect(IReadOnlyList<string> trackedPaths, Func<string, string?> readHead); // pure; readHead returns up to 2048 chars of a tracked file or null
}
public static partial class RelayConfigWriter
{
    public static void UpsertAuthorTests(string rootPath, TestLayoutDetection detection); // overwrites detectedLanguages + inlineTestExtensions; adds diffAudit "auto" only when absent; adds top-level testPaths [] only when absent
}

// Core/Execution
public enum AuthorTestScopeKind { Separate, InlineCapable, Suspect }
public sealed record AuthorTestScopeVerdict(string Path, AuthorTestScopeKind Kind);
public static class AuthorTestScope
{
    public static IReadOnlyList<AuthorTestScopeVerdict> Classify(IReadOnlyList<string> testFiles, RelayConfig config); // Separate if TestPathClassifier.IsTestRelated(path, config.TestPaths); else InlineCapable if extension in config.AuthorTests.InlineTestExtensions (ignore case); else Suspect
    public static string Describe(IReadOnlyList<AuthorTestScopeVerdict> verdicts); // "tests/a_test.go=separate;src/x.rs=inline-capable;src/y.go=suspect"
}
```

**Detection rules (binding):**
1. Input is `git ls-files` (never a filesystem walk). Drop any path with a segment (case-insensitive) in: `node_modules`, `vendor`, `third_party`, `thirdparty`, `dist`, `build`, `out`, `target`, `bin`, `obj`, `.venv`, `venv`, `site-packages`, `Pods`, `Carthage`, `bower_components`, `.terraform`, `_build`, `deps`, `generated`, `gen`, `.gradle`, `.next`, `.svelte-kit`, `coverage`. Drop file names ending in `.lock`, exactly `package-lock.json`, `yarn.lock`, `Cargo.lock`, and names ending in `.pb.go`, `.g.dart`, `_pb2.py`, or containing `.generated.`.
2. Markers: a surviving file whose name matches `TestLayoutCatalog.ByMarker` and whose path has at most three directory segments marks its language present.
3. Counting: extension = lowercased last suffix; files without a suffix are skipped. Drop a `.d` file when a same-stem `.o` is tracked in the same directory (Makefile depfile). `.pl`, `.pro`, `.p` files are Prolog when `readHead` returns text whose first non-blank line starts with `:-` or which contains `:- module(` or `:- begin_tests(`; otherwise `.pl` counts for perl and `.pro`/`.p` are skipped. `pack.pl` is the prolog marker.
4. Floors, without a marker: Inline-bucket languages are present at `count >= 2`; Separate-bucket languages at `count >= 3 || (count >= 2 && count * 50 >= CountedFiles)`. None-bucket languages are never reported (they never decide the strategy).
5. `InlineTestExtensions = TestLayoutCatalog.InlineExtensionsFor(DetectedLanguages)`.
6. Nothing detected: empty lists.

**Loader rules:** missing `authorTests` yields `Default`; `inlineTestExtensions` entries are normalized (trim, lowercase, prepend a dot when missing, distinct, sorted) and non-strings ignored; an invalid `diffAudit` falls back to `auto`; `detectedLanguages` is read verbatim (strings only). Round trip: what the writer writes the loader reads back equal.

**Measurement (required by the spec):** run the detector over the three evaluation clones in `/Users/admin/Dev/vr-eval/colored`, `/Users/admin/Dev/vr-eval/mux`, `/Users/admin/Dev/vr-eval/click` (a throwaway harness outside the repo, or a temporary test you delete before committing) and record per repo: counted files, top five extension counts, detected languages, inline extensions, and the nearest below-floor extension. Expected: colored gives `[".rs"]`, mux and click give `[]`. Put the floors and the three results in the commit body.

- [ ] **Step 1: Write the failing tests**: loader (missing object → defaults; full object round-trips; invalid `diffAudit` → auto; extensions normalized), writer (fresh config gets the object and `testPaths: []`; a second upsert overwrites detection but keeps an operator's `diffAudit: "off"` and existing `testPaths`), detector on synthetic tracked lists (Rust only → `[".rs"]`; Go only → `[]`; Rust plus Python → `[".rs"]`; HTML plus CSS only → `[]` and no languages; `packages/core/Cargo.toml` counts; `vendor/x/Cargo.toml` and `node_modules/**` ignored; one `docs/sample.rs` in a Python repo does not count; a Perl repo with `cpanfile` and `.pl` files without Prolog directives is not inline-capable; `.pl` with `:- module(` plus `pack.pl` is prolog; `.d` beside `.o` ignored), scope (separate / inline-capable / suspect / order: `tests/x.rs` is separate even with `.rs` inline; `Describe` format), bootstrap (a GitSim repo with `Cargo.toml` and `src/lib.rs` ends with `authorTests.inlineTestExtensions == [".rs"]` in `.relay/config.json`).
- [ ] **Step 2: Run** the five classes — expected FAIL.
- [ ] **Step 3: Implement**; wire bootstrap and the status sentence; document the keys in README (a short "authorTests" subsection near the other config keys: what each field means, that `testPaths` holds repo-specific test globs, and that an operator can opt a language into inline handling by adding its extension).
- [ ] **Step 4: Measure** on the three clones and write the numbers into the report and the commit body.
- [ ] **Step 5: Run** the classes, `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 6: Commit** `feat(init): detect the test layout and write author-test defaults` with the measured floors in the body.

---

### Task 3: Classifier gaps

**Files:**
- Modify: `src/VisualRelay.Core/Execution/TestPathClassifier.cs` (split into a partial `TestPathClassifier.Patterns.cs` if it passes 300 lines), `tests/VisualRelay.Tests/TestPathClassifierTests.cs` (add cases; split into `TestPathClassifierPatternTests.cs` to stay under 300 lines).

**Interfaces:**
- Consumes/Produces: `TestPathClassifier.IsTestRelated(path, testPaths)` and `IsRunnableTestFile` keep their signatures.

**Rules to add (binding; every rule OrdinalIgnoreCase unless stated):**
- File-name infixes: `.t.`, `.tftest.`, `.tofutest.`, `.testclasses.` (beside the existing `.tests.`, `.spec.`, `.test.`).
- Stem suffixes: `-tests`, `_tests`, `-spec`, `-specs`, `_specs`, `_suite`. Stem prefixes: `tst_`, `test-`.
- Extensions that are always tests: `.t`, `.bats`, `.feature`, `.robot`, `.plt`, `.pf`.
- Directory segment exact names: `e2e`, `integration`, `acceptance`, `cypress`, `playwright`, `testdata`, `fixtures`, `__fixtures__`, `__mocks__`, `__snapshots__`, `molecule`, `testbench`, `step_definitions`. (`features` is deliberately NOT added: feature-module directories are common in application code and a false positive there would let the code-change gate mistake a real fix for tests-only.)
- Directory segment suffixes: `.tests`, `.test`, `.unittests`, `.integrationtests`, `.specs`.
- camelCase directory segments matching `^[a-z][A-Za-z0-9]*Tests?$` with Ordinal comparison (the capital `T` is the signal): `commonTest`, `androidTest`, `jvmTest`, `integrationTest` are tests; `contest`, `latest`, `manifest` are not.
- Not added: a bare `test` stem, a PascalCase `Test` prefix (`TestRunner.cs` must not classify as a test).

- [ ] **Step 1: Write the failing tests**: one `[Theory]` row per new pattern (both a matching and a near-miss path), `Tests/FooTests.swift` and `Foo.Tests.ps1` classify as tests, `TestRunner.cs` does not, `src/contest/x.cs` does not, `SRC/E2E/x.ts` does (case-insensitivity), `features/login.feature` does (extension) but `src/features/login.ts` does not.
- [ ] **Step 2: Run** `./visual-relay test TestPathClassifier` — expected FAIL for the new rows.
- [ ] **Step 3: Implement**; keep rule order: directory rules, then file-name rules, then globs.
- [ ] **Step 4: Run** the classifier tests, `WorktreeFilterTests`, `RelayDriverStage5Tests`, `RelayDriverEarlyImplementationTests`, then `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** `feat(stages): teach the test path classifier the common layouts` with a body noting the deliberate omissions.

---

### Task 4: Stage 5 gate outcomes, re-ask, verify_result, prompt sentences

**Files:**
- Create: `src/VisualRelay.Core/Execution/RelayDriver.Stage5Gate.cs` (outcome evaluation), `src/VisualRelay.Core/Execution/RelayDriver.Stage5Scope.cs` (scope check + events + ledger lines), `src/VisualRelay.Core/Execution/AuthorTestGateOutcome.cs` (types), tests `RelayDriverStage5GateTests.cs`, `RelayDriverStage5ReaskTests.cs`.
- Modify: `RelayDriver.Stage5.cs` (delegate to the new partials; keep under 300), `RelayDriver.cs` (the Stage 5 call site if the re-ask needs a loop there), `RelayDriver.VerifyObservability.cs` (`PublishVerifyResultAsync` gains optional `string? commandOverride = null, IReadOnlyDictionary<string,string>? extraData = null`), `src/VisualRelay.Domain/StageStatus.cs` (`StageStatusEntry.Reason` string?, serialized as `reason` when non-null), `RelayDriver.Artifacts.cs` (`MarkStatusDone` accepts the reason), `RelayStages.cs` (Stage 5 prompt sentences), `docs/relay-artifacts.md` (event and status.json documentation), `TROUBLESHOOTING.md` (entry "Stage 5 records unproven" and "repositories with unusual test layouts": what to put in `testPaths` and `inlineTestExtensions`, how to opt OCaml or Erlang into inline handling), existing tests `RelayDriverStage5Tests.cs`, `RelayDriverStage5GateUnusableTests.cs`, `CodingStageSystemPromptTests.cs`.

**Interfaces:**
- Consumes: `AuthorTestScope.Classify/Describe` (Task 2); `AuthorTestGate.RunAsync`; `RedGate`; `WorktreeFilter.DiscardNonTestEditsAsync`; `PublishVerifyResultAsync`; the existing stage retry mechanism (find how stage 11 feeds verify output back into the next attempt, and how `attempt` is numbered; reuse it for the re-ask instead of inventing a new path).
- Produces:

```csharp
public enum AuthorTestCheck { Red, Unproven }
public sealed record AuthorTestGateOutcome(
    AuthorTestCheck? Check,          // null only when the task was flagged (hard failure)
    string? Reason,                  // one of the Global Constraints reasons when Unproven
    string Command,                  // the targeted command that ran or would have run
    int? ExitCode,                   // null when nothing ran
    IReadOnlyList<string> StrippedFiles,
    IReadOnlyList<AuthorTestScopeVerdict> Verdicts,
    bool ReaskRequested);            // true when the caller must re-ask once and evaluate again
```

**Behavior (binding):**
1. After the worktree filter and manifest merge, classify `testFiles`. Any Suspect entry: `author_test_scope_suspect` warn event (Data: `files`, `scope`) and ledger line `Scope check (stage 5): suspect entries kept: a, b`. Entries are kept.
2. Empty `testFiles`: outcome Unproven, reason `no test files declared`.
3. Placeholder test command (reuse the placeholder detection behind `/state.testCommandIsPlaceholder`): Unproven, reason `placeholder test command`, plus the existing `author_test_gate_unusable` warn.
4. Otherwise the gate ALWAYS runs (the `hasImpl` short-circuit is removed): strip manifest implementation files outside `testFiles` (may be nothing), run the targeted command, restore.
   - Error/Conflict/TimedOut: flag as today (`Check` null).
   - `IsGateUnusable`: Unproven, reason `gate command unusable`, existing warn.
   - exit non-zero: Red.
   - exit zero with something stripped: today's hard failure, unchanged.
   - exit zero, nothing stripped, any InlineCapable or Suspect verdict, and no re-ask yet in this run: `ReaskRequested = true`. The caller re-invokes Stage 5 once (attempt 2) with this exact message appended to the stage input: "Your new tests passed before any implementation was stripped, and at least one file you listed as a test file can carry implementation: <files>. Either the change was implemented inside a test file, or the tests do not exercise the change. Remove every implementation change from the test files so the new tests fail against the current code, or rewrite the tests so they fail. Do not touch implementation files." Publish `author_test_reask` (info, Data: `reason`, `files`) and the ledger line `Re-ask (stage 5): green before implementation`. After the re-ask, run the filter, merge, scope check and gate again; a second green is Unproven with reason `green before implementation`.
   - exit zero, nothing stripped, only Separate verdicts: Unproven, reason `no implementation to strip` (replaces today's silent "Already-resolved" acceptance).
5. Every outcome except a flag publishes a Stage 5 `verify_result` through `PublishVerifyResultAsync` with `commandOverride = outcome.Command` and `extraData = { strippedFiles = "a,b", scope = Describe(verdicts) }`, `check` = `red`/`unproven`, `reason` when unproven, `exitCode` when a command ran. Unproven also publishes `author_test_unproven` (warn, Data: `reason`). status.json's Stage 5 entry carries `check` and `reason`; the ledger carries `Author-test gate (stage 5): red (exit 1), stripped: a, b` or `Author-test gate (stage 5): unproven (<reason>)`.
6. Stage 5 continues to Stage 6 after `unproven` (Stage 10 still gates the end result). `RecheckEarlyImplementationAsync` stays as it is.
7. Prompt: append these two sentences verbatim to Stage 5's static prompt: "Put new tests in a separate file from the code under test whenever the test framework allows it; when the convention keeps unit tests inside the implementation file, add only tests there and no implementation." and "Do not add documentation tests or any other test inside an implementation file unless that is where the language keeps its unit tests."

- [ ] **Step 1: Write the failing tests** with the driver recipe (`TestRepository.Create()`, `ScriptedSubagentRunner.SeedHappyPath`, `RelayDriverTestHelpers.InitSim`, `ScriptedTestRunner`, `InMemoryRelayEventSink`, `RelayDriverDependencies.ForTests`, `RelayDriverOptions.NoGitCommit`): (a) suspect entry warns and is kept; (b) manifest with only `src/control.rs` listed as the test file (`inlineTestExtensions [".rs"]` in the config) and a scripted red result records `check: red` with a Stage 5 `verify_result` whose `command` is the targeted command; (c) green with nothing stripped and an inline-capable file re-asks once (the runner sees Stage 5 twice, the second input contains the re-ask message) and then records `unproven` with reason `green before implementation`, `author_test_reask` and `author_test_unproven` events present; (d) green after stripping still fails hard; (e) exit 127 records `unproven` with reason `gate command unusable`; (f) placeholder command records `unproven` with reason `placeholder test command`; (g) green with only separate files records `unproven` with reason `no implementation to strip`; (h) every case above has exactly one Stage 5 `verify_result` per attempt; (i) an inline-capable file on the list survives the filter and is not in `strippedFiles`; (j) status.json Stage 5 entry has `check` and `reason`; (k) both prompt sentences are present and the portability test passes.
- [ ] **Step 2: Run** the new classes — expected FAIL.
- [ ] **Step 3: Implement**, then update the existing Stage 5 tests whose expectations changed (the "Already-resolved" ledger text and the silent no-check path no longer exist).
- [ ] **Step 4: Document** the events and status fields in `docs/relay-artifacts.md` and the TROUBLESHOOTING entries.
- [ ] **Step 5: Run** `./visual-relay test RelayDriverStage5`, `./visual-relay test CodingStageSystemPromptTests`, `./visual-relay test RelayDriver`, then `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 6: Commit** `feat(stages): make the author-test red gate report every outcome` with a body listing the outcomes and the single re-ask.

---

### Task 5: Diff audit behind `diffAudit`

**Files:**
- Create: `src/VisualRelay.Core/Execution/AuthorTestDiffAuditor.cs`, `tests/VisualRelay.Tests/AuthorTestDiffAuditorTests.cs`, `tests/VisualRelay.Tests/RelayDriverStage5AuditTests.cs`.
- Modify: `RelayDriver.Stage5Gate.cs` or `RelayDriver.Stage5Scope.cs` (run the audit between the scope check and the gate; a reported hunk sets `ReaskRequested` using the same single re-ask), `docs/relay-artifacts.md` (`author_test_audit`), `README.md` (`diffAudit` values).

**Interfaces:**
- Consumes: the way `Execution/FixTaskAuthorRunner.cs` makes a one-off model call through `ISubagentRunner` with a synthetic stage definition and attributes its cost; `IGitInvoker` for `git diff`.
- Produces:

```csharp
public sealed record AuthorTestAuditHunk(string File, string Reason);
public sealed record AuthorTestAuditResult(IReadOnlyList<AuthorTestAuditHunk> ImplementationHunks, string? Error);
public static class AuthorTestDiffAuditor
{
    public static bool ShouldRun(string diffAudit, IReadOnlyList<AuthorTestScopeVerdict> verdicts); // off → false; always → verdicts non-empty; auto → any InlineCapable or Suspect
    public static string BuildPrompt(string diff); // generic: hunks that change behavior versus hunks that only assert it; asks for {"implementationHunks":[{"file","reason"}]}; nothing language-specific
    public static Task<AuthorTestAuditResult> RunAsync(string rootPath, string taskId, string runId, IReadOnlyList<string> testFiles, RelayConfig config, ISubagentRunner runner, IGitInvoker git, IRelayEventSink sink, CancellationToken ct);
}
```

**Behavior (binding):** the diff is `git diff HEAD -- <testFiles>` plus the full content of untracked test files, capped at 60,000 characters with a truncation marker. One cheap-tier call (tier `cheap`), contract as above; the answer is logged as `author_test_audit` (info, Data: `mode`, `hunks`, `files`, `reasons` clipped to 240 characters). Any hunk triggers the single re-ask with the message from Task 4 (the file list is the audited files); the audit never flags a task and never produces `unproven` by itself. A call failure logs `author_test_audit` with `error` and proceeds as if no hunks. Cost is attributed to Stage 5 like other stage costs.

- [ ] **Step 1: Write the failing tests**: `ShouldRun` truth table; `BuildPrompt` contains the contract and no language names; driver tests with `CapturingSubagentRunner`: `off` never invokes the audit; `auto` invokes it only with inline-capable or suspect edits; `always` invokes it for separate-only edits; a scripted hunk answer triggers exactly one re-ask (and no second re-ask when the gate would also request one).
- [ ] **Step 2: Run** — expected FAIL.
- [ ] **Step 3: Implement** and document.
- [ ] **Step 4: Run** `./visual-relay test AuthorTestDiffAuditor`, `./visual-relay test RelayDriverStage5`, `./visual-relay test`, `./visual-relay check`; `git checkout -- docs/images`.
- [ ] **Step 5: Commit** `feat(stages): audit the author-test diff with a cheap model call`.

---

### After the tasks

Verification through the control API (colored for Rust inline tests, gorilla/mux for Go sibling tests, click for Python) is driven by the controller after the plan; the observed `verify_result` events go into the commit that retires the spec into `llm-tasks/completed/`.
