# Remove Low-Value Tests That Pin Fixed-Forever Static Assets

Part of the test-suite speed push (hard ceiling: full suite under 60 s; 45 s is the
aspirational target). This task is the
small, purely subtractive slice: delete tests whose only subject is a committed static
asset that is now fixed in place. Such tests have no regression surface — the asset can
only change deliberately, and whoever changes it must edit the test in the same commit,
so the test never catches anything; it only costs runtime and maintenance.

This is a **tests-only deletion task**. No production code changes, no test rewrites.

## What to remove (vetted)

1. `tests/VisualRelay.Tests/AppIconTests.cs` — 14 facts pinning the shipped app icon:
   `src/VisualRelay.App/Assets/app-icon.ico` contents/geometry (partly via an
   ImageMagick `magick` probe when on PATH), the icon references in
   `MainWindow.axaml` / `VisualRelay.App.csproj`, and the `packaging/icon` iconset
   files. The icon is final; none of this can regress accidentally.
2. `tests/VisualRelay.Tests/AppMenuNameTests.cs` — a single fact pinning the app menu
   name string. Same reasoning.

After deleting, `grep -rn "AppIconTests\|AppMenuNameTests" --include="*.cs" --include="*.md" tests/ tools/ docs/ AGENTS.md`
and clean up any references (e.g. guard allowlists or docs that name these files).
Leave `llm-tasks/replace-app-icon/` and the `packaging/icon/` assets themselves alone —
only the tests go.

## Optional sweep (strict criteria — when in doubt, keep)

Scan `tests/VisualRelay.Tests/` for other facts matching ALL of:
- asserts byte content, geometry, or a literal display string of a **committed static
  asset** (image, icon, fixed label), AND
- exercises no code path (no view-model logic, no converter, no layout measurement), AND
- would necessarily be edited in the same commit as any deliberate change to the asset.

Do NOT touch:
- Convention/guard tests — they police live rules and bite on new code:
  `SplitGuardVerificationTests*`, `RealSleepGuardTests`, `RealBuildSubprocessGuardTests`,
  `SourceEnumerationGuardTests`, `ShellScriptSizeGuardTests`, `ButtonThemeGuardTests`,
  `BlameHangTimeoutGuardTests`, `CommitMessage*`.
- Env-gated integration tests (anything calling `SkipIfNotOptedIn()` or `Assert.Skip`).
- Anything asserting behavior, bindings, or layout of live controls.

If the sweep finds nothing beyond the two vetted files, that is a fine outcome — this
task is deliberately small.

## Done when

- The two named test files are deleted; any references to them are cleaned up.
- The summary lists every deleted file with its fact count and one-line justification.
- Full suite passes; `./visual-relay check` passes.

## Guardrails

- Deletions only — no test may be rewritten, weakened, or skipped in this task.
- If a candidate is ambiguous (any doubt whether it can catch a real regression), keep
  it and note it in the summary instead of deleting.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`).
