# Bootstrap asks the model for a test command when none of its own passes

Bootstrap finds a project's test command from a table of marker files
(`package.json`, `Cargo.toml`, `*.sln` ...) and keeps the first candidate that
passes a real run. When every candidate fails, or the table knows no marker in
the folder, it writes a placeholder and tells the operator to "add a task that
scaffolds the project", although the repository may have a working suite one
folder away. Each such repository has so far been answered with more
per-language rules. This task adds one general fallback instead: ask a model
for the command, show it what was tried and why it failed, and keep its answer
only if it passes the same check. It also makes the failure message say what
was tried.

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
  that is in the way, which is what a reader needs to find the command.
- The same session, Windows: FreshRSS (its suite runs through a composer
  script) and crawl (its Makefile is two folders down) also got the
  placeholder. Neither has a marker rule, and each would need its own.
- A model-based guess already exists and bootstrap never calls it:
  `Init/LlmTestCommandFinder.cs` builds a prompt from the root's first 100
  entries and reads one line back; `Init/ProviderTestCommandCompleter.cs`
  sends it through the provider transport on the `cheap` tier and returns
  empty when no provider key is present. Its only caller is the GUI's manual
  "find test command" action (`MainWindowViewModel.Execution.cs:167-188`),
  which puts the answer in a text box for a person to review.

## Current state (researched at f8a0d07b)

- `Init/ProjectBootstrapper.cs:176-220` `ResolveTestCommandAsync`: candidates
  from `TestCommandDetector.DetectToolchainCandidates`; each is run through
  `TestCommandValidator.ValidateAsync` and the first `Accepted` wins. When all
  are rejected it returns the placeholder with a `SetupCheckDiagnostic` built
  from every rejection (`:211-216`; the full list goes to
  `.relay/setup-check.log`). With no candidate at all it returns the
  placeholder with no diagnostic (`:219`).
- The check runs in the operator's checkout through a bare shell
  (`CreateValidationRunner`, `:80-81`, `ShellTestRunner`, no nono). That is
  acceptable for a fixed string such as `npm test`. It is not acceptable for a
  command a model wrote after reading file names from an untrusted repository.
- The pipeline's own runner is `SandboxedTestRunner(new ShellTestRunner(...),
  config, ...)` (`MainWindowViewModel.RunOne.cs:29-33`);
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

1. A proposer seam. `BootstrapAsync` and
   `TryUpgradePlaceholderTestCommandAsync` take an optional
   `Func<string, CancellationToken, Task<string>>? proposeCommand` (prompt in,
   raw model text out, the shape `LlmTestCommandFinder` already takes). The GUI
   passes its `ProviderTestCommandCompleter`; the CLI init builds one the same
   way. Null, or an empty answer (no provider key), skips the fallback, so a
   keyless machine behaves as today.
2. When it runs. After the built-in candidates: every candidate was rejected,
   or none was detected and `layout.CountedFiles > 0`. A folder with no
   tracked source files is greenfield and keeps today's placeholder and
   advice.
3. What the model is shown. `LlmTestCommandFinder.BuildPrompt` gains two
   optional inputs: the tracked files, shallowest first and capped at 300
   paths (from the `ls-files` list `TestLayoutDetector` already reads), and
   the attempts so far, each as the command, its exit code and the first 30
   lines of its output. The instruction stays one line out: a shell command,
   run from the repository root, that runs the unit tests once and exits (no
   watch mode, no installs). The manual GUI action gets the file list too.
4. A proposal is checked where it is safe. The model's command never runs in
   a bare shell. It is validated with the same `TestCommandValidator` over a
   `SandboxedTestRunner` built from `RelayConfigLoader.Defaults(proposal)`,
   the wrapper the pipeline will run the command under anyway. When
   `MissingRequiredTools` reports the sandbox cannot run here, the fallback is
   skipped and the diagnostic says so.
5. Up to three proposals. A rejected proposal joins the attempts and the model
   is asked again; a repeated answer ends the loop early. The first accepted
   proposal becomes `testCmd`. Every proposal and its rejection goes into the
   same `SetupCheckDiagnostic` list, so `.relay/setup-check.log` shows the
   whole conversation.
6. The result says where the command came from. `ProjectBootstrapResult` gains
   `TestCommandSource` (`Detected`, `Proposed`, `Placeholder`). The status
   sentence for a proposed command reads `testCmd: <command> (proposed by the
   model and checked; review it in .relay/config.json)`.
7. An honest failure message. When the folder has source files and nothing
   passed, the headline is `No test command passed the check (<candidate>:
   <first line of its rejection>; ...). Set testCmd in .relay/config.json.` and
   the scaffolding advice is kept for the greenfield case only. At most three
   attempts are named; the log has the rest.
8. Docs: `docs/OPERATIONS.md` gains a short "How bootstrap finds the test
   command" section (built-in candidates, then the model under the sandbox,
   then the placeholder); `AGENTS.md`'s `bootstrap` entry gains the clause
   about a proposed command; `TROUBLESHOOTING.md` gains "Bootstrap wrote the
   placeholder on a repository that has tests".

## Tests

- `LlmTestCommandFinderTests`: the prompt lists tracked files shallowest first
  and stops at the cap; attempts render as command, exit code and output head;
  with neither input the prompt is today's.
- `ProjectBootstrapperProposalTests` (new; fake runner and a scripted
  proposer):
  - all built-in candidates rejected, the first proposal accepted: `testCmd` is
    the proposal, the source is `Proposed`, and the proposer's prompt contained
    the rejected commands and their output;
  - the proposal ran through the sandboxed runner, never the bare one (two
    recording runners, assert which saw the proposal);
  - three rejected proposals give the placeholder, and the diagnostic lists
    built-in and proposed attempts in order;
  - a repeated proposal ends the loop after two calls;
  - no candidates and no tracked source files: the proposer is never called;
  - a null proposer, and one that answers empty, leave today's result;
  - the sandbox tools missing: the proposer is never called and the diagnostic
    says why.
- `MainWindowViewModelTests.Bootstrap`: a result whose candidates were all
  rejected names them and does not mention scaffolding; a greenfield result
  keeps the scaffolding sentence; a proposed command's status says so.
- Watch each fail first; mutate the runner choice (send the proposal to the
  bare runner) and confirm the sandbox fact catches it.

## Verification (through the control API)

Mac, with a provider key present. Shallow-clone ThreeMammals/Ocelot into a
scratch folder, `open-folder`, `bootstrap`, then read `/state.statusText` and
`.relay/config.json`. Expected: `placeholder=false`, a `testCmd` that runs the
unit test project, the "proposed by the model and checked" sentence, and a
`.relay/setup-check.log` that shows the two solution commands rejected before
the proposal. Run one small task to confirm the pipeline accepts the command
under its own sandbox. Then repeat the bootstrap with the provider keys unset:
the placeholder, and the new headline naming both rejected solution commands.
Put the accepted command, the number of proposals it took and the keyless
headline in the commit body.

## Out of scope

The per-file test command (its own task,
`prove-the-per-file-test-command-at-bootstrap`); new marker rules for any
language; sandboxing the built-in candidates' check; letting the model use
tools to explore the repository (a multi-turn research call would be a new
kind of stage with its own budget).

## Rejected alternatives

- More marker rules (the MTP project scan, a composer script rule, a nested
  Makefile rule): each helps one ecosystem, each needs upkeep, and the next
  unusual repository needs another.
- Trusting the model's answer without the check: the check is what makes a
  guess safe to keep, and it already exists.
- Running the proposal in the bare shell like the built-in candidates: the
  prompt contains file names and tool output from a repository VR has never
  seen, so the answer is untrusted input and gets the sandbox.
