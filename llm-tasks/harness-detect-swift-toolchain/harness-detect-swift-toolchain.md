# Harness: auto-detect Swift / SwiftPM projects during `init`

VR's `init` auto-detects a target repo's test, guard, and (new) format commands from
build-system markers, but it only knows .NET, Bun, Python, Rust, Go, and Node. It has **no
Swift / SwiftPM support**. This was discovered when driving a Swift package (a `Package.swift`
repo): `init` could not produce a working `.relay/config.json`, so the config had to be
hand-written. This task adds SwiftPM detection alongside the existing toolchains so VR can
drive Swift codebases out of the box.

The signal is a single root manifest file — `Package.swift` — exactly the same shape as the
Rust (`Cargo.toml`) and Go (`go.mod`) detectors. The commands a SwiftPM package needs are:

| Concern | Command |
|---------|---------|
| test    | `swift test` |
| guard / build | `swift build` |
| format  | `swiftformat .` |

Keep the detector **general**: gate purely on `Package.swift` presence, add no repo-specific
flags. (See the parallelism note in step 1 — some repos need `swift test --no-parallel`, but
that stays an operator edit, not a baked-in default.)

## Current state (researched)

**Freshness contract.** The line numbers below are a snapshot and may have drifted by the
time you implement this. **Locate every anchor by searching for the quoted code snippet, not
by line number.** If a quoted snippet no longer matches verbatim, re-read the whole file and
adapt to what is actually there before editing — do not blind-patch a line number.

### The detector that picks test commands

`src/VisualRelay.Core/Init/TestCommandDetector.cs` — `TestCommandDetector.DetectCandidates`
builds a priority-ordered list of test-command candidates, one `File.Exists` / `HasAnyFile`
block per toolchain. The current order (strongest → weakest signal) is documented in the
class-level comment at the top of the file:

```
//   1. .NET         (*.slnx / *.sln / *.csproj)  → "dotnet test"
//   2. Bun           (bun.lock / bunfig.toml)     → "bun test"
//   3. Python        (pyproject.toml / setup.py / pytest.ini) → "pytest"
//   4. Rust          (Cargo.toml)                 → "cargo test"
//   5. Go            (go.mod)                     → "go test ./..."
//   6. Node          (package.json)              → scripts.test value or "npm test"
//   7. Python (weak) (tests/ directory only)     → "pytest"  ← LAST, weakest signal
```

The Rust block is the closest structural analog to follow:

```csharp
// 4. Rust
if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
{
    candidates.Add("cargo test");
}
```

`Detect` (the backward-compatible convenience) is just
`DetectCandidates(rootPath).FirstOrDefault() ?? string.Empty`, so adding a Swift candidate to
`DetectCandidates` makes `Detect` return `"swift test"` for a Swift-only repo automatically —
no separate change needed.

### `HasAnyFile` helper

At the bottom of `TestCommandDetector`:

```csharp
private static bool HasAnyFile(string rootPath, params string[] patterns) =>
    patterns.Any(pattern =>
        Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly).Any());
```

It is **`private static`** on `TestCommandDetector`. Swift is a single concrete filename
(`Package.swift`), so the detector should use `File.Exists(Path.Combine(rootPath,
"Package.swift"))` (matching the Rust/Go blocks) and **does not need `HasAnyFile`**. (If the
sibling `FormatCommandDetector` work — see below — has already promoted `HasAnyFile` to
`internal static`, leave that as-is; this task neither requires nor reverts that change.)

### The guard detector

`GuardCommandDetector.Detect` (same file, below `TestCommandDetector`) enumerates
`tools/guards/*.sh` and chains them with ` && `, then **appends a toolchain-specific build/
format check** when a marker is present. Today it only appends the .NET case:

```csharp
// Append dotnet format when a .NET solution file exists.
var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
var sln = Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
var solution = slnx ?? sln;
if (solution is not null)
{
    parts.Add($"dotnet format {Path.GetFileName(solution)} --verify-no-changes");
}

return string.Join(" && ", parts);
```

**Important behavioral note:** `GuardCommandDetector.Detect` **returns `null` unless a
`tools/guards/` directory with at least one `*.sh` exists** — it early-returns at the top:

```csharp
var guardsDir = Path.Combine(rootPath, "tools", "guards");
if (!Directory.Exists(guardsDir))
    return null;
```

So for a typical Swift package (no `tools/guards/`), the guard detector contributes nothing
and `guardCmd` is omitted from the config. That means the Swift `swift build` guard must ALSO
be reachable without `tools/guards/` — see step 2 for how this task surfaces `swift build`
regardless of whether a guards directory exists.

### How detected commands reach `.relay/config.json`

`src/VisualRelay.Core/Init/RelayConfigWriter.cs` — `RelayConfigWriter.Write(rootPath,
testCommand)` writes the config. It already calls the guard detector and writes `guardCmd`
when non-null:

```csharp
// Auto-detect guard command when guard scripts exist.
var guardCmd = GuardCommandDetector.Detect(rootPath);
if (guardCmd is not null)
{
    json["guardCmd"] = JsonValue.Create(guardCmd);
}
```

The `testCmd` value passed in by the caller is whatever `TestCommandDetector` resolved (after
the caller's smoke-validation fall-through), so once `DetectCandidates` emits `swift test`,
the writer needs no change for the test command.

### The format detector — may or may not exist yet

A sibling spec, `llm-tasks/harness-format-before-verify/harness-format-before-verify.md`,
**introduces a new `FormatCommandDetector` class** in this same file
(`TestCommandDetector.cs`, below `GuardCommandDetector`) and wires a `formatCmd` write into
`RelayConfigWriter.Write`. As of this spec's research, **`FormatCommandDetector` does not yet
exist in the source tree** — it is in flight.

Therefore this task must handle both cases (see step 3):

- **If `FormatCommandDetector` exists** when you implement this: add a Swift branch to it that
  returns `"swiftformat ."` when `Package.swift` is present, and add a detector test.
- **If it does not exist yet:** do NOT create it here (that is the sibling task's
  deliverable). Instead, leave a one-line `// TODO(swift): emit "swiftformat ." here once
  FormatCommandDetector lands (harness-format-before-verify)` comment at the natural insertion
  point and note it in the commit body. Do not duplicate the whole detector.

### Detector test conventions

`tests/VisualRelay.Tests/TestCommandDetectorTests.cs` — the canonical pattern for detector
unit tests. Each test creates an isolated temp repo, drops a marker file, and asserts the
detected command. The shape to mirror exactly:

```csharp
[Fact]
public void Detect_PythonProject_ReturnsPytest()
{
    using var repo = TestRepository.Create();
    File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");
    Assert.Equal("pytest", TestCommandDetector.Detect(repo.Root));
}
```

`TestRepository` lives in `tests/VisualRelay.Tests/TestDoubles.cs`
(`TestRepository.Create()`, `repo.Root` is a fresh temp dir, `IDisposable`). Namespace is
`VisualRelay.Tests`. There is a full-priority-order test worth extending:

```csharp
[Fact]
public void DetectCandidates_AllMarkers_ReturnsFullPriorityOrder()
{
    using var repo = TestRepository.Create();
    File.WriteAllText(Path.Combine(repo.Root, "App.sln"), "");           // dotnet
    File.WriteAllText(Path.Combine(repo.Root, "bun.lock"), "");          // bun
    ...
    Assert.Equal([ "dotnet test", "bun test", ... ], candidates);
}
```

There is **no** dedicated `GuardCommandDetectorTests.cs`; guard-command writing is exercised
indirectly through `tests/VisualRelay.Tests/RelayConfigWriterTests.cs`
(`RelayConfigWriter.Write` → `RelayConfigLoader.TryLoadAsync` round-trips). Follow that file
for any writer-level Swift assertion.

## What to build

Write the failing tests first (TDD). All changes are in the VR harness; nothing is
Swift-repo-specific.

### 1. Add a Swift candidate to `TestCommandDetector.DetectCandidates`

In `src/VisualRelay.Core/Init/TestCommandDetector.cs`, add a Swift block to
`DetectCandidates`. Place it **after Go and before Node** — i.e. as the new step 6, pushing
Node and the weak-Python `tests/` heuristic down. Rationale: `Package.swift` is a strong,
unambiguous single-manifest signal (like Rust/Go) and should outrank the generic
`package.json` → `npm test` fallback, since a Swift repo can incidentally contain a
`package.json` (e.g. for docs tooling) but is never a Node test project.

```csharp
// 6. Swift (SwiftPM)
if (File.Exists(Path.Combine(rootPath, "Package.swift")))
{
    candidates.Add("swift test");
}
```

Update the class-level priority-order comment at the top of the file to insert Swift in the
same position and renumber Node / weak-Python accordingly, e.g.:

```
//   6. Swift         (Package.swift)              → "swift test"
//   7. Node          (package.json)              → scripts.test value or "npm test"
//   8. Python (weak) (tests/ directory only)     → "pytest"  ← LAST, weakest signal
```

**Do NOT add `--no-parallel`.** SwiftPM runs tests in parallel by default, and some suites
(those sharing global/filesystem state) deadlock or flake under parallelism. The fix is
repo-specific, so the detector must emit the plain `swift test`. Document the workaround in
the field/commit body so an operator knows they can hand-edit the generated config to
`"swift test --no-parallel"` for a deadlock-prone suite. (Also worth a one-line mention in the
generated config's surrounding docs if such docs exist, but keep the detector output plain.)

### 2. Add `swift build` as the Swift guard

The guard surface is the awkward part because `GuardCommandDetector.Detect` early-returns
`null` when there is no `tools/guards/` directory, and most Swift packages have none. Pick the
approach that matches how the codebase is actually structured when you get there — read
`GuardCommandDetector.Detect` and `RelayConfigWriter.Write` first, then choose:

**Preferred — make the guard detector toolchain-aware even without a guards dir.** Refactor
`GuardCommandDetector.Detect` so that the toolchain build/format check is appended **whether
or not** `tools/guards/` exists, returning a non-null guard string when EITHER guard scripts
OR a recognized toolchain marker is present. Concretely:

- Move the early `return null` so it only fires when **both** the guards list is empty **and**
  no toolchain marker contributes a check.
- Add Swift to the toolchain-check section, mirroring the existing .NET append:

  ```csharp
  // Append "swift build" when a SwiftPM manifest exists.
  if (File.Exists(Path.Combine(rootPath, "Package.swift")))
  {
      parts.Add("swift build");
  }
  ```

- The .NET branch already appends `dotnet format <sln> --verify-no-changes`; Swift's analog is
  `swift build` (a compile check — SwiftPM has no `--verify`-style format gate; formatting is
  handled separately by `formatCmd`, step 3).

This keeps the change general: any future single-marker toolchain (and the existing .NET case)
benefits from the same "marker contributes a guard even without `tools/guards/`" behavior.
**Verify you do not regress the existing tests** — `RelayConfigWriterTests` and any
`GuardCommandDetector` callers must still see `null` for a repo with neither guards nor a known
marker, and the existing .NET-solution-with-guards composition (`... && dotnet format ...`)
must be unchanged.

**Fallback — if refactoring `GuardCommandDetector` proves invasive** (e.g. callers depend on
the strict `tools/guards/`-only contract): instead emit `swift build` through the
guard-writing path in `RelayConfigWriter.Write`. After the existing guard block, add:

```csharp
// SwiftPM build check doubles as the guard when no guard scripts exist.
if (guardCmd is null && File.Exists(Path.Combine(rootPath, "Package.swift")))
{
    json["guardCmd"] = JsonValue.Create("swift build");
}
```

Whichever path you take, the end state is: **a Swift-only repo's generated
`.relay/config.json` has `"guardCmd": "swift build"`** (and, when the repo also has
`tools/guards/*.sh`, `swift build` is chained after them with ` && `). Document the chosen
approach in the commit body.

### 3. Add Swift to the format detector (conditional)

Re-check whether `FormatCommandDetector` exists in
`src/VisualRelay.Core/Init/TestCommandDetector.cs` at implementation time (search for
`class FormatCommandDetector`):

- **It exists** → add a Swift branch returning `"swiftformat ."`, positioned analogously to its
  Rust branch (single-marker, after the Node/package.json fallback or wherever Rust/Go sit in
  that detector):

  ```csharp
  // SwiftPM — swiftformat is the de-facto formatter.
  if (File.Exists(Path.Combine(rootPath, "Package.swift")))
      return "swiftformat .";
  ```

  Then ensure `RelayConfigWriter.Write` already writes `formatCmd` (the sibling spec adds
  this); if so, a Swift-only repo will get `"formatCmd": "swiftformat ."` with no further
  change. Add a `FormatCommandDetectorTests` case (mirror its existing ones) asserting
  `Package.swift` → `"swiftformat ."`.

- **It does NOT exist** → do not create it. Add a single placeholder comment at the
  GuardCommandDetector/where-the-format-detector-will-live boundary:

  ```csharp
  // TODO(swift): when FormatCommandDetector lands (harness-format-before-verify),
  // emit "swiftformat ." for repos containing Package.swift.
  ```

  and call this out in the commit body so the sibling-task implementer (or a follow-up) wires
  it. Do not block this task on the format detector.

### 4. Tests (write first)

All in `tests/VisualRelay.Tests/`. Mirror the existing detector-test shape exactly
(`using var repo = TestRepository.Create();` + `File.WriteAllText(Path.Combine(repo.Root,
...))` + `Assert.Equal(...)`).

**a. Extend `tests/VisualRelay.Tests/TestCommandDetectorTests.cs`:**

```csharp
// ── New marker: Swift / SwiftPM ─────────────────────────────────────

[Fact]
public void Detect_SwiftPackage_ReturnsSwiftTest()
{
    using var repo = TestRepository.Create();
    File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");
    Assert.Equal("swift test", TestCommandDetector.Detect(repo.Root));
}

[Fact]
public void DetectCandidates_PackageSwiftAndPackageJson_SwiftBeforeNode()
{
    // A Swift repo that also carries a package.json (docs tooling, etc.)
    // must be detected as Swift, not Node.
    using var repo = TestRepository.Create();
    File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");
    File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");
    var candidates = TestCommandDetector.DetectCandidates(repo.Root);
    Assert.Equal(["swift test", "npm test"], candidates);
}
```

Also update the existing `DetectCandidates_AllMarkers_ReturnsFullPriorityOrder` test to add a
`Package.swift` marker and insert `"swift test"` into the expected ordered list at the same
position chosen in step 1 (between `"go test ./..."` and `"npm test"`). Keeping this test
authoritative for the full order prevents accidental reordering later.

**b. Guard coverage — `tests/VisualRelay.Tests/RelayConfigWriterTests.cs`** (or a new
`GuardCommandDetectorTests.cs` if you prefer a focused unit; the repo currently has no such
file, so extending `RelayConfigWriterTests` matches precedent):

```csharp
[Fact]
public async Task Write_SwiftPackage_ProducesSwiftBuildGuard()
{
    using var repo = TestRepository.Create();
    File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");

    RelayConfigWriter.Write(repo.Root, "swift test");

    var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
    Assert.Equal(RelayConfigStatus.Loaded, result.Status);
    Assert.Equal("swift test", result.Config.TestCommand);
    Assert.Equal("swift build", result.Config.GuardCommand);
}
```

(Confirm the loaded property name for the guard — it is `GuardCommand`, JSON key `guardCmd`.
If `RelayConfig` exposes it differently, assert against the actual property.) Add a companion
test proving a **non-Swift, no-guards** repo still yields a null guard (no regression):

```csharp
[Fact]
public async Task Write_NoGuardsNoToolchainMarker_GuardCommandIsNull()
{
    using var repo = TestRepository.Create();
    RelayConfigWriter.Write(repo.Root, "echo test");
    var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
    Assert.Null(result.Config.GuardCommand);
}
```

**c. Format detector test — only if `FormatCommandDetector` exists** (step 3 first branch):
add to its test file (e.g. `FormatCommandDetectorTests.cs`):

```csharp
[Fact]
public void Detect_SwiftPackage_ReturnsSwiftformat()
{
    using var repo = TestRepository.Create();
    File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");
    Assert.Equal("swiftformat .", FormatCommandDetector.Detect(repo.Root));
}
```

If the format detector does not exist, skip this test (and note it).

## Done when

- **Test command:** a repo containing only `Package.swift` makes
  `TestCommandDetector.Detect` return `"swift test"`, and `DetectCandidates` places
  `"swift test"` ahead of the `package.json`→`npm test` fallback — asserted by the new
  `TestCommandDetectorTests` cases and the extended full-order test.
- **Guard command:** `RelayConfigWriter.Write` on a Swift-only repo produces
  `"guardCmd": "swift build"`; when the repo also has `tools/guards/*.sh`, `swift build` is
  chained after them with ` && ` — asserted by `RelayConfigWriterTests`.
- **No guard regression:** a repo with neither `tools/guards/` nor a recognized toolchain
  marker still yields a null guard command — asserted by the companion test; existing
  `RelayConfigWriterTests` / .NET guard composition tests remain green and unchanged.
- **Format command:** if `FormatCommandDetector` exists, a Swift repo yields
  `"swiftformat ."` (asserted by a detector test) and `RelayConfigWriter` writes
  `"formatCmd": "swiftformat ."`. If it does not exist, a `TODO(swift)` marker is left at the
  insertion point and called out in the commit body.
- **Generality preserved:** detection is gated solely on `Package.swift`; no Vaycay- or
  repo-specific flags (no `--no-parallel`) are baked in; Swift slots into the same
  single-marker pattern as Rust/Go. The `--no-parallel` workaround is documented for operators
  but never auto-emitted.
- **`./visual-relay check` green** after all changes. No edits to existing test assertions
  beyond adding the `Package.swift` marker + `"swift test"` entry to the full-priority-order
  test.
- **Files under 300 lines each.** `TestCommandDetector.cs` gains a ~4-line Swift block (+ the
  guard-detector refactor of ~5 lines and/or a ~5-line writer branch); the test files gain
  small focused cases.
- **Conventional Commit subject candidates:**
  - `feat(init): detect SwiftPM projects (swift test / swift build / swiftformat)`
  - `feat(harness): add Swift toolchain detection to init`
  - `feat(detect): emit swift test + swift build for Package.swift repos`
