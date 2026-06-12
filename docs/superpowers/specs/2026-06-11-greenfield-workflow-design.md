# Greenfield Project Workflow

- **Date:** 2026-06-11
- **Status:** Draft (proposal) — not yet approved; captured for future consideration. No implementation planned.
- **Audience:** Maintainer evaluating whether/how Visual Relay should grow a second workflow alongside the existing 11-stage Relay pipeline.

## Problem

The existing workflow (`RelayStages.cs`) is built for brownfield work — features and
bugfixes inside an existing repository. Its load-bearing assumptions all fail at
time zero of a new project:

1. **Research/Diagnose assume a codebase and logs to read.** An empty directory
   has neither; what's needed instead is requirements elicitation, stack
   selection, and architecture design.
2. **The Plan manifest enumerates *existing* impacted files.** In greenfield the
   files don't exist yet, and "the whole project" is not a reviewable manifest.
3. **The red gate assumes a trusted test harness.** Authored tests "must fail
   before implementation" only means something once a working build + test
   runner exists. Greenfield must first *establish* the harness.
4. **`init` detects `testCmd` from existing project markers.** A greenfield run
   has no markers; the workflow must *choose* and pin the command itself.
5. **One task → one sealed commit.** A whole project squeezed into one commit
   produces an unreviewably large diff and a single all-or-nothing verify.

## Key insight: bootstrap, then delegate

After its first few commits, a greenfield project **becomes** a brownfield
project — and Visual Relay already owns a machine that is good at brownfield.

So the greenfield workflow should not replace the implement loop. Its job is to
take an idea from empty directory to *"a repo where the existing workflow can
run"*, and end by **generating ordered task files into `llm-tasks/`** that the
existing 11-stage pipeline then executes one sealed commit at a time, with all
its red/green gates intact. The greenfield workflow manufactures the
preconditions; the brownfield workflow does the building.

## Goals

- An idea (one markdown task) becomes a repo with a working build/test harness,
  a validated architecture skeleton, and a dependency-ordered task queue.
- Every downstream increment runs through the existing pipeline unchanged.
- The expensive, hard-to-reverse decisions (stack, architecture) get cheap human
  checkpoints before any code is generated.
- Reuses existing machinery wherever it transfers: tiers, JSON output contracts,
  corrective retries, ledger/seals, flagged tasks, sandbox, pause-at-boundary.

## Non-goals

- No changes to the existing 11-stage workflow's semantics (one possible
  adjacent tweak — skipping Diagnose for non-bug tasks — is noted but out of
  scope).
- No project-template library; the Scaffold stage generates from the chosen
  stack, it does not pick from canned templates.
- No multi-repo or monorepo orchestration.

## Proposed stages

| # | Stage | Tier | Kind | Writes | Gate at acceptance |
| --- | --- | --- | --- | --- | --- |
| 1 | Elicit | cheap | llm | none | Open questions present → flag NEEDS-REVIEW |
| 2 | Stack | balanced | llm | none | `testCmd` non-empty/well-formed; driver pins it |
| 3 | Architect | frontier | llm | none | Default pause-at-boundary (human checkpoint) |
| 4 | Scaffold | balanced | llm | all | **Build gate:** pinned `testCmd` runs green |
| 5 | Skeleton | balanced | llm | all | Tests pass; failure loops back to stage 3 |
| 6 | Decompose | frontier | llm | `llm-tasks/` only | Task DAG valid; acceptance criteria per task |
| 7 | Review | frontier | llm | none | Verdict pass/changes (same contract as today) |
| 8 | Fix | balanced | llm | all | Review blockers resolved |
| 9 | Commit | cheap | driver | none | Sealed bootstrap commit; queue takeover |
| 10 | Audit | balanced | llm | `llm-tasks/` only | Runs when child queue drains; emits gap tasks or passes |

### 1. Elicit (cheap, read-only)

Turn the idea markdown into a product brief.

```
{ "brief": string, "nonGoals": string[], "successCriteria": string[], "openQuestions": string[] }
```

**Gate:** if `openQuestions` is non-empty, flag the task NEEDS-REVIEW instead of
guessing. Ambiguity at stage 1 compounds through everything downstream; this is
the cheapest possible place for a human checkpoint.

### 2. Stack (balanced, read-only)

Choose language, framework, build tool, test framework — and critically, the
**pinned `testCmd`** the rest of the run is gated on. Brownfield `init`
*detects* the test command; greenfield must *choose* one and commit to it here.

```
{ "stack": { "language": string, "framework": string, "buildTool": string, "testFramework": string },
  "testCmd": string, "alternatives": string[], "rationale": string }
```

**Gate:** driver validates `testCmd` is present and plausibly runnable, then
pins it for the rest of the run (and for the `.relay/config.json` written at
stage 4).

### 3. Architect (frontier, read-only)

Module boundaries, data model, key interfaces, directory layout. Frontier tier
is justified: these are the highest-leverage decisions in the run and every
later stage inherits their errors.

```
{ "architecture": string, "components": [{ "name": string, "purpose": string, "interface": string }],
  "layout": string[] }
```

**Gate:** default pause-at-boundary after this stage (config-overridable, see
Defaults) so a human can veto stack/architecture before any files are written.

### 4. Scaffold (balanced, writes: all)

First writing stage. Generate the skeleton: build files, directory tree, test
harness wiring, `.gitignore`, README stub, hello-world entrypoint, and one
trivial **passing** smoke test.

**Gate (the greenfield analog of the red gate — the build gate):** the driver
runs the pinned `testCmd`; the harness must *execute and pass*. This proves the
feedback loop exists before any feature work. At this acceptance boundary the
driver also:

- runs `git init` if the selected directory is not yet a repo, and
- writes `.relay/config.json` with the pinned `testCmd` — the greenfield
  workflow produces its own `init` artifacts.

Note the deliberate inversion: brownfield proves tests *fail first* because the
harness is trusted; greenfield must first prove the harness *works at all*
(green smoke test). TDD discipline resumes per-increment once delegation
starts.

`files: all` is safe here precisely because the workspace is empty and the run
is sandboxed (nono confines writes to the workspace).

### 5. Skeleton — walking skeleton (balanced, writes: all)

Implement one thin end-to-end slice through every architectural layer (e.g.
input → core → output) with a real test.

```
{ "summary": string, "slice": string }
```

**Gate:** tests pass. Kept separate from Scaffold because the failure modes
route differently: scaffold failure means fix tooling (retry stage 4); skeleton
failure means **the architecture doesn't build — loop back to stage 3**
(bounded by config, analogous to `MaxVerifyLoops`).

### 6. Decompose (frontier, writes: `llm-tasks/` only)

Read brief + architecture + skeleton; emit ordered, dependency-aware increment
tasks as `llm-tasks/NN-<id>.md`, each sized like a normal Relay task (one
commit's worth) with explicit acceptance criteria.

```
{ "tasks": [{ "id": string, "title": string, "dependsOn": string[] }], "roadmap": string }
```

**Gate:** driver validates the DAG (no cycles, all deps exist), confirms every
task file carries acceptance criteria, and numbers files for queue order.

**Rolling decomposition:** rather than emitting 40 speculative tasks up front,
fully emit only milestone 1 and write later milestones into a `PLAN.md`
roadmap. Early tasks stay grounded in code that actually exists, avoiding task
rot; stage 10 re-decomposes when the queue drains.

This stage is the convergence point: its "manifest" is the task DAG, and its
output is consumed by the *existing* workflow.

### 7. Review (frontier, read-only)

Review the skeleton diff *and* the generated task list against the brief: scope
gaps, over-engineering, architecture/brief mismatch. Same verdict contract as
the existing stage 7 (`pass`/`changes` + issues). Reviews *plan completeness*
as much as code.

### 8. Fix (balanced, writes: all)

Resolve every review blocker and warning — in the skeleton or in the generated
task files.

### 9. Commit (driver)

Sealed bootstrap commit of skeleton + `.relay/config.json` + generated task
queue, with a trailer marking it greenfield-bootstrap (alongside the usual
`Task:`/`Relay-Seal:` trailers). Generated child tasks carry a provenance link
back to the bootstrap task (trailer/ledger), so the driver can tell when the
child queue has drained.

The greenfield task then parks in a **delegated** state rather than archiving:
the existing 11-stage workflow takes over the queue, one increment per sealed
commit.

### 10. Audit — epilogue (balanced, writes: `llm-tasks/` only)

When the child queue drains, the driver resumes the parked greenfield task at
this stage (the existing resume machinery fits). Audit re-reads the brief's
`successCriteria` against the now-real project and either:

- emits gap tasks / the next milestone's tasks (back to delegated), or
- returns done → the greenfield task archives as DONE.

Bounded by a `maxAuditLoops`-style config (default 1–2) to prevent infinite
convergence-chasing. This stage exists in the greenfield workflow because the
brownfield pipeline *cannot* write task files (stage-4 acceptance explicitly
rejects manifest entries under the tasks directory).

## What transfers, what inverts

**Transfers unchanged:** tiers and `tierProfiles`, JSON output contracts with
corrective retries, ledger/seals, flagged tasks (NEEDS-REVIEW), nono
sandboxing, pause-at-boundary UX, cost estimation from Swival reports.

**Inverts or changes meaning:**

- **Red gate → build gate** at stage 4 (prove the harness green before TDD can
  mean anything; red-gate discipline resumes per child task).
- **Manifest → task DAG** at stage 6 (nothing to enumerate until files exist).
- **Parallel planning worktrees don't apply within a run** — stages 1–3 are
  sequential by nature (each consumes the previous stage's output). Multiple
  greenfield *projects* still parallelize as today.
- **One task → one commit becomes one task → bootstrap commit + delegated
  children + park/resume lifecycle** (pending → stages 1–9 → delegated →
  Audit → done).

## Defaults (decided for the draft)

- **Starting point:** the run begins from whatever directory is selected in the
  GUI, empty or not; the driver runs `git init` during Scaffold acceptance if
  no repo exists. No separate "create project" flow.
- **Human gating:** auto-flag at Elicit only when open questions exist, plus a
  default pause-at-boundary after Architect — overridable in
  `.relay/config.json` (e.g. `"pauseAfterStage": []` to run straight through).
  Everything else runs unattended, same as today.

## Alternatives considered and rejected

1. **One monolithic pipeline** (spec → build entire project → single commit):
   loses incremental gates, produces an unreviewably large diff, and one failed
   verify poisons the whole run.
2. **A self-contained looping driver** (greenfield workflow internally loops
   increments with its own mini red/green gates and multiple commits inside one
   task): finer re-planning control, but duplicates `RelayDriver` machinery and
   breaks the one-task-one-commit invariant that seals and archiving assume.
   The Audit epilogue + rolling decomposition recover most of its benefit
   without the cost.

## Open questions (to think on)

1. **Workflow selection.** How is a task marked greenfield? Leaning: a
   frontmatter marker in the task markdown (`workflow: greenfield`) keeps task
   files the single source of truth; a GUI "New project…" affordance could
   write it. Alternative: infer from "selected directory has no repo/config".
2. **Implementation lift.** `RelayStages.All` is a single hardcoded list and
   `RelayDriver` has stage-number-specific acceptance logic baked in. A second
   workflow forces a `RelayWorkflow { name, stages, gates }` abstraction —
   this is the largest implementation cost of the whole proposal and worth
   sizing before committing to anything.
3. **Cost profile.** Three frontier stages per bootstrap (Architect, Decompose,
   Review) vs. one in brownfield. Acceptable for a once-per-project cost?
   Could Stack merge into Architect to cut a call, at the price of a bigger
   contract and a coarser human checkpoint?
4. **Child-task experience in the existing pipeline.** Generated feature tasks
   have no logs for Diagnose to read — does Diagnose no-op gracefully, or is a
   "skip Diagnose for non-bug tasks" tweak warranted? (Adjacent improvement,
   currently out of scope.)
5. **Queue-drained trigger.** What exactly resumes the parked task at Audit —
   driver watcher on queue state, or a manual "run audit" action in the GUI?
6. **Decompose sizing heuristic.** "One commit's worth" needs a concrete
   definition the gate can check (e.g. estimated manifest ≤ N files, single
   acceptance criterion cluster).
