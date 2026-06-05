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

See `README.md` for the full project overview.
