> **IGNORE (2026-06-16) — deferred, not abandoned.** The concrete build break this task generalizes
> was already **hotfixed out-of-band** (`b51eec3` added the missing `GitInvoker` arg), so the blind
> spot is mitigated today. This task was then driven and **flagged on Fix-verify non-determinism**
> (not a real defect): with the sandbox on, `app-launch-failed` did not recur (the `-c` fix held), a
> whole-solution `dotnet build VisualRelay.slnx` **succeeds under nono** (verified directly), and the
> suite passes standalone (1046/0/11). The implementation it produced runs the whole-solution build on
> **every** fix-verify iteration, which lengthens verify and amplifies the intermittent flakiness —
> shipping that is net-negative.
>
> **Refined design for a future revive:** run the whole-solution compile-check **once at the commit
> gate** (stage 11, before the commit), NOT inside the per-iteration fix-verify loop — that catches a
> `tools/` build break before commit without making routine verify heavier or flakier. Un-IGNORE and
> drive with that design if desired. Original spec below.

# Harness: verify must compile every shipped project, not just the test project

A refactor that breaks a project **outside the test project's build graph** passes Verify and the guard,
commits, and then breaks the harness's own tooling on the *next* run. **Observed 2026-06-16:** the
`inject-seams` refactor (commit `922da56`) added a required `GitInvoker` parameter to
`RelayDriverDependencies` and updated the App + DrainQueue call sites but **missed
`tools/VisualRelay.RunTask/Program.cs`** (CS7036). The test command —
`dotnet test tests/VisualRelay.Tests/…` — builds only the test project and its references
(Core/Domain/App), **not** the `tools/` projects, so the break shipped green and **blocked every
subsequent `run-task`** (run-task builds `RunTask` first → fails). It had to be fixed out-of-band
(commit `b51eec3`).

This is the [[pipeline-mocks-process-layer-blindspot]] class: verify's **build scope ⊊ the set of
artifacts the harness actually runs**. A self-hosting harness must compile everything it ships (CLI
tools, drain queue, launchers), or a refactor can silently break the very binaries the pipeline uses.

General harness change — keep it platform-agnostic.

## Root cause

`Verify`/the guard validate behavior of the test project's graph, but the harness *runs* additional
projects (`tools/VisualRelay.RunTask`, `tools/VisualRelay.DrainQueue`, the App, the Init tool) that
nothing in the verify path compiles. The repo's guard runs `dotnet format VisualRelay.slnx
--verify-no-changes`, which does not guarantee a full compile of every project either. So a
compile-time break in an un-referenced project is invisible until that project is launched.

## What to build

The fix has a general principle and a this-repo application:

1. **General:** before accepting Verify, the harness should ensure **all shipped projects compile**,
   not just the unit-test target. The cleanest, language-agnostic expression is a repo-provided
   "build everything" step that the verify routine runs (like the existing `formatCmd`/`guardCmd`
   pattern) — for a multi-project solution that means building the whole solution, not one project.
   Init's command-detection should, where it can, configure this (e.g. detect a `.sln`/`.slnx` and
   prefer a whole-solution build/verify) so the gap does not recur on other multi-project repos.
   Do **not** hardcode .NET — express it as "the repo's full-build command" config + detection.
2. **This repo:** add a whole-solution build to the guard (or test) command so `tools/` is compiled
   in verify — e.g. prepend `dotnet build VisualRelay.slnx` to `guardCmd` in `.relay/config.json`,
   and have init emit that for this repo. Confirm a deliberately-broken `tools/` call site then fails
   verify.

## Tests

- A guard/verify-level test: a deliberately-uncompilable edit in `tools/VisualRelay.RunTask`
  (e.g. a call-site arity mismatch) causes Verify/the guard to fail (red), proving the tools are now
  in the compile scope. (Keep it a fast unit/guard assertion — don't add a slow real build to the
  default suite; respect the `VR_RUN_NONO_INTEGRATION` gate for any heavy build.)
- Init-detection test: for a repo with a `.sln`/`.slnx`, the detected verify/build covers the whole
  solution.

## Done when

- A compile break in any shipped project (including `tools/`) fails Verify/the guard **before** commit.
- The whole-build step is repo-configured (not hardcoded to .NET) and init configures it for
  multi-project solutions.
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit subjects.

## Notes

- This is the general lesson behind the out-of-band `b51eec3` fix: don't let verify's build scope be
  narrower than what the harness runs. Pairs with the existing guidance to smoke-test `run-task`
  after harness/refactor changes.
