## Task: Hoist shared TestRepository+GitSim seed into a pipeline test fixture

Introduce a reusable test fixture that creates and seeds a `TestRepository` + `GitSimEngine` + `RelayConfig` once per class (or assembly-wide), so full-pipeline integration tests share the expensive immutable setup instead of each paying it individually.

### Baseline measurements

From `llm-tasks/completed/speed-up-automated-tests/timings-baseline.txt`, these always-on test files each create their own TestRepository, write config, write task files, init a GitSim, and seed from scratch **per test**:

| Test file | ~file total | Tests | Setup per test |
|---|---|---|---|
| NoCommitContaminationTests | ~35 s | 3 tests | Repo + config + 2 tasks + GitSim init + seed + PlanPhaseRunner |
| TaskCompletionArchiveNoBatchTests | ~14 s | 4 tests | Repo + config + 1 task + GitSim init + seed |
| RelayDriverGitCommitTests | ~10 s | 3+ tests | Repo + config + task + GitSim init + seed |
| RelayDriverGitCommitRetirementTests | ~12 s | 4+ tests | Repo + config + task + GitSim init + seed |
| RelayDriverResumeTests | ~10 s | 2+ tests | Repo + config + task + GitSim init + seed |
| RelayDriverTests | ~9 s | tests | Repo + config + task + GitSim init + seed |

Total: **~90 s** across ~6 files. Every test in these files creates a fresh `TestRepository` (temp directory via `Guid.NewGuid`), writes `visual-relay/relay.json`, writes `llm-tasks/<id>.md`, calls `RelayDriverTestHelpers.InitSim(repo)`, calls `sim.Seed(...)`, and calls `sim.Commit(...)`. This setup is **immutable and read-only** — no test mutates the seed state that another test depends on.

### Prescribed approach

1. Create a `PipelineTestFixture : IAsyncLifetime` class in `tests/VisualRelay.Tests/` that:
   - Creates a single `TestRepository` (one temp directory) in `InitializeAsync`.
   - Writes a standard config (`dotnet test`, archiveOnDone:true, etc.).
   - Writes one or more task files.
   - Inits a `GitSimEngine`, seeds with a baseline file, and commits.
   - Exposes the repo root path, the sim instance, and the seed commit hash as properties.
   - Cleans up in `DisposeAsync`.

2. Register the fixture assembly-wide with xUnit v3's `[assembly: AssemblyFixture(typeof(PipelineTestFixture))]` and inject it into each of the 6+ test classes via a constructor parameter. Do **not** put these classes into a shared `[Collection]`: xUnit parallelizes across collections, so one shared collection would serialize ~90 s of currently-parallel test files — likely losing more wall time than the fixture saves. The repo has no class/collection fixture precedent; the assembly fixture builds the seed exactly once without changing scheduling. Tests that need a specific config variant or different task files can still use `TestRepository.Create()` directly — only the tests that match the "standard pipeline shape" (one or two tasks, standard config, GitSim seed) use the fixture.

3. **Clone-on-write**: Tests that mutate the repo (and they all do — RelayDriver commits, archives tasks, modifies files) must get a **copy** of the seed, not the original. The fixture provides a `Clone()` method that copies the seed directory to a new temp dir, re-inits a fresh `GitSimEngine` pointing at the clone, and returns the clone. This is the same pattern as `TestRepository.Create()` but starting from a pre-seeded directory instead of an empty one. The per-test cost becomes a file-copy instead of full init+seed+commit.

4. Add `PipelineTestFixture` alongside `PipelineTestFixture.Seeder.cs` (a partial with a static helper that writes the standard seed). Keep the seeder simple: one config, one task file, one baseline source file.

### Expected saving (estimate — verify by measurement)

Each test currently spends ~2-3 s on setup (directory creation, file writes, GitSim init, seed, commit). With the fixture, setup becomes a directory copy (~0.1-0.2 s). Across ~15-20 pipeline tests, the estimated saving is **5–10 s** of full-suite wall time. This is an estimate, not a measurement — the real numbers come from timing the suite before and after, per the commit-message evidence section below.

### Pitfalls and guardrails

- **Never share anything a test mutates.** The fixture provides a `Clone()` method; every test calls it and gets its own disposable clone. The seed directory is never written to by any test.
- **Clone must be concurrency-safe.** The assembly fixture is visible to tests running in parallel: the seed is written once in `InitializeAsync` and read-only afterwards, and `Clone()` must be a pure directory copy safe to call from multiple tests at once.
- **Do NOT convert every test.** Only tests that match the standard pipeline shape use the fixture. Tests with unusual configs, custom task layouts, or special runner setups keep their own `TestRepository.Create()`.
- **Clone must be fast.** Use directory-level copy (not per-file). On macOS, `cp -R` or `Directory.CreateDirectory` + file copies. Profile the clone time — if it's >0.5 s, the fixture isn't worth it.
- **Do not share GitSimEngine across tests.** Each test gets its own sim pointing at its own clone directory.
- **Test count stays the same.** No tests are deleted, skipped, or weakened.
- **No `[Collection]` attributes.** These classes are not headless and today run as independent parallel collections; adding a shared collection attribute would serialize them (see the prescribed approach). The `SplitGuardVerificationTests` convention guards must stay green.

### Coverage rules

- Never delete, disable, skip, or weaken a test. Speed comes from doing the same verification cheaper.
- No tests are moved or merged — only their setup is refactored. No coverage mapping is needed.
- If a test's assertions change behavior after fixture adoption (e.g., a test that relied on a specific seed file not present in the standard seed), give that test its own `TestRepository.Create()` instead of forcing it into the fixture.

### Commit-message evidence

Measure the full-suite wall time right before starting and right after finishing.
Then put exactly one filled-in evidence bullet in the commit message body, following
the attached `commit-message-evidence.md`. Do not write the numbers into this task
file — real measured numbers belong in the commit message only.
