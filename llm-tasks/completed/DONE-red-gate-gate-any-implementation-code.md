# Red gate should gate any implementation code, not just C# files

Visual Relay's stage-5 TDD "red gate" decides whether a task must bring a
failing test first. It currently keys that decision on the `.cs` extension,
which is wrong for a tool meant to drive **any** codebase: a change to XAML,
TypeScript, Python, Go, etc. is silently waved through without a test, while a
legitimate test-only change is blocked. The gate's applicability should be about
*"does this change implementation code"*, not *"is this C#"*.

## Current state (researched)

In `src/VisualRelay.Core/Execution/RelayDriver.cs`, the stage-5 block decides
applicability with a C#-specific check:

```
var testFiles = ReadStringArray(json, "testFiles");
var touchesCode = manifest.Any(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
if (testFiles.Count > 0 || touchesCode)
{
    // ... run the red gate (strip implementation, require tests to go red) ...
}
```

Consequences:

- **XAML and every non-C# language are waved through.** A `.axaml`-only change
  (the kind that shipped the scrollbar de-virtualization with no test) has no
  `.cs`, so `touchesCode` is false and the gate is skipped — even though XAML is
  code that *is* testable (the project now has an Avalonia.Headless harness:
  `tests/VisualRelay.Tests/HeadlessTestApp.cs`, used by
  `ConfigInitEmptyStateUiTests.cs`). The same blind spot applies to any future
  TS/Py/Go/etc. codebase Visual Relay is pointed at.
- **Test-only changes are blocked.** Adding a regression/characterization test
  for already-correct behavior produces a test that passes immediately; with the
  manifest listing only test files, the gate strips nothing, runs the suite
  green, and flags `"author-tests did not go red"`. There is no path to "I only
  added tests."

The existing test `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits`
(in `tests/VisualRelay.Tests/RelayDriverTests.cs`) and the
`ScriptedSubagentRunner.SeedPresentationOnly` helper (in `TestDoubles.cs`)
encode the *old* assumption that a `.axaml` manifest should skip the gate — that
assumption must change (XAML is code).

## What to build

Make the gate's applicability language-agnostic and based on whether the change
includes **implementation code that is not itself a test**:

- Treat a small, explicit set of **non-code** extensions as documentation/config/
  data: at least `.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.csv` (and
  files with no extension). **Everything else is code**, including `.axaml`/
  `.xaml`, `.cs`, `.ts`, `.js`, `.py`, `.go`, `.rs`, etc. Unknown extensions
  default to code (fail safe toward requiring a test).
- Compute the **implementation files** = manifest entries that are code **and**
  are not listed in `testFiles`.
- Run the red gate **iff there is at least one implementation file.** Skip it
  (gate not applicable) when the manifest is entirely non-code (docs/config) or
  entirely test files. When skipped, stage 9 still verifies the full suite stays
  green.
- A code change with no authored tests (e.g. a `.axaml`-only change and empty
  `testFiles`) now has implementation files → the gate applies → it must bring a
  failing test, exactly as a `.cs` change does. XAML is no longer special.

Keep the existing red-gate behavior unchanged for normal code+test tasks (strip
implementation, require red, restore).

## Done when

- The red gate applies to a manifest containing any code file not in `testFiles`,
  regardless of language (covered by tests for at least `.axaml` and one non-C#
  extension), and a code-only change with no tests flags `"author-tests did not
  go red"` as a C# change would.
- The red gate is skipped for a manifest that is entirely non-code
  (`.md`/`.txt`/`.json`/…), and for a manifest whose every entry is a declared
  test file (a test-only change commits without flagging).
- The previous `.cs`-only assumption is gone: update
  `RunTaskAsync_PresentationOnlyChange_SkipsRedGateAndCommits` and the
  `SeedPresentationOnly` helper so the "skips" case uses a genuinely non-code
  file (e.g. a `.md`), and add a case proving a `.axaml`-only change now triggers
  the gate. Write the failing tests first.
- No regression: normal code+test tasks still strip implementation and require
  the authored tests to go red before committing.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional
  Commit subjects.
