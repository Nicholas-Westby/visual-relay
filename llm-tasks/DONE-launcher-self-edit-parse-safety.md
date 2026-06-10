# Editing the launcher mid-run corrupts the running bash invocation (self-edit parse hazard)

Bash parses script files **incrementally by byte offset**: while a long-running
subcommand executes inside a dispatch case, the remainder of the script is unread. If
the file changes meanwhile, bash resumes parsing the *new* file at the *old* offset.

Observed (2026-06-09, sandbox-2 drive): `visual-relay run-task … sandbox-2-…` ran for
69 min and its Implement stage edited the `visual-relay` launcher itself (the task added
nono prerequisite checks). The pipeline committed successfully
(`Committed: sandbox-2-… eaef5be`), then — as `dotnet run` returned and bash resumed —
the invocation died with `visual-relay: line 184: syntax error near unexpected token ')'`
and exit code 2. The drive recorded a false "FLAGGED" for a task that actually
committed. In a self-hosting pipeline that routinely edits its own launcher, this strikes
on **every** launcher-touching task; the corrupted exit code also poisons any tooling
that scripts over `run-task` exit codes (0=committed / 2=flagged).

## Goal

In-place edits to the `visual-relay` script can never affect an already-running
invocation: the script's full control flow is parsed before any subcommand executes, and
the process's exit code is always the subcommand's real result. (Same guarantee for any
other long-running shell entry points in the repo, e.g. `tools/backend/backend.sh`, if
they share the hazard.)

## Approach (suggested)

- Standard hardening: wrap the entire body in a function and invoke it at the bottom
  with an explicit exit so bash never returns to top-level parsing:
  `main() { …existing body…; }` + final line `main "$@"; exit $?` (the `exit` on the
  same line as the call is the load-bearing part). Preserve the existing
  `exec`-based re-entry paths (an `exec` replaces the process and is immune anyway).
- Audit `tools/backend/backend.sh` and any other multi-minute shell entry points for the
  same pattern; apply the same wrap where a pipeline task could plausibly edit them.
- Test: extend the existing launcher xunit coverage (e.g.
  `Installer5LauncherTests.cs` / `Installer5Sandbox2LauncherTests.cs` style): run a copy
  of the launcher via `bash` with a stubbed PATH whose fake `dotnet` (or stub
  subcommand) **appends garbage to the running script file** before exiting 0; assert
  the launcher still exits 0 with no syntax error on stderr. A structural assertion
  (script ends with the `main "$@"; exit` invocation) is an acceptable complement but
  not a substitute for the behavioral test.
