# Add a C# guard that flags chunky shell scripts (advisory in this task)

This repo is .NET 10 / Avalonia, but a lot of *logic* still lives in bash — `visual-relay`
(600 lines), `tools/backend/backend.sh` (381), the guard scripts, the macOS packaging. Shell
logic is untested, unlinted, and outside every guardrail the C# enjoys (analyzers,
`TreatWarningsAsErrors`, InspectCode, `dotnet format`, the 300-line file guard). Build the
guard that finds it: a C# tool that flags any **tracked** shell script — detected **by
extension or by hashbang** — whose **logic-line** count exceeds a limit.

This design is decided — implement exactly this, no alternatives:

- The guard lives in a new C# tool **`tools/VisualRelay.Guards`** (the "maybe a tool"
  the brief asked for), with pure, unit-tested logic.
- Detection is **by extension** (`.sh`, `.bash`, `.zsh`) **or by hashbang** (first line
  matches `^#!.*\bsh\b` — `bash`/`sh`/`zsh`/`dash`, incl. `/usr/bin/env bash`), over
  `git ls-files` only.
- A **logic line** is any line that is **not** blank, **not** a full-line comment (first
  non-whitespace char `#` — this also drops the hashbang), and **not** inside a here-doc
  body (between `<<[-]['"]?WORD` and the closing `WORD`). A trailing inline comment does
  not rescue a code line. This is a line classifier, **not** a bash parser.
- **No allowlist — ever.** One global limit, `VISUAL_RELAY_SHELL_LINE_LIMIT`, default **20**
  (mirrors `VISUAL_RELAY_FILE_LINE_LIMIT`). 20 is a *ceiling, not a target* — keep wrappers
  as thin as possible.

> **Sequencing — this is task 1 of 6 (12 → 17), and it is deliberately *advisory*.**
> Because there is no allowlist, the *enforcing* (build-failing) test can only land once
> every existing script conforms. So this task ships the tool + unit tests + a **reporting**
> `./visual-relay guards` (exit 0, even with offenders). Tasks 13–16 (and the parallel
> `claim-authorship-strip-claude-trailers`, which converts `me.sh`) then convert the
> offenders; **task 17** flips this guard to enforcing. See
> `docs/superpowers/specs/2026-06-20-csharp-first-shell-scripts-design.md`.

## Current state (researched)

- **Today's tracked shell scripts** (by extension or hashbang; total lines): `visual-relay`
  600, `tools/backend/backend.sh` 381, `tools/guards/guard-source-enumeration.sh` 132,
  `packaging/macos/build-app-bundle.sh` 127, `me.sh` 116, `test.sh` 70,
  `tools/guards/inspect-code.sh` 62, `packaging/macos/generate-iconset.sh` 45,
  `.githooks/pre-commit` 45, `tools/guards/check-file-size.sh` 18, `.githooks/commit-msg` 17.
  The extensionless ones (`visual-relay`, `.githooks/pre-commit`, `.githooks/commit-msg`)
  are why hashbang detection is required.
- **Guard-as-test is the house idiom**, and a tool Exe is referenceable from tests:
  `SourceEnumerationGuardTests.cs` and `SplitGuardVerificationTests.cs` are guards expressed
  as xUnit tests; `tests/VisualRelay.Tests/VisualRelay.Tests.csproj:44` already
  `ProjectReference`s a tool, `tools/VisualRelay.DrainQueue`. Repo root in tests is
  `RepoSetup.Root` (`tests/VisualRelay.Tests/RepoSetup.cs:9` — walks up to the dir holding
  the `visual-relay` file). Synthetic git fixtures use `TestRepository.Create()` +
  `TestGit.Run(...)` (see `SourceEnumerationGuardTests.cs:153-209`).
- **The minimal tool csproj to mirror** is `tools/VisualRelay.SampleTasks/…csproj` (Sdk
  `Microsoft.NET.Sdk`, `OutputType=Exe`, `net10.0`, `ImplicitUsings=enable`, `Nullable=enable`).
  Tools register in `VisualRelay.slnx` under the `/tools/` folder. `Directory.Build.props`
  applies `TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true` repo-wide, so the
  tool must be warning-clean.
- **Git seam:** route the `git ls-files` call through `GitInvoker`
  (`src/VisualRelay.Core/Execution/GitInvoker.cs:12,38` — pins a stable git binary,
  sanitizes env). `RunTask` shows a tool referencing `Core`. Do not shell out to raw `git`.
- **The `./visual-relay` dispatch** is a bash `case` (`visual-relay:468-580`); add a new
  `guards)` branch mirroring the `inspect)` branch (`visual-relay:572-575`) and extend the
  usage string (`visual-relay:577`). (Task 13 later carries this into `VisualRelay.Cli`.)
- **File-size precedent (don't touch it here):** the 300-line rule is `check-file-size.sh`
  (18 lines, `VISUAL_RELAY_FILE_LINE_LIMIT` default 300), invoked from `visual-relay:546`
  and characterized by `SplitGuardVerificationTests` (`:20-42` shells out to it; `:49-65`
  is the pure-C# variant). Task 15 ports it to C# — out of scope here.

## What to build

TDD — write the failing unit tests first, on synthetic inputs (no real-tree assertion yet).

1. **New project `tools/VisualRelay.Guards`** (mirror `VisualRelay.SampleTasks`'s csproj;
   add a `ProjectReference` to `VisualRelay.Core` for `GitInvoker`; register in
   `VisualRelay.slnx`).
2. **`ShellScriptClassifier`** (pure): `IsShellScript(string relativePath, string? firstLine)`
   → true when the extension is `.sh`/`.bash`/`.zsh` **or** `firstLine` matches
   `^#!.*\bsh\b`. Unit-test: `.sh` by extension; extensionless `visual-relay` by hashbang
   `#!/usr/bin/env bash`; `.githooks/pre-commit` by hashbang; a `.py`/`.cs`/README is not a
   shell script; a `#!/usr/bin/env python3` file is not.
3. **`ShellScriptLineCounter.CountLogicLines(IEnumerable<string> lines)`** (pure): apply the
   blank / full-line-comment / here-doc-body rules above. Unit-test each: blank lines and
   `# comment` lines (and the hashbang) excluded; `cmd  # inline` counts as 1; a here-doc
   (`cat <<EOF` … `EOF`, and the quoted `<<'MSG'` and dash `<<-EOF` forms) body excluded
   while the `cmd <<EOF` line itself counts; a realistic ~3-line wrapper counts 3; a script
   with an `if`/`while` counts the branch lines.
4. **`ShellSizeGuard.FindViolations(IReadOnlyList<(string Path, string[] Lines)> files, int limit)`**
   → ordered list of `(path, count, limit)` for shell scripts whose count > limit. Unit-test
   it on a synthetic set mixing a thin wrapper (passes), a branching script (fails), and a
   non-shell file (ignored).
5. **`Program.cs`**: read the limit from `VISUAL_RELAY_SHELL_LINE_LIMIT` (default 20) or a
   `--max <n>` arg; obtain the tracked file list via `GitInvoker` `ls-files` against the repo
   root (resolve root from cwd); classify, count, and **print** each violation as
   `path: N logic lines (limit M)` plus a one-line remedy ("move the logic into a C# tool and
   leave a thin wrapper; there is no allowlist"). **Exit 0 regardless** (advisory in this
   task) — but print a clear summary line (e.g. `shell-size: N script(s) over the limit`).
6. **Wire `./visual-relay guards`**: add the `guards)` dispatch branch + usage entry. Running
   it prints today's offenders and exits 0; it is **not** added to the `check)` gate yet
   (task 17).

## Done when

- `tools/VisualRelay.Guards` builds warning-clean and is registered in `VisualRelay.slnx`.
- The `ShellScriptClassifier` / `ShellScriptLineCounter` / `ShellSizeGuard` unit tests pass
  and **fail against today's (absent) code**; the here-doc, hashbang, extension, inline-comment,
  and thin-wrapper-vs-branching cases are all covered.
- `./visual-relay guards` lists the current offenders — against a limit of 20 it should report
  the 8 logic-heavy scripts (`visual-relay`, `backend.sh`, `guard-source-enumeration.sh`,
  `build-app-bundle.sh`, `me.sh`, `test.sh`, `inspect-code.sh`, `generate-iconset.sh`); the two
  git hooks and `check-file-size.sh` already fit under 20 — and **exits 0** (it does not fail
  `./visual-relay check` yet).
- **No allowlist file exists and none is referenced** (by design — confirm there is no path
  to exempt an individual file).
- `./visual-relay check` is green; changed C#/XAML files < 300 lines; Conventional Commit
  subject e.g. `feat(guards): add advisory shell-script size guard tool`.
- Coordination (baked here because the implementer sees one task at a time): this guard is
  intentionally advisory now; **task 17** adds the build-failing guard-as-test, after tasks
  13–16 + `claim-authorship-strip-claude-trailers` convert every offender. Keep the pure logic
  reusable by the test project (it will `ProjectReference` this tool, exactly as it does
  `VisualRelay.DrainQueue`).
