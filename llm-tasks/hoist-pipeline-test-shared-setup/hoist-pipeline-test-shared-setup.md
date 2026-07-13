## Task: Hoist shared TestRepository+GitSim seed into a pipeline test fixture

Introduce a reusable test fixture that creates and seeds a `TestRepository` + `GitSimEngine` + `RelayConfig` once per class (or assembly-wide), so full-pipeline integration tests share the expensive immutable setup instead of each paying it individually.

### Baseline measurements

From `llm-tasks/speed-up-automated-tests/timings-baseline.txt`, these always-on test files each create their own TestRepository, write config, write task files, init a GitSim, and seed from scratch **per test**:

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

2. Convert each of the 6+ test classes to use `[Collection("Pipeline")]` and inject `PipelineTestFixture` via constructor (the standard xUnit `IClassFixture<PipelineTestFixture>` pattern). Tests that need a specific config variant or different task files can still use `TestRepository.Create()` directly — only the tests that match the "standard pipeline shape" (one or two tasks, standard config, GitSim seed) use the fixture.

3. **Clone-on-write**: Tests that mutate the repo (and they all do — RelayDriver commits, archives tasks, modifies files) must get a **copy** of the seed, not the original. The fixture provides a `Clone()` method that copies the seed directory to a new temp dir, re-inits a fresh `GitSimEngine` pointing at the clone, and returns the clone. This is the same pattern as `TestRepository.Create()` but starting from a pre-seeded directory instead of an empty one. The per-test cost becomes a file-copy instead of full init+seed+commit.

4. Add `PipelineTestFixture` alongside `PipelineTestFixture.Seeder.cs` (a partial with a static helper that writes the standard seed). Keep the seeder simple: one config, one task file, one baseline source file.

### Expected saving

Each test currently spends ~2-3 s on setup (directory creation, file writes, GitSim init, seed, commit). With the fixture, setup becomes a directory copy (~0.1-0.2 s). Across ~15-20 pipeline tests, expected saving: **5–10 s** from full-suite wall time.

### Pitfalls and guardrails

- **Never share anything a test mutates.** The fixture provides a `Clone()` method; every test calls it and gets its own disposable clone. The seed directory is never written to by any test.
- **Do NOT convert every test.** Only tests that match the standard pipeline shape use the fixture. Tests with unusual configs, custom task layouts, or special runner setups keep their own `TestRepository.Create()`.
- **Clone must be fast.** Use directory-level copy (not per-file). On macOS, `cp -R` or `Directory.CreateDirectory` + file copies. Profile the clone time — if it's >0.5 s, the fixture isn't worth it.
- **Do not share GitSimEngine across tests.** Each test gets its own sim pointing at its own clone directory.
- **Test count stays the same.** No tests are deleted, skipped, or weakened.
- **The `SplitGuardVerificationTests` convention guard checks that every `[Collection]` attribute is correct.** The new `[Collection("Pipeline")]` must follow existing conventions (explicitly declared, single-purpose).

### Coverage rules

- Never delete, disable, skip, or weaken a test. Speed comes from doing the same verification cheaper.
- No tests are moved or merged — only their setup is refactored. No coverage mapping is needed.
- If a test's assertions change behavior after fixture adoption (e.g., a test that relied on a specific seed file not present in the standard seed), give that test its own `TestRepository.Create()` instead of forcing it into the fixture.

### Commit-message evidence

```
- test time dropped from 119s to 113s, saving 6s (full-suite wall time)
```
