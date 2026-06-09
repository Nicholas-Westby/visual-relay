# AGENTS.md

Guidance for AI agents and contributors working in this repository.

## Workflow

All work done in this project should be a commit against `main`; we don't use
feature branches or PRs.

Make focused commits directly on `main` with Conventional Commit subjects
(enforced by the `commit-msg` hook once `./visual-relay install-hooks` has been
run).

## Build & checks

- Build, run, and test through the single entry point: `./visual-relay build`,
  `./visual-relay test`, `./visual-relay launch`.
- Run the full gate before considering work done: `./visual-relay check`
  (file-size guard, format verification, build, tests, screenshot render).
- Keep C# and Avalonia XAML source files under 300 lines
  (`tools/guards/check-file-size.sh`).
- If `./visual-relay test` hangs (sits at `Testing (NNNs)` with nothing completing), it's a
  deadlock, not a slow test. Find the culprit with
  `./visual-relay test --blame-hang --blame-hang-timeout 30s`. See `TROUBLESHOOTING.md`.
- Headless UI tests must use `[AvaloniaFact]`/`[AvaloniaTheory]` (Avalonia.Headless.XUnit);
  `HeadlessUnitTestSession` is banned (BannedApiAnalyzers) — reintroducing it fails the build.

See `README.md` for the full project overview and `TROUBLESHOOTING.md` for diagnosing the
dev loop.

## Sample Tasks (dev-only)

The following tools are available in source checkouts but are **not shipped** in the
Homebrew formula:

- `./visual-relay sample-reset <path>` — regenerates a sample tasks repository with
  repeatable demo state (runs `tools/VisualRelay.SampleTasks`).
- `./visual-relay run-task <path> <task>` — runs a single task headlessly through the
  full Relay pipeline (runs `tools/VisualRelay.RunTask`).
- `./visual-relay screenshot` — renders README screenshots via Avalonia Headless
  (runs `tools/VisualRelay.Screenshots`).

These require a .NET SDK and a full source checkout. Brew-installed users only have
`launch` and `init`.
