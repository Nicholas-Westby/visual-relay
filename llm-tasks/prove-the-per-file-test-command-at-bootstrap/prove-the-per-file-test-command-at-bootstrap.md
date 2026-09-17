# Prove the per-file test command at bootstrap, or do without one

The author-tests gate (stage 5) runs only the test files a task just wrote,
through `testFileCmd`, a command with a `{files}` token. Bootstrap writes that
command from a table of runner names, and nothing ever runs it before the
first task depends on it. Two things follow. Where the table is wrong the gate
fails for the wrong reason: the jest and vitest forms throw away the project's
own flags, so a project whose script is `jest --config x` gets `npx jest
{files}`. And where the table has no entry (dotnet, go, cargo, swift, maven,
gradle, cmake) the gate runs the whole suite on every task, which is correct
but slow. This task gives the per-file command the treatment the test command
already gets: whatever bootstrap is about to write is first run against real
test files, and a command that does not pass is not written. Where the table
has nothing, or its form fails, the model is asked for one and its answer goes
through the same proof.

This spec replaces the per-file half of
`per-file-test-commands-for-dotnet-go-and-node` (2026-09-11), which prescribed
a shell snippet per toolchain (a `FullyQualifiedName~` loop for dotnet, a
`dirname` loop for go) and a rewrite of the node form. Do it after
`bootstrap-asks-the-model-when-no-test-command-passes`, whose proposer seam
and sandboxed check it reuses.

## Evidence

- Validation of 2026-09-11: bootstrap wrote `testFileCmd: null` for colored
  (`cargo test`) and mux (`go test ./...`); every stage 5 there ran the whole
  suite. Operators hand-wrote forms the table lacks (`node --test {files}`,
  `./node_modules/.bin/ava {files}`), and this repository's own config carries
  a hand-written one for dotnet (`sh tools/dotnet-test-files.sh {files}`).
- `Init/TestCommandDetector.PerFile.cs:78-83` `OneShotForm` answers
  `npx vitest run {files}` or `npx jest {files}` for any command naming the
  runner, whatever flags it had;
  `TestCommandDetectorTests.PerFileForm_JestAndVitest_BecomeTheirOneShotInvocation`
  pins `jest --ci` to `npx jest {files}`. A dropped `--ci` or `--coverage` is
  harmless; a dropped `--config`, `--runInBand` or an env prefix such as
  `NODE_OPTIONS=--experimental-vm-modules` makes the gate command fail before
  any test runs, and a red that comes from a broken command is not proof that
  the new tests fail.
- `TakesTestFilePaths` (`:85-106`) carries a `mix test` form that no detection
  rule can produce.

## Current state (researched at f8a0d07b)

- Seeding: `Init/RelayConfigWriter.cs:38-39` (bootstrap) and `:79-80` (the
  placeholder upgrade) call `TestCommandDetector.PerFileForm(testCommand,
  rootPath)` and write null when it returns null. The two-argument overload
  (`PerFile.cs:43-54`, 627333ec) reads `scripts.test` out of `package.json`
  when the command is a package-manager script.
- Consumption: a null `testFileCmd` falls back to `testCmd` itself
  (`Configuration/RelayConfigLoader.Defaults.cs:14`, `RelayConfigLoader.cs:112`),
  so stage 5 runs the whole suite and the run logs a
  `testfilecmd_no_files_token` warning
  (`Execution/RelayDriver.TestFileCmdWarning.cs:34`). With a token,
  `RelayDriver.Stage5Gate.cs:30-31` and `RelayDriver.Artifacts.cs:145-155`
  substitute the space-joined test files.
- `RelayDriver.Stage5Gate.cs:154-191` `IsGateUnusable` already knows a command
  that ran nothing: exit 127, "no tests found", "no tests collected", zero
  tests.
- `Execution/TestPathClassifier.cs:78` `IsRunnableTestFile` decides which
  tracked paths are test files, and `TestLayoutDetector` already reads the
  tracked file list during bootstrap (`Init/ProjectBootstrapper.cs:99`).
- Visual Relay is meant for repositories whose suite is green. On such a
  repository an existing test file, run alone, passes.

## Prescribed approach

1. The proof. `Init/PerFileCommandProof.cs`: `internal static async
   Task<PerFileProofResult> ProveAsync(string rootPath, string perFileCommand,
   IReadOnlyList<string> testFiles, ITestRunner runner, CancellationToken ct)`.
   It substitutes up to two real test files for `{files}` (two, so a form that
   only works for one file is caught; one when the repository has one) and
   runs the command once. Proven means: the command contains `{files}`, the
   validator accepts the run as a test run, `IsGateUnusable` is false, and the
   exit code is 0. Anything else is a rejection with its reason and output
   head. `IsGateUnusable` moves to a place both the driver and bootstrap can
   call.
2. Which files. From the tracked files, those `IsRunnableTestFile` accepts;
   take the two smallest by size, so the proof is quick. With no test file in
   the repository there is nothing to prove with: write the table's form
   unproven, as today, and say so in the status.
3. Bootstrap proves before it writes. After `testCmd` is accepted:
   - the table has a form: prove it through the same runner the test command
     was checked with; proven, write it;
   - the table has none, or its form was rejected: ask the proposer (the seam
     from the companion task) for a per-file command, showing it `testCmd`,
     the project's own script text when `testCmd` is a package-manager script,
     the two test file paths, and any rejected form with its output; prove
     each answer under the sandboxed runner, up to three answers;
   - nothing proven: write `testFileCmd: null`. The gate then runs the whole
     suite and says so, which is slower and still correct.
   The placeholder upgrade (`UpsertResolvedToolchain`) goes through the same
   steps.
4. The result says what happened. `ProjectBootstrapResult` gains
   `PerFileCommandSource` (`Table`, `Proposed`, `None`, `Unproven`) and the
   status sentence names it, for example `testFileCmd: go test ./pkg/...
   (proposed by the model and proven on 2 test files)` or `no per-file test
   command passed its proof; the author-tests gate will run the whole suite`.
   Rejections join `.relay/setup-check.log`.
5. An operator's own `testFileCmd` is never proven, replaced or removed: the
   proof applies only to what bootstrap itself is about to write.
6. Table upkeep, small: delete the `mix test` form nothing can reach. The
   jest and vitest replacement stays as the table's first guess; the proof now
   catches the projects it is wrong for, and the proposer, shown the project's
   own script, supplies the form that keeps its flags.
7. Docs: `docs/OPERATIONS.md` gains "Per-file test commands": what `{files}`
   receives, that bootstrap proves the command on real test files, and that
   null means the whole suite. `TROUBLESHOOTING.md`'s `gate command unusable`
   row points at it.

## Tests

- `PerFileCommandProofTests` (fake runner): a passing run with a test count is
  proven; exit 1 is rejected with the output head; "no tests found" with exit
  0 is rejected as unusable; a command without `{files}` is rejected without
  running; two files are substituted when two exist, space-joined, and one
  when one exists.
- `ProjectBootstrapperPerFileTests` (fake runner, scripted proposer): the
  table's form proven is written with source `Table`; the table's form
  rejected and a proposal proven writes the proposal with source `Proposed`,
  and the proposer's prompt held the rejected form, its output and the script
  text; three rejected proposals write null with source `None`; no test files
  writes the table's form with source `Unproven`; a null proposer with no
  table form writes null; the proposal ran through the sandboxed runner only.
- `RelayConfigWriterTests.PerFileCommand`: an existing operator-written
  `testFileCmd` survives the placeholder upgrade untouched.
- `TestCommandDetectorTests.PerFileForm`: `mix test` has no form any more.
- Watch each fail first; mutate the proof to accept exit 1 and confirm a test
  catches it.

## Verification (through the control API)

Mac, with a provider key present. For gorilla/mux (go) and a fresh `dotnet new
xunit` project with one passing test and a first commit: `open-folder`,
`bootstrap`, read `testFileCmd` from `.relay/config.json` and the status
sentence. Expected: a proven per-file command for each (the exact text is the
model's), source `Proposed`. Then run one small task in mux and compare the
first stage-5 `verify_result`: its `command` is the per-file form with the
declared test file, `check=red`, and its wall time against a whole-suite run
of the same repository. Finally a vitest or jest project whose script carries
`--config`: the table's form is rejected or proven, and either way the written
command passes on its own test files. Put the three commands, their sources
and the two stage-5 times in the commit body.

## Out of scope

New per-runner forms in the table; proving `testCmd` differently; changing
what `IsRunnableTestFile` admits; re-proving on every run (bootstrap and the
placeholder upgrade only).

## Rejected alternatives

- A shell snippet per toolchain, as the replaced spec asked: each is
  per-language knowledge to keep up, the next runner needs another, and none
  of them is ever run before a task depends on it.
- Rewriting the node form to keep the project's flags: one more special case
  in a table that the proof makes safe to be wrong.
- Proving with a deliberately failing test: a healthy repository's own test
  files passing is the simpler signal, and it needs no file written into the
  operator's tree.
