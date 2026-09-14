# Bootstrap a test command for .NET repos on Microsoft.Testing.Platform

.NET 10's `dotnet test` switches to Microsoft.Testing.Platform (MTP) when
`global.json` says so, and then refuses a solution that mixes runners. For
such a repo every candidate bootstrap offers today fails, and bootstrap falls
back to the placeholder with advice meant for an empty folder ("Add a task
that scaffolds the project"), although the repo has a working 1,923-test
suite one project away.

## Evidence

Mac, ThreeMammals/Ocelot (shallow clone at `/Users/admin/Dev/vr-eval/Ocelot`),
2026-09-14. `global.json`:

    { "test": { "runner": "Microsoft.Testing.Platform" } }

Two solutions at the root, `Ocelot.slnx` and `Ocelot.Samples.slnx`.

- `dotnet test Ocelot.slnx` in the user's environment (SDK from `~/.dotnet`),
  exit 1: "global.json defines test runner to be Microsoft.Testing.Platform.
  All projects must use that test runner. The following test projects are
  using VSTest test runner: Ocelot.Benchmarks.csproj".
- `dotnet test Ocelot.Samples.slnx`, exit 1 after 4.8 s: "No test projects
  were found."
- `dotnet test --project unit/Ocelot.UnitTests.csproj -f net10.0` works:
  "Test run summary: Passed! total: 1923 failed: 0 succeeded: 1922 skipped: 1
  duration: 10s 914ms". The project multi-targets (the build lists net8.0 and
  net10.0), and MTP names failures as `failed Ns.Class.Test (12ms)`, which
  `TestFailureIds` reads since 71ed6d91.
- History of the bootstrap result: 0.376 kept `dotnet test Ocelot.slnx`
  (the check ran under the dev launcher's nix SDK, fixed in c03b019b); 0.377
  kept `dotnet test Ocelot.Samples.slnx` (the refusal was read as test output,
  fixed in 37403a8a); 0.379 writes the placeholder with the scaffolding
  advice.

## Current state (researched at 37403a8a)

- `Init/TestCommandDetector.cs:65-75`: with `*.slnx`, `*.sln` or `*.csproj` at
  the root, one `dotnet test <solution>` per solution (shortest name first)
  when there are several, else `dotnet test`. No look at `global.json`, no
  test-project candidates.
- `Init/TestCommandValidator.cs:180-195` `Refuses`: now includes "specify
  which", "all projects must use that test runner" and "no test projects were
  found".
- `App/ViewModels/MainWindowViewModel.Bootstrap.cs:59-75` `DescribeBootstrap`:
  the placeholder headline is the same whether the folder is empty or every
  detected candidate was rejected; the rejections live only in the setup-check
  artifact.

## Prescribed approach

1. In the .NET block, read `global.json`'s `test.runner`. When it is
   `Microsoft.Testing.Platform`, find test projects under the root (skip `bin`,
   `obj`, `node_modules`, `.git`): a `*.csproj`/`*.fsproj` whose file contains
   `<IsTestProject>true`, `Microsoft.NET.Test.Sdk`, `MSTest.Sdk`,
   `xunit.v3`, `TUnit` or `Microsoft.Testing.Platform`. Offer
   `dotnet test --project <relative path>` for each, projects whose name
   contains `Unit` first, then the rest by path, after the solution
   candidates. When a project has several `<TargetFrameworks>`, add
   `-f <the highest netN.0 listed>` so the check does not build every target.
2. Keep the solution candidates first: a solution that does not refuse is the
   whole suite.
3. When every candidate was rejected in a folder that has a toolchain, the
   status says so instead of the scaffolding advice: "No test command passed
   the check (`<candidate>`: <first line of its rejection>, ...); set testCmd
   in .relay/config.json." Keep the placeholder mechanics as they are.

## Tests

- `TestCommandDetectorTests.TestingPlatform` (new): a root with `global.json`
  naming MTP, `Ocelot.slnx`, `unit/Ocelot.UnitTests.csproj` (with
  `Microsoft.NET.Test.Sdk` and `<TargetFrameworks>net8.0;net10.0`) and
  `benchmarks/Ocelot.Benchmarks.csproj` (no test markers) offers the solution
  first, then `dotnet test --project unit/Ocelot.UnitTests.csproj -f net10.0`,
  and nothing for benchmarks; without `global.json` no project candidates.
- `MainWindowViewModelTests.Bootstrap`: a result whose candidates were all
  rejected names them and does not mention scaffolding.
- Watch each fail first.

## Verification (through the control API)

Mac: remove `/Users/admin/Dev/vr-eval/Ocelot/.relay`, `open-folder`,
`bootstrap`. Expected `testCmd` `dotnet test --project
unit/Ocelot.UnitTests.csproj -f net10.0`, `placeholder=false`. Then run one
task, for example Ocelot issue #2143 (an upstream path template containing a
regex special character such as `$` must match literally:
`/v3/Orders/$query` matches `/v3/orders/$query` and not `/v3/orders/query`;
placeholders, query strings, trailing slash and catch-all unchanged; tests
beside `UpstreamTemplatePatternCreatorTests`), and check its verify output
ends with "Test run summary".

## Out of scope

VSTest-only repos (unchanged), acceptance-test projects that need services,
per-file forms for MTP (`--filter` differs under MTP).
