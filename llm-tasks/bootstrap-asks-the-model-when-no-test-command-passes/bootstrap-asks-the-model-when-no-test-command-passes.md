# Bootstrap asks the model for a test command when none of its own passes

Bootstrap finds a project's test command from a table of marker files
(`package.json`, `Cargo.toml`, `*.sln` ...) and keeps the first candidate that
passes a real run. When every candidate fails, or the table knows no marker in
the folder, it writes a placeholder and tells the operator to "add a task that
scaffolds the project", although the repository may have a working suite one
folder away. Each such repository has so far been answered with more
per-language rules. This task adds one general fallback instead: a small agent
run, with the same tools any stage has, looks through the repository for the
command (its CI configuration, README and build files say what the project
itself runs), and bootstrap keeps the answer only if it passes the same check
as a built-in candidate. It also makes the failure message say what was tried.

This spec replaces `bootstrap-dotnet-tests-under-the-testing-platform`
(2026-09-14), which prescribed reading `global.json` and scanning project files
for six test SDK markers, and the detection half of
`per-file-test-commands-for-dotnet-go-and-node` (2026-09-11). Visual Relay is a
general-purpose tool; knowledge of one SDK's project files goes out of date
and helps one ecosystem.

## Evidence

- ThreeMammals/Ocelot, Mac, 2026-09-14. `global.json` selects
  Microsoft.Testing.Platform. `dotnet test Ocelot.slnx` exits 1 ("All projects
  must use that test runner ... Ocelot.Benchmarks.csproj"),
  `dotnet test Ocelot.Samples.slnx` exits 1 ("No test projects were found"),
  and `dotnet test --project unit/Ocelot.UnitTests.csproj -f net10.0` passes
  1,922 of 1,923 tests in 11 s. Bootstrap (0.379) wrote the placeholder with
  the scaffolding advice. The first rejection's own output names the project
  that is in the way, and the working command is one look at the repository's
  folders away.
- The same session, Windows: crawl (its Makefile is two folders down) also got
  the placeholder. No marker rule covers it, and it would need its own.
- A model-based guess exists and bootstrap never calls it:
  `Init/LlmTestCommandFinder.cs` sends the root's first 100 entry names in one
  prompt and reads one line back (`Init/ProviderTestCommandCompleter.cs`,
  `cheap` tier). Its only caller is the GUI's manual "find test command" action
  (`MainWindowViewModel.Execution.cs:167-188`). A list of root entry names is
  thin evidence: it cannot show a nested test project, a script's text or what
  CI runs.

## Current state (researched at f8a0d07b)

- `Init/ProjectBootstrapper.cs:176-220` `ResolveTestCommandAsync`: candidates
  from `TestCommandDetector.DetectToolchainCandidates`; each is run through
  `TestCommandValidator.ValidateAsync` and the first `Accepted` wins. When all
  are rejected it returns the placeholder with a `SetupCheckDiagnostic` built
  from every rejection (`:211-216`; the full list goes to
  `.relay/setup-check.log`). With no candidate at all it returns the
  placeholder with no diagnostic (`:219`).
- The built-in check runs in the operator's checkout through a bare shell
  (`CreateValidationRunner`, `:80-81`, `ShellTestRunner`, no nono).
- The pattern for a helper agent run outside the twelve stages exists:
  `Execution/FixTaskAuthorRunner.cs:40-100` builds a `RelayStageDefinition`
  with `Number: 0` and a `StageInvocation` on the real root, with its trace
  and report under `.relay/<task>/fix-task/`, and runs it through an
  `ISubagentRunner` from `SubagentRunnerFactory.Create`
  (`MainWindowViewModel.FixTask.cs:63-66`). That runner has the normal tool
  catalog, and its command tool runs inside the sandbox.
- The pipeline's own test runner is `SandboxedTestRunner(new
  ShellTestRunner(...), config, ...)` (`MainWindowViewModel.RunOne.cs:29-33`);
  `RelayConfigLoader.Defaults(command)` yields a config for a command when no
  file exists yet (`ShellTestRunner.cs:79-80` already uses it), and
  `SandboxedStage.MissingRequiredTools` says whether the sandbox can run on
  this host.
- `App/ViewModels/MainWindowViewModel.Bootstrap.cs:63-71` `DescribeBootstrap`:
  the placeholder headline is the same whether the folder is empty or every
  candidate was rejected, and it never reads `result.SetupCheck`.
  `MainWindowViewModelTests.Bootstrap.cs:118` pins its opening words
  ("placeholder test command set").
- `TestLayoutDetector.DetectAsync` (`ProjectBootstrapper.cs:99`) already counts
  the tracked files by extension (`CountsByExtension`, `CountedFiles`), so
  bootstrap knows whether the folder holds source files.
- The three surfaces that resolve a command all go through
  `ResolveTestCommandAsync`: CLI init (`tools/VisualRelay.Init/Program.cs:14`),
  the GUI button (`MainWindowViewModel.Bootstrap.cs:41`) and the placeholder
  upgrade (`MainWindowViewModel.RunnableGate.cs:29`).

## Prescribed approach

1. The proposer. `Init/TestCommandProposer.cs`, modeled on
   `FixTaskAuthorRunner`: `RunAsync(string rootPath, IReadOnlyList<CommandAttempt>
   attempts, RelayConfig config, ISubagentRunner runner, CancellationToken ct)`
   returns the proposed command or null. It builds a `Number: 0` stage named
   `TestCommandProposer` on the `cheap` tier with the contract `{ "testCmd":
   string, "evidence": string }` and `MaxTurns: 30`, on the real root, with
   its trace and report under `.relay/bootstrap/`. The system prompt: find the
   one shell command that, run from the repository root, runs this project's
   unit tests once and exits (no watch mode); look at the CI configuration,
   the README and the build files; you may run commands to check your answer;
   do not edit, create or delete any file. The task input lists the attempts
   so far: each command, its exit code and the first 30 lines of its output.
2. A seam keeps bootstrap testable. `BootstrapAsync` and
   `TryUpgradePlaceholderTestCommandAsync` take an optional
   `Func<IReadOnlyList<CommandAttempt>, CancellationToken, Task<string?>>?
   proposeCommand`. The GUI and the CLI init build it from
   `SubagentRunnerFactory.Create` over `RelayConfigLoader.Defaults(...)` and
   `TestCommandProposer`. Null skips the fallback; so does a machine with no
   provider key, where the proposer is never started.
3. When it runs. After the built-in candidates: every candidate was rejected,
   or none was detected and `layout.CountedFiles > 0`. A folder with no
   tracked source files is greenfield and keeps today's placeholder and
   advice. The agent's commands need the sandbox, so when
   `MissingRequiredTools` reports it cannot run here the fallback is skipped
   and the diagnostic says so.
4. The answer is checked like any candidate, under the sandbox. The proposal
   goes through the same `TestCommandValidator`, over a `SandboxedTestRunner`
   built from `RelayConfigLoader.Defaults(proposal)`: the wrapper the pipeline
   will run the command under anyway, and the right place for a command
   written by a model that has been reading an unfamiliar repository.
5. Two rounds at most. A rejected proposal joins the attempts and the proposer
   runs once more; a repeated answer ends it. The first accepted proposal
   becomes `testCmd`. Every proposal and its rejection goes into the same
   `SetupCheckDiagnostic` list, so `.relay/setup-check.log` shows everything
   that was tried.
6. One way to ask. The GUI's manual "find test command" action uses the
   proposer too, and `LlmTestCommandFinder` and `ProviderTestCommandCompleter`
   are deleted with their tests, since nothing else calls them.
7. The result says where the command came from. `ProjectBootstrapResult` gains
   `TestCommandSource` (`Detected`, `Proposed`, `Placeholder`). The status
   sentence for a proposed command reads `testCmd: <command> (proposed by the
   model and checked; review it in .relay/config.json)`.
8. An honest failure message. When the folder has source files and nothing
   passed, the headline is `No test command passed the check (<candidate>:
   <first line of its rejection>; ...). Set testCmd in .relay/config.json.` and
   the scaffolding advice is kept for the greenfield case only. At most three
   attempts are named; the log has the rest.
9. Docs: `docs/OPERATIONS.md` gains a short "How bootstrap finds the test
   command" section (built-in candidates, then the proposer, then the
   placeholder; what the proposer costs and where its report is);
   `AGENTS.md`'s `bootstrap` entry gains the clause about a proposed command;
   `TROUBLESHOOTING.md` gains "Bootstrap wrote the placeholder on a repository
   that has tests".

## Tests

- `TestCommandProposerTests` (a fake `ISubagentRunner`): the invocation is a
  `Number: 0` stage on the cheap tier with the contract above, the root as its
  target and its trace under `.relay/bootstrap/`; the task input renders each
  attempt as command, exit code and output head; a valid answer returns its
  `testCmd`; an invalid or empty answer returns null.
- `ProjectBootstrapperProposalTests` (new; fake runners and a scripted
  proposer seam):
  - all built-in candidates rejected, the first proposal accepted: `testCmd` is
    the proposal, the source is `Proposed`, and the proposer was handed the
    rejected commands with their output;
  - the proposal was validated through the sandboxed runner, never the bare
    one (two recording runners, assert which saw the proposal);
  - two rejected proposals give the placeholder, and the diagnostic lists
    built-in and proposed attempts in order;
  - a repeated proposal ends the fallback after one validation;
  - no candidates and no tracked source files: the proposer is never called;
  - a null seam leaves today's result;
  - the sandbox tools missing: the proposer is never called and the diagnostic
    says why.
- `MainWindowViewModelTests.Bootstrap`: a result whose candidates were all
  rejected names them and does not mention scaffolding; a greenfield result
  keeps the scaffolding sentence; a proposed command's status says so.
  `MainWindowViewModelInitTests`: the manual action fills the text box from the
  proposer.
- Watch each fail first; mutate the runner choice (send the proposal to the
  bare runner) and confirm the sandbox fact catches it.

## Verification (through the control API)

Mac, with a provider key present. Shallow-clone ThreeMammals/Ocelot into a
scratch folder, `open-folder`, `bootstrap`, then read `/state.statusText` and
`.relay/config.json`. Expected: `placeholder=false`, a `testCmd` that runs the
unit test project, the "proposed by the model and checked" sentence, a
`.relay/setup-check.log` that shows the two solution commands rejected before
the proposal, and a report under `.relay/bootstrap/` with the proposer's turns
and cost. `git status` in the clone shows nothing the proposer changed. Run
one small task to confirm the pipeline accepts the command under its own
sandbox. Then repeat the bootstrap with the provider keys unset: the
placeholder, and the new headline naming both rejected solution commands. Put
the accepted command, the proposer's turn count and cost, and the keyless
headline in the commit body.

## Out of scope

The per-file test command (its own task,
`prove-the-per-file-test-command-at-bootstrap`); new marker rules for any
language; sandboxing the built-in candidates' check.

## Rejected alternatives

- More marker rules (the MTP project scan, a nested Makefile rule): each helps
  one ecosystem, each needs upkeep, and the next unusual repository needs
  another.
- A single prompt with a list of file names, which is what the existing finder
  sends: the best evidence for a test command is inside files (the CI
  workflow, a script's text), and an agent with tools can read them and try
  its answer before giving it.
- Trusting the answer without the check: the check is what makes a guess safe
  to keep, and it already exists.
