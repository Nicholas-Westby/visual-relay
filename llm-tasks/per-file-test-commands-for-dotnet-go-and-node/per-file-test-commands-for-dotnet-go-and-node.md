# Seed a real per-file test command for dotnet, go and node

The stage-5 gate is only as narrow as `testFileCmd`. Bootstrap seeds one only
for runners that take file paths as trailing arguments, so a .NET, Go or Rust
repository gets `null` and the gate runs the whole suite, saying so in
`verify_result`. That is honest but slow, and for two of the three it is
unnecessary: this repository's own config narrows `dotnet test` through a
twenty-line script that maps each authored test file to a
`FullyQualifiedName~<Class>` filter, and `go test` takes package paths, which
every test file has. At the same time the detector formats per-file forms for
`rspec`, `phpunit` and `mix test` that no detection rule can ever produce, and
replaces a project's own `vitest`/`jest` script with a bare `npx … {files}`,
dropping the flags the project put there. This task seeds a per-file form for
dotnet and go, keeps the project's own node invocation, and gives the three
orphaned forms the detection rules they were written for.

## Evidence

- Validation of 2026-09-11 (item 3): bootstrap wrote `testFileCmd: null` for
  colored (`cargo test`) and mux (`go test ./...`); every Stage 5 there ran
  the whole suite. The prep notes hand-wrote forms detection could have
  seeded: `commander.js.prep.md:9` `node --test {files}`, `ky.prep.md:7`
  `./node_modules/.bin/ava {files}`; `mux.prep.md:7` and `colored.prep.md:9`
  left `null`, the latter as "not natural for cargo".
- This repository's `.relay/config.json` sets `testFileCmd` to
  `sh tools/dotnet-test-files.sh {files}`; the script (`tools/dotnet-test-files.sh:10-18`)
  takes each `.cs` argument, strips the directory, `.cs` and everything after
  the first dot, joins `FullyQualifiedName~<stem>` with `|`, and runs
  `dotnet test --filter`. `TargetedTestCommandTests.BuildTargetedTestCommand_DotNetTestFileUnderTests_ReturnsTargetedCommand`
  (`:89`) pins that shape end to end.
- Review of 2026-09-11 (item 4): `TakesTestFilePaths` (`Init/TestCommandDetector.cs:204-225`)
  accepts `mix test`, `rspec`, `mocha`, `phpunit`; `DetectCandidates`
  (`:41-141`) has no rule for `mix.exs`, `phpunit.xml` or `.rspec`, and the
  Ruby rule (`:115-120`) yields `bundle exec rake test` then `bundle exec rake`.
  `OneShotForm` (`:197-202`) returns `npx vitest run {files}` and
  `npx jest {files}` for any command naming the runner, whatever flags it had;
  `TestCommandDetectorTests.PerFileForm_JestAndVitest_BecomeTheirOneShotInvocation`
  (`:41`) pins the replacement.

## Current state (researched at d5bf93cc)

- `PerFileForm` (`:174-188`): a command with `{files}` is kept; a chain, pipe,
  redirect or `;` gets null; else `OneShotForm` or, when `TakesTestFilePaths`,
  the command plus ` {files}`. The chain rule judges the input command, so a
  form the detector authors itself may contain a `;`.
- Seeding: `Init/RelayConfigWriter.cs:36-40` (bootstrap) and `:76-80` (the
  placeholder upgrade) both call `PerFileForm(testCommand)` and write null
  when it returns null.
- Consumption: `RelayDriver.Artifacts.cs:145-155` `BuildTargetedTestCommand`
  replaces `{files}` with the space-joined manifest files that
  `TestPathClassifier.IsRunnableTestFile` (`Execution/TestPathClassifier.cs:78-84`)
  accepts, else falls back to `testCmd`; `RelayDriver.Stage5Gate.cs:30-31`
  substitutes the declared `testFiles` the same way. Every test command runs
  through `/bin/sh` (`SandboxedTestRunner(new ShellTestRunner(…))`), which is
  what lets the CMake candidate carry `&&` (`:126-131`).
- `IsGateUnusable` (`RelayDriver.Stage5Gate.cs:144-163`): exit 127, "no tests
  found", "no tests collected", `ZeroTestsPattern`, "zero tests". `dotnet test`
  with a filter that matches nothing prints "No test matches the given
  testcase filter" and exits 0; that phrase is not in the list.
- Tests: `TestCommandDetectorTests` and its `.PerFileForm`, `.Python`, `.Jvm`,
  `.RubyCMake` partials; `RelayConfigWriterTests.PerFileCommand` (three facts);
  `TargetedTestCommandTests` (thirteen).

## Prescribed approach

1. dotnet. `PerFileForm("dotnet test …")` returns the script's mapping inline,
   with the detected command's own tokens kept ahead of the filter:
   `f=; for p in {files}; do n=${p##*/}; n=${n%.cs}; n=${n%%.*};
   f="${f:+$f|}FullyQualifiedName~$n"; done; dotnet test --filter "$f"`
   (the `dotnet test` and any flags come from the detected command). The
   form is a `DotnetForm(tokens)` beside `OneShotForm`, with the mapping
   string as one named constant and a comment pointing at
   `tools/dotnet-test-files.sh` as its origin. `IsGateUnusable` gains
   "no test matches", so a stem that names no class records
   `unproven: gate command unusable` instead of a green.
2. go. `PerFileForm("go test [flags] ./...")` drops the trailing `./...` and
   appends the package list of the files:
   `go test [flags] $(for p in {files}; do printf './%s\n' "$(dirname "$p")"; done | sort -u)`.
   Package scope is the narrowest form `go test` has, and a root-package test
   file yields `./.`, which is valid. The flags stay the project's (mux's
   `-count=1` survives).
3. node keeps its own invocation. `OneShotForm` becomes `NodeForm`: locate the
   `vitest` or `jest` token; for vitest insert `run` after it unless the next
   token already is `run`, and drop `--watch`, `-w` and `--ui`; for jest drop
   `--watch` and `--watchAll` (never `-w`, which is `--maxWorkers`); prefix
   `npx` when the first token is a bare binary (not a path, not `npx`, `npm`,
   `node`, `yarn`, `pnpm`, `bun`); append ` {files}`. So `vitest --coverage`
   gives `npx vitest run --coverage {files}` and `cross-env NODE_ENV=test jest --ci`
   gives `npx cross-env NODE_ENV=test jest --ci {files}`. `TakesTestFilePaths`
   gains `node --test` and `ava`, the two forms the prep notes wrote by hand.
4. The orphaned forms get their rules, in `DetectCandidates`: Elixir, `mix.exs`
   gives `mix test` (after Swift); PHP, `phpunit.xml` or `phpunit.xml.dist`
   gives `vendor/bin/phpunit` when that file exists, else `phpunit` (after
   Ruby); Ruby, a `.rspec` file or a `spec/` directory beside the Gemfile
   puts `bundle exec rspec` ahead of the two rake candidates. `mocha` needs no
   rule: it arrives as a package.json script, which is detection.
5. cargo stays null, as `colored.prep.md` judged: `cargo test <filter>`
   selects by test name, and a file-to-module mapping is wrong for
   integration tests and doc tests alike.
6. Docs: `docs/OPERATIONS.md` gains a short "Per-file test commands" section
   listing what bootstrap seeds per toolchain and that `null` means the gate
   runs the whole suite and says so; TROUBLESHOOTING.md's
   `gate command unusable` row (`:82`) gains the dotnet filter case.

## Tests

- `TestCommandDetectorTests.PerFileForm`: `DotnetTest_MapsFileStemsToAFullyQualifiedNameFilter`
  (the form contains the loop, `FullyQualifiedName~` and `dotnet test --filter`);
  `DotnetTestWithFlags_KeepsTheFlagsAheadOfTheFilter`;
  `GoTest_ReplacesTheWildcardWithThePackagesOfTheFiles` (`go test -count=1 ./...`
  keeps `-count=1`); `Vitest_KeepsItsFlagsAndInsertsRun`;
  `VitestRun_IsNotDoubled`; `Jest_KeepsItsFlagsAndDropsWatch`;
  `JestMaxWorkersShortFlag_Survives`; `ALauncherAheadOfTheRunner_IsPrefixedWithNpx`;
  `NodeTest_AndAva_TakeFilePaths`. `PerFileForm_JestAndVitest_BecomeTheirOneShotInvocation`
  is replaced by the vitest and jest facts above.
- `TestCommandDetectorTests.Elixir`, `.Php`, `.RubyCMake`: `MixExs_OffersMixTest`;
  `PhpunitXml_OffersTheVendorBinaryWhenPresentElseTheTool`;
  `Rspec_IsOfferedAheadOfRake`; the existing rake facts keep their order
  when no rspec marker exists.
- `RelayConfigWriterTests.PerFileCommand.Write_ADotnetToolchain_SeedsTheFilterForm`
  and `Write_AGoToolchain_SeedsThePackageForm`.
- `TargetedTestCommandTests.BuildTargetedTestCommand_GoForm_ExpandsInsideTheSubstitution`
  (the `{files}` inside `$( … )` is substituted like any other).
- A shell-level fact, real `sh`, in `DotnetPerFileFormShellTests`: the seeded
  dotnet form with `{files}` replaced by `tests/A/FooTests.cs tests/B/Bar.Tests.cs`
  and `dotnet` shadowed by a stub on PATH receives
  `--filter FullyQualifiedName~FooTests|FullyQualifiedName~Bar`; the go form
  with `a/x_test.go b/c/y_test.go a/z_test.go` hands a stub `go`
  `test ./a ./b/c`.
- `RelayDriverStage5GateTests`: output "No test matches the given testcase
  filter" with exit 0 records `unproven: gate command unusable`.

## Verification (through the control API)

Create `/Users/admin/Dev/vr-work/xunit-probe` with `dotnet new xunit`, one
passing test in `UnitTest1.cs`, `git init` and a commit; clone gorilla/mux as
`mux.prep.md` describes. For each: `open-folder`, `bootstrap`, then

    jq -r .testFileCmd <clone>/.relay/config.json
    curl -s -X POST -d '{"title":"Probe task","body":"<a mux task from the validation>\n"}' http://127.0.0.1:8765/command/create-task
    curl -s -X POST http://127.0.0.1:8765/command/run-all
    # poll /state until isBusy is false, then:
    grep verify_result <clone>/.relay/probe-task/run.log | head -1

Expected: the xunit config carries the dotnet filter form and mux the go
package form (colored still `null`); mux's first stage-5 `verify_result`
names `go test -count=1 ./.` (or the packages of its declared files), not
`./...`, with `check=red`; the xunit run's names `--filter
"FullyQualifiedName~<the declared file's stem>"`. Put both commands and the
stage-5 wall time against the whole-suite run of 2026-09-11 in the commit body.

## Out of scope

JVM per-file forms (`-Dtest=`, `--tests`): the same loop shape serves them and
they get their own task once a Java repository is in the validation set;
cargo; a `.mocharc` rule; changing what `IsRunnableTestFile` admits.

## Rejected alternatives

- A driver-side `{stems}` placeholder joined per runner: one toolchain wants
  `|FullyQualifiedName~`, the next wants `,`, the third `--tests` per item; the
  knowledge would live in the driver instead of in the one config line the
  operator can read and edit, and the shell already does the mapping.
- Copying `tools/dotnet-test-files.sh` into the target repository: bootstrap
  writes only `.relay/`, and a file the task's diff would then carry is not
  the harness's to add.
- Dropping the rspec, phpunit and mix forms instead of adding rules: the forms
  are right, and each rule is one marker file; dead code was the cheaper
  mistake to fix the other way.
- `cargo test <module>` from the file stem: wrong for `tests/*.rs` (which
  wants `--test <name>`), for doc tests and for a `mod tests` that names the
  file differently; a whole-suite `cargo test` on the crates seen so far runs
  in seconds.
