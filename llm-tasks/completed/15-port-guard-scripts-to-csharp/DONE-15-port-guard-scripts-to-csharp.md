# Port the three guard scripts to C# (file-size, inspect-code, source-enumeration)

The guards are themselves shell: `tools/guards/check-file-size.sh` (18), `inspect-code.sh`
(62), `guard-source-enumeration.sh` (132). A C#-first repo should enforce its standards in
C#, where they are tested and linted. Port all three into the `VisualRelay.Guards` tool (from
task 12) and retire the scripts.

This design is decided — implement exactly this, no alternatives:

- **File-size guard → `FileSizeGuard` in `VisualRelay.Guards`**: src/tests/tools `*.cs` +
  `*.axaml` ≤ `VISUAL_RELAY_FILE_LINE_LIMIT` (default 300), excluding `bin/`/`obj/`. Retire
  `check-file-size.sh`; **re-point `SplitGuardVerificationTests`** (which currently shells out
  to it).
- **InspectCode gate → C#** (in `VisualRelay.Guards` / the Cli `inspect`): run
  `dotnet jb inspectcode`, parse the SARIF with `System.Text.Json` (not the embedded
  `python3`), gate on zero findings, caches under XDG. Retire `inspect-code.sh`.
- **Source-enumeration guard → C#**: a tested `SourceEnumerationGuard` class **plus an MSBuild
  inline C# task** that replaces the `<Exec bash …>` in `Directory.Build.targets` (it must run
  before `CoreCompile` without a pre-built assembly). Retire `guard-source-enumeration.sh`.

> **Sequencing — task 4 of 6 (12 → 17).** Follows task 13 (the Cli owns `check`/`inspect`).
> Each port is independent; land them as separate commits if useful.

## Current state (researched)

- **File-size:** `check-file-size.sh` (18 lines) scans `src tests tools` for `*.cs`/`*.axaml`
  excluding `bin`/`obj`, limit `VISUAL_RELAY_FILE_LINE_LIMIT` default 300; called from
  `visual-relay:546` and chmod'd by `install-hooks` (`:541`). It is **already mirrored in C#**
  by `SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines`
  (`tests/VisualRelay.Tests/SplitGuardVerificationTests.cs:49-65`) — copy that enumerate/count/
  `violations`/`Assert.Empty` shape — while `GuardScript_ExitsZero` (`:20-42`) shells out to
  the script and **must be re-pointed** to the C# guard (or removed in favor of a repo-wide C#
  guard test).
- **InspectCode:** `inspect-code.sh` runs `dotnet tool restore` + `dotnet jb inspectcode
  VisualRelay.slnx --no-build --output=<sarif> --severity=SUGGESTION`, then counts SARIF
  `runs[].results[]` via an inline `python3` and exits non-zero if any. Caches/SARIF under
  `${XDG_CACHE_HOME:-$HOME/.cache}/visual-relay/inspectcode/`. Called from `visual-relay:552`
  (`check`) and `:574` (`inspect`). InspectCode always exits 0 — the SARIF is the source of
  truth (carve-outs live in `.editorconfig`; see `llm-tasks/10-adopt-inspectcode-standards-repo-wide`).
- **Source-enumeration:** `guard-source-enumeration.sh` compares `git ls-files` counts of
  `*.cs`/`*.axaml` against files visible on disk under `src/tests/tools` (excluding `bin`/
  `obj`); exits 2 when visible < 50% of tracked (the virtio-fs stale-readdir bug). Wired into
  the build at `Directory.Build.targets` (`<Target GuardSourceEnumeration BeforeTargets=
  "CoreCompile"><Exec Command="bash …guard-source-enumeration.sh">`) and called from
  `visual-relay:503` (`build`) and `:545` (`check`). It is characterized by
  `SourceEnumerationGuardTests` (which copies the `.sh` into a fixture repo and runs it —
  `:179-209`); re-point those cases to the C# class.

## What to build

TDD — write/extend the failing tests first.

1. **`FileSizeGuard`** (in `VisualRelay.Guards`): given roots + extensions + limit, return
   violations (reuse the `SplitGuardVerificationTests:49-65` pattern; native enumeration, no
   `find`). Wire the Cli `check` to call it; re-point `SplitGuardVerificationTests.
   GuardScript_ExitsZero` to the C# guard; delete `check-file-size.sh` and its `check`/
   `install-hooks` references.
2. **`InspectCodeGate`** (in `VisualRelay.Guards`/Cli): `dotnet tool restore` + `dotnet jb
   inspectcode` over `VisualRelay.slnx`, parse SARIF with `System.Text.Json`, gate on zero
   results at the floor, caches under XDG. Unit-test the SARIF parser (zero vs N results) on
   fixture SARIF; wire `inspect`/`check`; delete `inspect-code.sh`. Do not rely on
   InspectCode's exit code.
3. **`SourceEnumerationGuard`** (pure-ish class, takes tracked-count + visible-count or the
   roots + a `GitInvoker`): replicate the ratio/threshold logic and the cause+remedy message.
   Re-point `SourceEnumerationGuardTests` to exercise the class (intact / zero-visible /
   below-threshold / above-threshold / excludes bin+obj / covers axaml). Then replace the
   `Directory.Build.targets` `<Exec bash>` with an **inline MSBuild C# task** (`<UsingTask …
   <Code Type="Fragment" Language="cs">`) performing the same count-and-compare (it can't
   reference the not-yet-built assembly; keep the inline body minimal and mirror the class —
   note the small duplication). Update the Cli `build`/`check` to call the C# guard. Delete the
   `.sh`.

## Done when

- `check-file-size.sh`, `inspect-code.sh`, and `guard-source-enumeration.sh` are deleted; no
  references remain (Cli `check`/`inspect`, `Directory.Build.targets`, `install-hooks`, docs).
- File-size and InspectCode gates run from C# (Cli `check`/`inspect`); the source-enum guard
  runs as an inline MSBuild C# task before `CoreCompile` **and** from `build`/`check`.
- `SplitGuardVerificationTests` and `SourceEnumerationGuardTests` are re-pointed to the C#
  guards and pass; the SARIF parser and file-size guard have unit tests that fail against the
  (absent) C# code. Verify the source-enum guard still trips: temporarily hide tracked sources
  and confirm the build fails with the cause+remedy message.
- `./visual-relay check` is green (and still gates InspectCode at zero / file-size at 300);
  changed C# files < 300 lines; Conventional Commit subject e.g.
  `refactor(guards): port file-size, inspectcode, and source-enumeration guards to C#`.
- Coordination: depends on task 13 (Cli owns `check`/`inspect`/`build`). Keep the InspectCode
  carve-out in `.editorconfig` exactly as task 10 left it. The new shell-size guard (task 12)
  already lives in `VisualRelay.Guards`; these join it.
