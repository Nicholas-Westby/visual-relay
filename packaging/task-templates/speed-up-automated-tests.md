---
name: Create Tasks to Speed Up Automated Tests
title: Speed up automated tests
---
Make this repository's automated test suite faster without losing any coverage. This
task has two deliverables:

1. Speed up exactly one automated test — the highest-value target found by the
   measurement step below — and commit that change with before/after timing evidence
   in the commit message, per "Commit-message evidence" below.
2. Author one follow-up LLM task per remaining opportunity (sibling task folders next to
   this one, following the existing folder-naming convention), so the rest of the suite
   gets faster incrementally. Each follow-up task must be opinionated: exactly one
   prescribed approach, never a menu of options.

## Measure first — every decision is data-informed

- Run the full suite once with this repo's own test command, capturing per-test timings
  from the runner's native timing/report output. Save the timings to a file in this
  task's folder (e.g. timings-baseline.txt), sorted slowest-first. Every slowness claim
  in this task and in every follow-up task must trace to numbers in that file.
- Expect a Pareto shape: a small fraction of tests usually accounts for most of the wall
  time. Rank three views: slowest individual tests, slowest test files/suites, and any
  serial phase the rest of the suite waits on.
- Full-suite timings are contention-inflated. A test that looks slow under a loaded
  worker pool can be fast alone — re-time the top candidates in isolation before
  choosing what to fix.
- Classify the suite: wait-bound (wall time far exceeds CPU time — threads idle in
  sleeps, polling, I/O, or child processes) or CPU-bound. The high-payoff remedies
  differ, and misclassifying wastes the whole effort.

## Coverage is non-negotiable

- Never delete, disable, skip, or weaken a test to make the suite faster. Speed must
  come from doing the same verification cheaper, not from verifying less.
- Any change that moves, merges, or replaces tests must include a written mapping:
  every original test name → where that scenario and its assertions now live. A
  scenario with no destination is lost coverage — do not proceed with that change.
- A test that genuinely must exercise a slow real boundary (real network, real child
  processes, real disk, real time) is still valuable: keep it, move it behind an opt-in
  slow/integration tag, and cover the same logic with a fast in-process equivalent in
  the default suite. Gated is fine; gone is not.
- After each change the whole suite must pass, and if the test count changed, the
  mapping above must account for every removed name.

## Strategies that are known to work (roughly in payoff order)

1. Remove real-time waits. Fixed sleeps and long-interval polling are the classic
   hidden cost. Replace them with waits on the actual condition or event, and route
   deadline/timer logic through an injectable clock so tests drive time instead of
   living through it. If the codebase has many of these, one follow-up task should add
   an automated guard that fails on new unjustified sleeps in tests.
2. Put a seam in front of expensive boundaries. Tests that spawn real child processes
   or hit real network/disk per test are usually the slowest family. Introduce an
   explicit interface at the boundary and use an in-memory fake or simulator in the
   default suite; keep a small tagged set of real-boundary integration tests per the
   coverage rules above.
3. Turn on and tune runner parallelism. Check the runner's parallelism settings first —
   suites often run far below the machine's capacity. Prefer machine-relative worker
   counts (multipliers) over hardcoded numbers so every machine scales. A wait-bound
   suite benefits from oversubscribing workers beyond the core count; a CPU-bound one
   does not.
4. Split oversized test files. Most runners parallelize across files/classes/suites,
   not within them, so one huge file serializes into a long tail. Split it into several
   smaller ones as pure moves — same tests, same assertions — so the scheduler can
   spread the load.
5. Know the parallelism pitfalls before flipping the switch. Tests sharing mutable
   state flake in parallel: global/static singletons, environment variables, current
   working directory, fixed ports, shared temp paths or fixture files, a shared
   database, and order-dependent tests. Fix isolation first (own data, unique
   ports/paths per test), and explicitly serialize the few genuinely serial tests as a
   named group instead of lowering parallelism for everyone. Budget for two companion
   effects: individual tests run slower under contention, so per-test hang/timeout
   ceilings usually must rise together with parallelism; and timing-sensitive
   assertions that pass solo can lose races under load. Prove stability with several
   consecutive green full-suite runs, and fix a flake by removing the shared state or
   serializing that test — never by retrying until green.
6. Merge near-duplicate tests into one data-driven test. When several tests share the
   same arrange/act/assert shape and differ only in inputs, convert them into a single
   parameterized test where each scenario is a data row. This cuts per-test setup
   overhead. Enumerate the originals first and map each one to a row, per the coverage
   rules.
7. Hoist expensive immutable setup. Setup every test rebuilds identically (compiled
   artifacts, seeded stores, parsed fixtures) can be built once at the widest safe
   scope and shared read-only. Never share anything a test mutates.
8. Look for product-side waste. Sometimes the suite is slow because the product wastes
   time on paths tests exercise constantly — e.g. a retry backoff triggered by a
   benign, expected condition. Fixing the product speeds up the suite and the product.

## Investigate beyond this list

The list above is generic. Spend real time in this specific codebase: read the
test-runner configuration and its parallelism/timeout settings, the CI test invocation,
custom fixtures and helpers, and the slowest files from the timings file. Anything
repo-specific you find — an expensive fixture everyone pays for, an artifact rebuilt
per test, a serialized phase — becomes its own follow-up task with its measured cost
cited.

## The one fix to make in this task

From the timings file, pick the single change with the best ratio of measured time
saved to risk, scoped to one test (or the one shared wait/fixture that dominates that
test's time). Implement it, run the full suite to green, and re-measure: record the
before and after numbers for the affected test and for the whole suite next to the
baseline file, and carry the measured result into the commit message per
"Commit-message evidence" below.

## Follow-up tasks to author

Create one task folder beside this one per remaining opportunity. Each follow-up task
must:

- be self-contained — executable without reading this task. Quote the relevant
  baseline numbers inline, and cite the timings file at the path it will have after
  this task completes and its folder is archived:
  `llm-tasks/completed/<this-task-folder-name>/timings-baseline.txt`;
- prescribe exactly one approach, with the pitfalls above baked in as guardrails;
- cite its target tests' measured baseline numbers from the timings file and state
  the expected saving, clearly labeled as an estimate;
- restate the coverage rules: no deleted/skipped/weakened tests, and a name-by-name
  mapping for any moved or merged test;
- carry the commit-message instructions with it. Copy `commit-message-evidence.md`
  from this task's folder into each follow-up task's folder, unchanged;
- end with a short "Commit-message evidence" section that says only this: measure
  before and after while implementing, then put one filled-in evidence bullet in the
  commit message body, following the attached `commit-message-evidence.md`.
  Never pre-fill that bullet — a task file must not contain an evidence bullet with
  concrete numbers, measured or predicted. Numbers are measured at implementation
  time and go into the eventual commit message, nowhere else.

## Commit-message evidence (this task and every follow-up)

This task's folder contains `commit-message-evidence.md`: the required bullet shape
and the rules for filling it in. Every commit that changes test timing — this task's
one fix and every follow-up implemented later — must carry exactly one such bullet in
its commit message body, filled with real numbers measured before and after the
change, never estimates or predictions. This makes the payoff of every change legible
straight from the log.
