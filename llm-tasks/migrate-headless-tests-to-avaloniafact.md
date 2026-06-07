# Migrate headless UI tests to `[AvaloniaFact]` (xunit v3) and ban hand-rolled sessions

The headless Avalonia UI tests intermittently **deadlock the whole suite**. Each one hand-rolls
its own session — `using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp))`
inside a plain `[Fact]` (`tests/VisualRelay.Tests/ActivityColumnItemsPanelTests.cs:24`,
`ConfigInitEmptyStateUiTests.cs:16`, `NewTaskAuthoringTests.cs:19`). Avalonia headless uses **one
process-global app/dispatcher per process**, and xUnit runs separate test classes (collections)
in parallel by default, so two headless tests can start two sessions at once and wedge each
other. Symptom: `./visual-relay test` freezes at `Testing (NNNs)` with nothing completing; the
hang dump lists two headless tests "running" simultaneously. This is what timed out relay stages
5 and 10 in the `new-task-editor-in-detail-pane` run, and what `TROUBLESHOOTING.md` documents.

The fix is to route every UI test through `Avalonia.Headless.XUnit`'s `[AvaloniaFact]` /
`[AvaloniaTheory]`, which run all UI tests on a **single shared, serialized session**, and to
make the broken pattern impossible to reintroduce via a banned-API analyzer.

## Current state (researched — these were verified by building/running, not assumed)

- **`Avalonia.Headless.XUnit` 12.0.4 requires xunit v3.** Its nuspec depends on
  `xunit.v3.extensibility.core` 3.2.2. Adding it on top of the current `xunit` 2.9.3
  (`tests/VisualRelay.Tests/VisualRelay.Tests.csproj:14`) produces 212× `CS0433`
  (`InlineDataAttribute` exists in both `xunit.core` and `xunit.v3.core`). So the test project
  must move to xunit v3 first.
- **The v3 migration is clean.** Swapping `xunit` 2.9.3 → `xunit.v3` 3.2.2 compiles the entire
  existing 210-test suite with **zero `CS####` errors** (no API breaks). The *only* new
  diagnostic is **xUnit1051** (250×: "use `TestContext.Current.CancellationToken`"), which the
  repo's `TreatWarningsAsErrors=true` (`Directory.Build.props`) turns into build errors.
- **`[AvaloniaFact]` works at runtime here.** A spike test (real headless body, no `StartNew`/
  `Dispatch`) passed in isolation in ~300 ms under v3.
- **You cannot mix `[AvaloniaFact]` with leftover `StartNew` tests.** Run together in parallel,
  the AvaloniaFact test fails with `InvalidOperationException: The calling thread cannot access
  this object because a different thread owns it` — the shared session and the per-test sessions
  fight over the one global app. **All headless tests must be converted in this one change.**
- **`dotnet test` still works** for v3 via the existing `Microsoft.NET.Test.Sdk` 17.14.1 +
  `xunit.runner.visualstudio` 3.1.4 — discovery/execution succeeded in the spike. No switch to
  Microsoft.Testing.Platform is needed.

## What to build (single direction — do exactly this)

### 1. Move the test project to xunit v3
In `tests/VisualRelay.Tests/VisualRelay.Tests.csproj`:
- Replace `<PackageReference Include="xunit" Version="2.9.3" />` with
  `<PackageReference Include="xunit.v3" Version="3.2.2" />`.
- Add `<PackageReference Include="Avalonia.Headless.XUnit" Version="12.0.4" />`.
- Keep `Microsoft.NET.Test.Sdk` 17.14.1, `xunit.runner.visualstudio` 3.1.4,
  `Avalonia.Headless` 12.0.4, `coverlet.collector`. Keep the `<Using Include="Xunit" />`; add
  `<Using Include="Avalonia.Headless.XUnit" />` so `[AvaloniaFact]` needs no per-file using.
- Do **not** switch to Microsoft.Testing.Platform; keep running via `dotnet test`.
- Do **not** globally disable test parallelization — non-UI tests must stay parallel.

### 2. Silence xUnit1051 (decision: suppress, don't rewrite 250 call sites)
Add to `.editorconfig`, with a comment recording why:
`dotnet_diagnostic.xUnit1051.severity = none`
Rationale (state it in the comment): xUnit1051 is a cancellation-responsiveness style rule;
this is a fast unit suite and many sites pass `CancellationToken.None` deliberately. Threading
`TestContext.Current.CancellationToken` through ~250 call sites is scope creep orthogonal to
this task and risks mistakes. Revisit later if desired. (Confirm no other v3 analyzer
diagnostics surface once this is off — in the spike, xUnit1051 was the only one.)

### 3. Convert every headless test to `[AvaloniaFact]` / `[AvaloniaTheory]`
For all three classes (`ActivityColumnItemsPanelTests`, `ConfigInitEmptyStateUiTests`,
`NewTaskAuthoringTests`) and every UI test method:
- Replace `[Fact]` → `[AvaloniaFact]` (and any `[Theory]` → `[AvaloniaTheory]`).
- Delete the `using var session = HeadlessUnitTestSession.StartNew(...)` line and unwrap the
  `await session.Dispatch(async () => { ... }, CancellationToken.None)` — inline the body
  directly into the test method (`[AvaloniaFact]` already runs it on the shared dispatcher
  thread). Remove the `return 0;` Func-overload trick. Keep the `Dispatcher.UIThread.RunJobs()`
  calls and all assertions.
- After this, **no `HeadlessUnitTestSession` reference may remain anywhere in `tests/`.**

### 4. Make the broken pattern un-reintroducible (BannedApiAnalyzers)
In `tests/VisualRelay.Tests/VisualRelay.Tests.csproj`, add the analyzer (pin the latest stable
version):
```xml
<PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="<latest>">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```
and wire the banned-symbols file:
```xml
<ItemGroup>
  <AdditionalFiles Include="BannedSymbols.txt" />
</ItemGroup>
```
Create `tests/VisualRelay.Tests/BannedSymbols.txt` banning the whole type, with a rich,
LLM-readable message on one line (BannedSymbols messages cannot contain newlines):
```
T:Avalonia.Headless.HeadlessUnitTestSession;Do not hand-roll a headless session. Avalonia headless uses ONE process-global app/dispatcher per process, so calling HeadlessUnitTestSession.StartNew inside a [Fact] lets xUnit's parallel test collections start two sessions at once - they deadlock (the whole suite hangs at "Testing (Ns)" with nothing completing) or cross-thread-fault ("the calling thread cannot access this object because a different thread owns it"). Write the test with [AvaloniaFact]/[AvaloniaTheory] from Avalonia.Headless.XUnit instead: it runs every UI test on a single shared, serialized session. See TROUBLESHOOTING.md.
```
Banning the type (not just `StartNew`) also catches `session.Dispatch` and field/local uses.
Ensure the banned-API rule is an error: add `dotnet_diagnostic.RS0030.severity = error` to
`.editorconfig` (it would already break the build via `TreatWarningsAsErrors`, but make it
explicit). No production code references this type, so the ban is scoped naturally to tests.

### 5. Update docs
- `TROUBLESHOOTING.md`: note that headless tests use `[AvaloniaFact]` and that
  `HeadlessUnitTestSession` is banned (BannedApiAnalyzers) — reintroducing it fails the build.
- `AGENTS.md`: one line — "Headless UI tests must use `[AvaloniaFact]`/`[AvaloniaTheory]`
  (Avalonia.Headless.XUnit); `HeadlessUnitTestSession` is banned."

## Done when

- [ ] The test project references `xunit.v3` 3.2.2 + `Avalonia.Headless.XUnit` 12.0.4; `xunit`
      2.9.3 is gone; the suite builds 0/0 (xUnit1051 suppressed in `.editorconfig` with a
      justifying comment; no other analyzer diagnostics appear).
- [ ] All headless tests use `[AvaloniaFact]`/`[AvaloniaTheory]`; **`grep -rn HeadlessUnitTestSession tests/` returns nothing.**
- [ ] `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` are in place banning
      `T:Avalonia.Headless.HeadlessUnitTestSession` with the rich message; RS0030 is an error.
      **Verified** by temporarily re-adding a `HeadlessUnitTestSession.StartNew(...)` call and
      confirming the build fails with that message, then reverting.
- [ ] The deadlock is gone: `./visual-relay test` runs green **10× in a row with zero hangs and
      zero cross-thread faults** (previously it hung intermittently).
- [ ] Non-UI tests still run in parallel (parallelization is **not** globally disabled).
- [ ] `dotnet test` (vstest adapter) still drives the suite — no move to Microsoft.Testing.Platform.
- [ ] `TROUBLESHOOTING.md` and `AGENTS.md` updated as above.
- [ ] `./visual-relay check` green; C#/XAML files under 300 lines; Conventional Commit subjects.
