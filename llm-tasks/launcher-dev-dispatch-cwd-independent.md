# Source-checkout launcher subcommands break when invoked from another directory

`visual-relay init` run from a target repo (the README's documented flow) fails in
source checkouts: `The provided file path does not exist:
tools/VisualRelay.Init/VisualRelay.Init.csproj` (observed 2026-06-10 from
~/Dev/JobFinder). The dev-mode dispatch passes **cwd-relative** project paths to
`dotnet run --project tools/...`, so every dotnet-run case silently assumes cwd is the
VR repo root. Brew installs are immune (published binaries resolve via the absolute
`$SCRIPT_DIR`); dev users hit it for `init` (the primary cross-repo subcommand) and any
other subcommand invoked from elsewhere.

## Goal

Every launcher subcommand behaves identically regardless of the caller's cwd, in both
dev (dotnet run) and published dispatch. `cd anywhere && /path/to/visual-relay init`
works against the current directory as the target repo, like the brew flow.

## Approach (suggested)

- Anchor all dev-dispatch project paths to `$SCRIPT_DIR` (e.g.
  `"$SCRIPT_DIR/tools/VisualRelay.Init/VisualRelay.Init.csproj"`), and audit every
  `--project`/file reference in the script for cwd assumptions (`run-task`,
  `gen-backend-config`, `screenshot`, guards, backend.sh path math).
- Make `init` (and any cwd-meaningful subcommand) pass the ORIGINAL caller cwd to the
  tool explicitly — note the nix re-entry (`nix develop --command bash "$0" ...`) may
  change cwd; capture `$PWD` before re-entry and forward it (the Init tool already
  accepts a root-path argument).
- Tests: extend the launcher xunit coverage — run the launcher from a temp cwd outside
  the repo with a stub dotnet recording argv+cwd; assert the project path is absolute
  and the target root forwarded is the original cwd. Mirror for the nix re-entry path
  (stub nix), composing with the existing arg-preservation test.
