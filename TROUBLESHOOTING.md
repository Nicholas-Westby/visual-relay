# Troubleshooting

Operational notes for the dev loop. Add entries as you hit (and solve) things.

## A test run hangs / never finishes

`./visual-relay test` normally finishes in ~10s. If it sits at `Testing (NNNs)` with the
counter climbing and **no test ever completing**, a test has deadlocked — it's hung, not slow
(a slow test still eventually prints `Passed … [NNNNN ms]`).

Find the culprit — abort after 30s of inactivity and dump which test(s) were running:

```bash
./visual-relay test --blame-hang --blame-hang-timeout 120s
```

The output prints `The test running when the crash occurred:`. If **two or more** tests are
listed, they were running concurrently and likely deadlocked on shared global state — our
Avalonia headless UI tests each spin up a process-global Avalonia app via
`HeadlessUnitTestSession.StartNew`, and xUnit runs separate test classes in parallel by
default, so two headless classes overlapping can wedge each other.

Cleanup: the hang dump is multi-GB. `TestResults/` is gitignored — delete it when done:

```bash
rm -rf tests/VisualRelay.Tests/TestResults
```

## Headless UI tests must use `[AvaloniaFact]`

Avalonia headless uses **one process-global app/dispatcher per process**. Hand-rolling a
session — `HeadlessUnitTestSession.StartNew(...)` inside a plain `[Fact]` — lets xUnit's
parallel test collections start two sessions at once, which either deadlocks the suite (the
hang above) or throws `the calling thread cannot access this object because a different thread
owns it`. All headless UI tests therefore use `[AvaloniaFact]`/`[AvaloniaTheory]`
(`Avalonia.Headless.XUnit`), which run every UI test on a single shared, serialized session.

`HeadlessUnitTestSession` is **banned** via `Microsoft.CodeAnalysis.BannedApiAnalyzers`
(`tests/VisualRelay.Tests/BannedSymbols.txt`); reintroducing it fails the build (RS0030).

## Serial test mode for trustworthy per-test timings

The default parallel run packs many tests into ~92s of wall clock, but a test's reported
duration is dominated by time queued between awaits, not real work (e.g. a 0.02s test
reports 29s). To get real per-test numbers:

```bash
./visual-relay test serial              # full suite, one collection at a time
./visual-relay test serial GitCommitter # filter within serial mode
```

Serial mode appends `-- xUnit.ParallelizeTestCollections=false` and raises the watchdog
timeout to 1800s (unless `VISUAL_RELAY_TEST_TIMEOUT` is set). The stderr output prints a
`serial mode:` banner so it is obvious in saved logs.

## Leftover backend state under `$XDG_DATA_HOME/visual-relay/`

Until 2026-09-01 a local model gateway ran behind every stage, provisioned into
a `uv`-built Python venv under your user data directory. The gateway, the venv
and the `uv` dependency are gone: providers are called directly, in process.

Nothing reads these any more, so delete them if you want the space back:

| What | Location |
|------|----------|
| Gateway venv | `$XDG_DATA_HOME/visual-relay/backend-venv/` |
| Pidfile, log, generated config | `$XDG_DATA_HOME/visual-relay/scratch/` |

`XDG_DATA_HOME` defaults to `~/.local/share` if unset. Settings and sandbox
policy live elsewhere and are still in use — remove only the two paths above.

## Stage 5 records `unproven`

The Author-tests gate proves the new tests fail before the change exists. When it
cannot, it records `check: unproven` on the stage plus a `reason`, and the task
continues (Verify still gates the end result). The reason names what to fix:

| Reason | What happened | What to do |
|--------|---------------|------------|
| `no test files declared` | The stage returned an empty `testFiles`, so there was nothing to gate. | Fine for a docs-only or config-only change; otherwise the task needs a test. |
| `placeholder test command` | `testCmd` is the bootstrap placeholder: it exits 0 having run nothing. | Set a real `testCmd` in `.relay/config.json`, or re-run `bootstrap` once the project has a toolchain. |
| `gate command unusable` | The command exited 127 (not found) or reported that it collected no tests. | Check `testFileCmd`: its `{files}` expansion must name runnable test files for this project. |
| `no implementation to strip` | The new tests pass against the current code, and nothing outside them could be taken away to make them fail. | So either the behaviour already exists (regression coverage, which is fine) or the tests do not exercise the change. Read the stage-5 diff to tell which. |
| `green before implementation` | The tests passed with everything still in place, and at least one declared test file can carry implementation. The stage was re-asked once and still came back green. | Read the stage-5 diff: the change was probably implemented inside the test file, or the tests do not exercise it. |

Every outcome is in `run.log` as a `verify_result` for stage 5, naming the
command that ran, its exit code, what was stripped and how each declared file was
classified — plus an `author_test_unproven` warning for the reason above.

## Repositories with unusual test layouts

Whether a test can be told from an implementation by its path is a property of the
language. Bootstrap detects it and writes `authorTests` into `.relay/config.json`
(the keys are documented in [docs/OPERATIONS.md](docs/OPERATIONS.md)). Three knobs
fix a repository it reads wrongly:

- **`testPaths`** (top level) — extra globs that count as test paths, on top of the
  built-in filename and directory conventions: `["spec/**", "examples/*_example.go",
  "t/**"]`. Use it when a scope check keeps warning `author_test_scope_suspect`
  about files that really are this project's tests. Bootstrap only creates the key;
  it never overwrites your globs.
- **`authorTests.inlineTestExtensions`** — extensions whose files may carry tests
  beside the implementation. Every file on the model's test-file list is kept
  whatever its extension; an inline-capable extension only changes how the gate
  judges it (the as-is red check rather than the path).
  Bootstrap fills it from the detected languages (`[".rs"]` for Rust, `[".zig"]`
  for Zig, and so on) and empties it for a repository whose languages all keep
  tests in files of their own.
- **`authorTests.diffAudit`** — whether a cheap model call also reads the stage's
  diff for hunks that change behavior instead of asserting it. Set it to `"off"`
  in a repository where that call keeps asking for a re-ask it should not, or to
  `"always"` to audit every Author-tests diff, however the files are classified.

To opt a language in whose inline mechanism you actually use, add its extension by
hand: `".ml"` for OCaml's `ppx_inline_test`, `".erl"` for EUnit behind
`-ifdef(TEST)`, `".ts"` for in-source Vitest, `".move"` for in-module `#[test]`.
The key is per extension, so a Rust and Python repository can treat `.rs` as
inline-capable while `.py` stays gated by path. Re-running `bootstrap` refreshes
`detectedLanguages` and `inlineTestExtensions`, so record a hand-added extension
somewhere you will remember.

## A target repo already tracks `.relay` run artifacts

Older versions force-added each run's bookkeeping (ledger, seals, manifest,
status, per-stage input/report) into the task commit, so a repo Visual Relay ran
against before will have thousands of `.relay/` paths in its history. Nothing
stages them any more, but the ones already tracked keep showing up in
`git status` whenever a run rewrites them.

Untrack them once, from the repo root — the files stay on disk and every run
keeps using them:

    git rm -r --cached --quiet -- .relay ':(exclude).relay/config.json' ':(exclude).relay/.gitignore'

Then commit the removal. Keep `.relay/config.json` tracked if you want the
target's config shared with the repo; `.relay/.gitignore` keeps the rest out.

## Windows

**State locations.** Windows has no `XDG_DATA_HOME`/`HOME`, so Visual Relay falls back to the
standard Windows folders (XDG/`HOME` still win when explicitly set):

| What | Location |
|------|----------|
| UI state, settings (`.env`), sandbox policy | `%APPDATA%\visual-relay\` |
| Provisioned .NET SDK (when the launcher installs it) | `%LOCALAPPDATA%\visual-relay\dotnet\` |

**`.\visual-relay` is blocked / "running scripts is disabled".** The PowerShell execution
policy is blocking the launcher. The `.cmd` shim already passes `-ExecutionPolicy Bypass`, so
prefer `.\visual-relay launch` (which runs `visual-relay.cmd`). To run the `.ps1` directly,
`powershell -ExecutionPolicy Bypass -File visual-relay.ps1 launch`.

**`dotnet` not found after the launcher installed it.** The launcher prepends its install dir to
PATH for that session only (no global machine change). Re-run through `.\visual-relay`, or add
`%LOCALAPPDATA%\visual-relay\dotnet` to your PATH for a standalone `dotnet`.

**Task execution is blocked.** Windows confines writes with Microsoft Execution Containers
(MXC); when `wxc-exec` is not provisioned, execution is blocked rather than run uncontained —
there is no opt-out. Run `visual-relay provision-mxc` to download and install the pinned,
Microsoft-signed `wxc-exec` runtime into `%LOCALAPPDATA%\visual-relay\mxc\` (a no-op if it is
already present), or run execution inside WSL2 with `nono`. Inspection (queue, logs, traces,
settings) works without any sandbox.

Write-confinement is empirically verified against the real `wxc-exec` (a command writing
outside the workspace is denied, inside is allowed). Where the BaseContainer/processcontainer
backend is unavailable, MXC falls back to the **AppContainer + DACL** tier; for the fewest
caveats run `wxc-host-prep prepare-system-drive` (elevated) once so AppContainer processes can
read the system-drive root metadata. The confinement policy lists only writable roots that
exist — a missing toolchain-cache dir is never sent to `wxc-exec` (it would fail to stamp it).

**Git hooks.** `install-hooks` works on Windows through Git for Windows' bundled bash (the
pre-commit hook is `#!/usr/bin/env bash`); a working `git` on PATH is required.
