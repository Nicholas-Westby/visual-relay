# Inject test seams instead of process-global mutable statics

Several production types expose a **settable `public`/`internal` static** member that
exists for one reason only: so tests can swap in a double. xUnit runs test classes in
parallel (class-level parallelism), so when two collections mutate the same global
static they clobber each other mid-flight → nondeterministic, timing-dependent
("flaky") failures. The repo's current defense is to herd every class that touches a
given static into one shared `[Collection]` and enforce that membership with a
convention test — i.e. it *serializes* the races instead of removing them. That
serialization is a band-aid: it slows the suite, couples unrelated test files, and
leaves the loaded footgun in place for the next test author.

This task removes the global mutable state at the root: pass the dependency in (the
codebase already has the DI idiom — `RelayDriverDependencies`, constructor-injected
`I*` interfaces), delete the static override members, retire the `[Collection]`
band-aids that existed only to tame these specific races, and add an automated
convention guard so the anti-pattern cannot return.

Behavior is preserved end-to-end: production wiring changes, production *behavior* does
not. The full suite must stay green **without** relying on `[Collection]` serialization
for any of these seams.

## Current state (researched)

> **Freshness contract.** Every file path, line number, member name, and test-class
> name below was read on 2026-06-15 against the working tree. Line numbers drift —
> before editing, re-grep each anchor (`EnvironmentAccessorOverride`,
> `Override { get; set; }`, `RawGitRunner { get; set; }`, the `[Collection(...)]`
> convention method names) and trust the **symbol**, not the line. If an anchor no
> longer matches (member renamed, seam already migrated, a *new* settable static seam
> has appeared that is not in the table below), STOP and re-research: the seam
> inventory is the spine of this task and must be exact before any code changes.

### The three process-global mutable test seams (exhaustive)

A full sweep of `src/` for settable static members (`grep -rnE "static.*\{[^}]*set;"`
plus a `*Override` / `*ForTests` / `Reset*` pass over all three projects;
`VisualRelay.App` and `VisualRelay.Domain` have **none**) finds exactly three. Each is
a static purely so a test can inject a fake:

**1. `KeyEnvFile.EnvironmentAccessorOverride`** —
`src/VisualRelay.Core/Configuration/KeyEnvFile.cs:37`

```csharp
public static class KeyEnvFile
{
    private static readonly AsyncLocal<IEnvironmentAccessor?> _environmentAccessorOverride = new();

    public static IEnvironmentAccessor? EnvironmentAccessorOverride
    {
        get => _environmentAccessorOverride.Value;
        set => _environmentAccessorOverride.Value = value;
    }

    public static string? GetEnv(string name) =>
        EnvironmentAccessorOverride?.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(name);
    // ... ResolvePath()/Read()/Upsert()/GetUnsetKeys() are all STATIC and call GetEnv()
}
```

- **Injects:** an `IEnvironmentAccessor` (`src/VisualRelay.Core/Configuration/IEnvironmentAccessor.cs`)
  so env reads route through a fake instead of the real process env.
- **Tests that mutate it:** `KeyEnvFileTests` (ctor sets it to a
  `DictionaryEnvironmentAccessor`, `Dispose` nulls it —
  `tests/VisualRelay.Tests/KeyEnvFileTests.cs:13,18`), `SettingsPanelUiTests`
  (`:25,30`), `KeySetupPanelUiTests` (`:21,26`). `DictionaryEnvironmentAccessor` is a
  test double (`tests/VisualRelay.Tests/TestDoubles.cs:13`).
- **Already half-mitigated:** the backing field is `AsyncLocal`, which the doc comment
  (`KeyEnvFile.cs:20-25`) claims isolates parallel classes. That is *itself* a band-aid
  — it papers the race by hoping every read happens on the setter's async-flow. It is
  still process-global settable state and still the thing the guard must forbid.
- **Static-method reach (the hard part):** `KeyEnvFile` is a `static class`; `GetEnv`,
  `ResolvePath`, `Read`, `Upsert`, `GetUnsetKeys` are all static and call `GetEnv`
  with no instance to thread an accessor through. Production callers are in
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs` (`KeyEnvFile.Upsert`,
  `KeyEnvFile.Read`, `KeyEnvFile.GetEnv` at `:95,130,134`).

**2. `GitInvoker.Override`** —
`src/VisualRelay.Core/Execution/GitInvoker.cs:23`

```csharp
internal static class GitInvoker
{
    private static readonly object Lock = new();
    private static string? _gitBinary;            // lazy cache (private)
    private static IReadOnlySet<string>? _envRemove;  // lazy cache (private)

    internal static Func<string, IEnumerable<string>, string, CancellationToken,
        TimeSpan?, IReadOnlyDictionary<string, string>?,
        Task<(int ExitCode, string Output, bool TimedOut)>>? Override { get; set; }

    internal static void ResetForTests() { /* nulls _gitBinary/_envRemove AND Override */ }
    internal static void SetResolvedBinaryForTests(string binaryPath) { ... }

    public static async Task<(int, string, bool)> RunAsync(string rootPath, ...)
    {
        var overrideFn = Override;          // captured before await (TOCTOU guard)
        if (overrideFn is not null) return await overrideFn(...);
        return await ProcessCapture.RunAsync(...);
    }
}
```

- **Injects:** a delegate standing in for the real git-process launch, so tests can
  return synthetic `(ExitCode, Output, TimedOut)` without spawning git.
- **Tests that mutate it:** `GitInvokerTests` (sets `Override`, calls `ResetForTests`/
  `SetResolvedBinaryForTests` in every test, restores in `finally` —
  `tests/VisualRelay.Tests/GitInvokerTests.cs`), and the `WorktreeFilterTests` family
  (`.cs`, `.CopyRecords.cs`, `.RevertHardening.cs`, `.RevertHardening2.cs`,
  `.DataLossFixes.cs` all set `GitInvoker.Override`; the other companions —
  `.EdgeCases.cs`, `.ResidualDataLoss.cs`, `.ResidualLeaks.cs`, and the real-git tests
  in `.cs` — **rely on `Override == null`** to reach real git). `TestGit.cs` shells out
  to real git directly and does not touch the seam.
- **Static-method reach (the *widest* blast radius):** `GitInvoker.RunAsync` is the
  single git chokepoint, called by **8** production sites, almost all themselves
  `static` classes: `HookInstaller` (`static`), `WorktreeFilter` (`static partial`),
  `WorktreeResetter` (`static`), `RedGate` (`static`), `PlanningWorktree` (`static`),
  `EarlyImplementationDetector` (`static`), `GitCommitter` (`static`),
  `ProcessRunners.ManifestValidation`. Threading an `IGitInvoker` instance to all of
  them by constructor would require de-staticizing a large swath of `VisualRelay.Core`.
  See "What to build" for the bounded approach.

**3. `GitCommitter.RawGitRunner`** —
`src/VisualRelay.Core/Execution/GitCommitter.cs:16`

```csharp
internal static class GitCommitter
{
    internal static Func<string, IEnumerable<string>, CancellationToken, TimeSpan?,
        IReadOnlyDictionary<string, string>?,
        Task<(int ExitCode, string Output, bool TimedOut)>>? RawGitRunner { get; set; }

    private static async Task<(int, string, bool)> GitAsync(...)  // retry loop
    {
        var result = RawGitRunner is not null
            ? await RawGitRunner(rootPath, args, cancellationToken, timeout, environment)
            : await GitInvoker.RunAsync(rootPath, args, cancellationToken, timeout, environment);
        // ... 3-attempt retry
    }
}
```

- **Injects:** a git runner double (same role as `GitInvoker.Override` but one layer up,
  so the retry loop in `GitCommitter.GitAsync` is exercised).
- **Tests that mutate it:** `GitCommitterTests` (sets `RawGitRunner = shim.RunAsync` in
  each test, nulls it in `finally` — `tests/VisualRelay.Tests/GitCommitterTests.cs:22,37`
  and elsewhere). The double is `TransientGitShim`
  (`tests/VisualRelay.Tests/TransientGitShim.cs`), which already deliberately bypasses
  `GitInvoker.Override` by calling `ProcessCapture.RunAsync("git", …)` directly to
  "avoid coupling to `GitInvoker.Override`, eliminating cross-collection races"
  (its own comment at `:44-48`) — evidence the team already feels this pain.
- **Static-method reach:** `GitCommitter` is `static`; `CommitAsync`,
  `CaptureUntrackedSnapshotAsync`, `FindUncommittedAuthoredFilesAsync` are static and
  funnel through the static `GitAsync`. Production callers:
  `RelayDriver.CommitGate.cs:156,167`, `RelayDriver.Snapshot.cs:66`.

### The house DI idiom (what the refactor must match)

The codebase already injects collaborators through interfaces and a dependencies
record — this is the target style, not statics:

- **`RelayDriverDependencies`** (`src/VisualRelay.Core/Execution/RelayDriverDependencies.cs`)
  is a `sealed record(ISubagentRunner, ITestRunner, IRelayEventSink)` with a
  `ForTests(...)` factory. `RelayDriver` takes it by constructor and stores
  `_dependencies` (`RelayDriver.cs:12,19-21`); production code reads
  `_dependencies.TestRunner`, `_dependencies.SubagentRunner`, `_dependencies.EventSink`
  throughout (`RelayDriver.Bootstrap.cs:73`, `RelayDriver.Stage5.cs:99`, etc.).
- The collaborator interfaces live in `src/VisualRelay.Core/Execution/Interfaces.cs`
  (`ISubagentRunner`, `ITestRunner`, `IRelayTaskRunner`) — small, single-method,
  `Task`-returning. `IEnvironmentAccessor` is the existing analogue for env reads.
- Static helpers that need a collaborator already take it as a **parameter** rather
  than reaching a static: e.g. `AuthorTestGate.RunAsync(..., ITestRunner testRunner, …)`
  (`RelayDriver.Stage5.cs:99`), `RelayDriver.RepoGuards.cs:30` (`ITestRunner testRunner`).
  This "thread the interface through the call" is the established answer to the
  static-method-reach problem and the model this task follows.

### The serialization band-aids to retire

These convention tests in
`tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs` exist *only* to
serialize the seam races and must be removed/replaced once the seams are injected:

- `GitInvokerTests_HasCollectionAttribute` (`:233`) — asserts **both** `GitInvokerTests`
  **and** `WorktreeFilterTests` carry `[Collection("GitInvoker")]` "so every test that
  touches the static `GitInvoker.Override` seam … is serialized and cannot race".
- `EnvironmentCollectionFiles_HaveCollectionAttribute` (`:70`) — asserts
  `KeyEnvFileTests` carries `[Collection("Environment")]`.
- `NoTestFile_CallsEnvironmentSetEnvironmentVariable` (`:136`) — a *related* guard
  ("all env mutation routes through `KeyEnvFile.EnvironmentAccessorOverride`"). Keep
  the spirit (no raw `Environment.SetEnvironmentVariable` in tests) but update its
  wording/rationale since the route is now an injected accessor, not a static.
- The `GitCommitter` collection (`GitCommitterCollectionFiles_HaveCollectionAttribute`,
  `:48`) lists four files and exists partly for `RawGitRunner` serialization — keep
  only the parts justified by something *other* than the now-removed static (if
  nothing else justifies it, retire it too).

> Note on `[Collection("Headless")]`: the two UI classes
> (`SettingsPanelUiTests`, `KeySetupPanelUiTests`) are in `"Headless"` because they
> share the Avalonia process-global dispatcher (`HeadlessCollectionFiles_…`,
> `SplitGuardVerificationTests.Headless.cs`), **not** because of the env static. That
> collection is legitimate and must stay; only their `EnvironmentAccessorOverride`
> mutation goes away.

### Reflection-guard model

The existing reflection convention test
`SplitGuardVerificationTests.Headless.cs:13` (`HeadlessTestClasses_AllCarryHeadlessCollectionAttribute`)
is the template: it walks `typeof(SplitGuardVerificationTests).Assembly.GetTypes()`,
filters by attribute, and accumulates violations into an asserted-empty list. The new
guard follows this shape but reflects over the **production** assemblies. Facts that
make this work:
- The test project references `VisualRelay.Core` and `VisualRelay.App`
  (`tests/VisualRelay.Tests/VisualRelay.Tests.csproj:42-43`), and
  `VisualRelay.Core` has `<InternalsVisibleTo Include="VisualRelay.Tests" />`
  (`VisualRelay.Core.csproj:14`) — so `typeof(KeyEnvFile).Assembly` and
  `typeof(VisualRelay.App.ViewModels.MainWindowViewModel).Assembly` are reachable, and
  `BindingFlags.NonPublic` reflection sees `internal` members.
- Legitimate statics that must **not** trip the guard already exist and inform the
  allowlist: `private static` lazy caches `GitInvoker._gitBinary`/`_envRemove`
  (private → excluded by a public/internal filter); `static readonly` collections
  (`MainWindowViewModel.AllProviderKeys`, `GitCommitter.InternalArtifactPrefixes`,
  `WorktreeFilter.InternalArtifactPrefixes`); get-only singletons with **no setter**
  (`RelayDriverOptions.Default`/`NoGitCommit`, `RelayPricing.Default`); the
  `AsyncLocal<>` backing field (private + readonly); `const`. None of these is a
  settable, non-readonly, public/internal static field or a static property with a
  setter — so the rule below excludes them by construction.

## What to build

> Behavior-preserving refactor. No production *behavior* changes — only how the git
> runner / env accessor is supplied. After this task, the only way a test substitutes
> a double is by passing it in.

### A. Refactor each seam to an injected dependency

**Seam 1 — `KeyEnvFile` env accessor.** Make environment access instance-supplied
instead of a global static, in whichever of these shapes is least invasive while
killing the static:
- Preferred: convert `KeyEnvFile`'s env-dependent surface so the `IEnvironmentAccessor`
  is **passed in**. Concretely, give the public entrypoints an overload that accepts an
  `IEnvironmentAccessor` (defaulting to a real-process implementation — add a small
  `ProcessEnvironmentAccessor : IEnvironmentAccessor` in `VisualRelay.Core` wrapping
  `Environment.GetEnvironmentVariable`, mirroring the test `DictionaryEnvironmentAccessor`),
  and thread it through `GetEnv` → `ResolvePath`/`Read`/`Upsert`/`GetUnsetKeys`. The
  no-arg production overloads keep working by defaulting to the real accessor; the
  App callers in `MainWindowViewModel.Keys.cs` pass the accessor they already hold (or
  the default). Tests call the accessor-taking overload with a
  `DictionaryEnvironmentAccessor` — **no static assignment**.
- Acceptable alternative if the static surface is too sprawling to thread cleanly:
  promote the env-dependent helpers onto a small injectable instance type (e.g. an
  `EnvFile`/`KeyEnvFileReader` holding an `IEnvironmentAccessor`), construct it in the
  App layer, and pass it where `KeyEnvFile` static calls are made. The pure-parse
  helpers (`Read(filePath)`, `Upsert(filePath, …)`, `ResolvePath(xdg, home)`) that
  take explicit args and never read ambient env can stay static — they are not seams.
- **Delete** `EnvironmentAccessorOverride` (property **and** the `AsyncLocal<>` backing
  field). Update the three test classes to inject via the new overload/instance.

**Seam 2 — `GitInvoker` runner.** Introduce an interface for the git launch (e.g.
`IGitInvoker` with the `RunAsync` signature currently on `GitInvoker`) in
`Interfaces.cs` (or alongside it), with the real implementation being today's
binary-pinning + env-sanitizing logic (a `GitInvoker` *instance*, not statics). The
binary-resolution cache (`_gitBinary`/`_envRemove`/`Lock`) moves to instance fields so
each constructed invoker pins once. Inject the invoker where git is called:
- Because `RunAsync` is reached from ~8 mostly-`static` helpers, pick **one** coherent
  threading strategy and apply it uniformly (do not leave a static fallback):
  - **Option G1 (thread the interface, matches house style):** add an `IGitInvoker`
    parameter to the static helpers that currently call `GitInvoker.RunAsync`
    (`WorktreeFilter.DiscardNonTestEditsAsync`, `RedGate.*`, `WorktreeResetter`,
    `PlanningWorktree`, `EarlyImplementationDetector`, `HookInstaller`,
    `ProcessRunners.ManifestValidation`, `GitCommitter`), exactly as `ITestRunner` is
    already threaded into `AuthorTestGate.RunAsync`/`RelayDriver.RepoGuards`. The
    `RelayDriver` owns the real `IGitInvoker` (add it to `RelayDriverDependencies` next
    to `TestRunner`, with `ForTests(...)` updated) and passes it down the call chain.
    Tests construct a fake `IGitInvoker` and pass it directly — no global.
  - **Option G2 (instance collaborators):** convert the worst-offending static helpers
    to instances that take `IGitInvoker` by constructor. Heavier; only choose if G1's
    parameter-threading proves noisier than constructorization for a given type.
  - Whichever is chosen, the invoker is constructed once (preserving the
    pin-git-once + env-sanitize behavior the comments at `GitInvoker.cs:6-10,67-72`
    describe) and **no settable static remains**.
- **Delete** `GitInvoker.Override` and `ResetForTests()`'s `Override = null` line.
  `SetResolvedBinaryForTests`/`ResetForTests` can be reborn as ordinary configuration
  on the real invoker instance (e.g. a constructor arg pinning the binary for tests),
  or dropped if tests now use a pure fake `IGitInvoker`. Rewrite `GitInvokerTests` to
  exercise the real invoker *instance* and the fake; rewrite the `WorktreeFilterTests`
  family to pass a fake/real `IGitInvoker` in (the "rely on `Override == null` to reach
  real git" tests now pass a real invoker explicitly).

**Seam 3 — `GitCommitter` runner.** `GitCommitter.GitAsync` already chooses
`RawGitRunner ?? GitInvoker.RunAsync`. Once seam 2 supplies an `IGitInvoker`, the
double for the retry loop is just *a different `IGitInvoker`* — there is no need for a
*second* seam. Thread the same `IGitInvoker` into `GitCommitter.CommitAsync` (and the
snapshot/find helpers), and have `GitAsync` call it. **Delete** `RawGitRunner`.
`GitCommitterTests` injects a fake `IGitInvoker` (or keeps `TransientGitShim` but
reshaped to implement `IGitInvoker` and passed in) — note `TransientGitShim` already
avoids the static deliberately, so this is a natural fit.

### B. Retire the serialization band-aids

- Remove `GitInvokerTests_HasCollectionAttribute`,
  `EnvironmentCollectionFiles_HaveCollectionAttribute`, and (if no longer justified)
  the `GitCommitter`-collection assertion from
  `SplitGuardVerificationTests.Conventions.cs`. Remove the now-pointless
  `[Collection("GitInvoker")]` / `[Collection("Environment")]` attributes from the
  affected classes **only where the collection's *sole* purpose was the static race**.
  (Keep `[Collection("Headless")]` on the UI classes and any collection that serializes
  a genuinely shared resource such as the Avalonia dispatcher or real-process
  watchdogs.)
- Update `NoTestFile_CallsEnvironmentSetEnvironmentVariable`'s rationale to reference
  the injected accessor rather than the static override (the no-raw-`SetEnvironmentVariable`
  rule itself stays — it is still correct).

### C. Add the guard convention test

Add a fast, reflection-based `[Fact]` to the `SplitGuardVerificationTests` conventions
family (new partial file `SplitGuardVerificationTests.InjectionSeams.cs`, matching the
`Headless.cs` model) named e.g. `ProductionAssemblies_HaveNoSettableStaticTestSeams`.

**Exact rule.** For each production assembly
(`typeof(KeyEnvFile).Assembly` = VisualRelay.Core, and
`typeof(MainWindowViewModel).Assembly` = VisualRelay.App), walk every type and FAIL if
it declares either of:
1. a **static field** that is `public` or `internal`, **not** `readonly`, **not**
   `const` (literal), and **not** `[ThreadStatic]`; or
2. a **static property** that is `public` or `internal` and has a **setter** (public or
   non-public).

Reflection specifics: `type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
BindingFlags.Static | BindingFlags.DeclaredOnly)` then keep `IsPublic || IsAssembly`
(internal), drop `IsInitOnly` (readonly), drop `IsLiteral` (const), drop fields with
`IsDefined(typeof(ThreadStaticAttribute))`; and `type.GetProperties(same flags)` keep
those whose `GetSetMethod(nonPublic: true)` is non-null **and** whose accessor is
`Public`/`Assembly`. Accumulate `"{type.FullName}.{member}"` violations into a list and
`Assert.Empty` with a message pointing at this task. Skip compiler-generated noise:
exclude types/members carrying `[CompilerGenerated]`, names containing `<` (backing
fields, closures, lambdas), and `IsSpecialName` accessor artifacts.

**Allowlist / why it won't false-positive** (each legitimate static the repo has today
is excluded by construction, not by an ad-hoc name list):
- `private static` lazy caches (`GitInvoker._gitBinary`, `_envRemove`) → excluded:
  not public/internal.
- `static readonly` collections (`AllProviderKeys`, `InternalArtifactPrefixes` ×2,
  the `AsyncLocal<>` field once it… is deleted anyway) → excluded: `IsInitOnly`.
- get-only singletons (`RelayDriverOptions.Default`/`NoGitCommit`, `RelayPricing.Default`)
  → excluded: no setter.
- `const` → excluded: `IsLiteral`.
- `[ThreadStatic]` (none today, but allow them) → excluded explicitly.
- compiler-generated backing fields for `static readonly`/get-only properties →
  excluded by the `<`/`[CompilerGenerated]` filter.

Keep an **empty, documented escape hatch** (a `static readonly string[] Allowlist`
of `"Type.Member"` entries, expected empty after this task) so a genuinely-needed
future static can be added with a comment justifying it — the guard fails closed
otherwise. Prefer the structural heuristic over the allowlist; the allowlist is the
exception, not the mechanism.

**Optional second guard (defense in depth):** a string-scan `[Fact]` over the test
files (model: `NoTestFile_CallsEnvironmentSetEnvironmentVariable`,
`SplitGuardVerificationTests.Conventions.cs:136`) that FAILS if any test source assigns
a known-removed seam name (`KeyEnvFile.EnvironmentAccessorOverride =`,
`GitInvoker.Override =`, `GitCommitter.RawGitRunner =`). Cheap regression tripwire that
catches a re-introduction even before the reflection guard, and self-documents the
forbidden pattern.

## Tests

- **New guard `[Fact]`(s)** in `SplitGuardVerificationTests.InjectionSeams.cs`
  (reflection rule above, plus the optional string-scan tripwire). The reflection guard
  must be **red** if any of the three seams is reintroduced and **green** after the
  migration — verify by temporarily re-adding a settable static and confirming the
  failure names it, then removing it.
- **Rewritten seam tests inject via the new surface** (no static assignment):
  `KeyEnvFileTests`, `SettingsPanelUiTests`, `KeySetupPanelUiTests` (env accessor
  overload/instance); `GitInvokerTests`, the `WorktreeFilterTests` family (fake/real
  `IGitInvoker` passed in); `GitCommitterTests` (fake `IGitInvoker`/reshaped
  `TransientGitShim`). Every `[Fact]` that existed must still exist and assert the same
  behavior — this is a wiring change, not a coverage change.
- **Band-aid conventions removed/updated:** `GitInvokerTests_HasCollectionAttribute`
  and `EnvironmentCollectionFiles_HaveCollectionAttribute` deleted; the
  `GitCommitter`-collection assertion deleted if unjustified;
  `NoTestFile_CallsEnvironmentSetEnvironmentVariable` re-worded but still enforcing.
- **Determinism without serialization:** the affected classes no longer share a
  `[Collection]` for the seam (only legitimate collections like `"Headless"` remain).
  Run the full suite **N=5 consecutive** times (reuse the loop established by the
  deflake task) on both the repo checkout and a `/tmp` native-disk worktree — all green,
  with no test depending on `[Collection]` serialization of these seams to pass.
- **Process-layer smoke:** because this touches the real git chokepoint
  (`GitInvoker.RunAsync`) that unit tests mock, run a real `run-task` end-to-end after
  the change (a small task that commits) to confirm the live git path still pins the
  binary, sanitizes the env, and commits — verify, not just unit-green, is required for
  the git wiring.

## Done when

- `KeyEnvFile.EnvironmentAccessorOverride`, `GitInvoker.Override`, and
  `GitCommitter.RawGitRunner` (and the `AsyncLocal<>` backing field) are **gone**; the
  same dependencies are supplied by injection (overload/instance for the env accessor;
  an `IGitInvoker` threaded through `RelayDriverDependencies` and the git call chain).
- A `grep -rnE "static.*\{[^}]*set;" src/` over public/internal members returns **no
  test-injection seam** (only legitimate get-only singletons / `private` caches remain),
  and the new reflection guard enforces this automatically.
- The `[Collection]` serialization band-aids that existed solely for these seams, and
  their convention tests, are removed/replaced; no remaining test relies on
  `[Collection]` to serialize a global static for these seams.
- The reflection guard `[Fact]` (and optional string-scan tripwire) is in the
  conventions suite, fast, green now, and demonstrably red when a settable static seam
  is reintroduced.
- Full suite green 5× consecutively on both checkout and `/tmp` worktree; a real
  `run-task` commit succeeds (git path behavior unchanged).
- Production behavior is byte-for-byte unchanged; only the wiring moved.
