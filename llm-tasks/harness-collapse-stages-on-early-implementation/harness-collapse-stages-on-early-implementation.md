# Harness: down-shift redundant narration stages when implementation is front-loaded

The coding agent sometimes FRONT-LOADS the whole change into an early reasoning stage —
in one observed run, **stage 3 Diagnose** issued every source edit AND wrote the test
files. The harness then paid full freight for **stage 4 Plan** and **stage 6 Implement**
even though both produced ZERO file mutations and merely re-narrated already-done work
(~$0.034, ≈18% of run cost wasted); **stage 5 Author-tests** wrote 0 new tests. The harness
has no detection for "implementation already underway," so this task adds one and uses it to
DOWN-SHIFT the now-redundant Implement stage (and, when front-loading predates the manifest,
the Plan stage) onto the cheapest tier with a "confirm/amend only, do not re-narrate"
instruction — never a hard skip. The Review quality gate (stage 7, frontier) is untouched.

## Current state (researched)

> **Freshness contract.** Every code reference below was read at `/Users/admin/Dev/vr-driver`
> @ commit `a574ee8`. Do NOT trust the line numbers — locate each anchor by searching the
> exact quoted snippet, then re-read the surrounding code before editing. If a quoted snippet
> is not found verbatim, STOP and re-research: the file moved under you and the plan needs
> refreshing, not forcing.

### The stage loop and where tiers are chosen

`src/VisualRelay.Core/Execution/RelayDriver.cs` — `RunTaskAsync` runs the single
`foreach (var stage in RelayStages.All)` loop (search `"foreach (var stage in RelayStages.All)"`).
For each non-driver stage it builds an invocation and runs the subagent:

```csharp
var invocation = BuildInvocation(rootPath, runId, taskId, taskDirectory, config, stage, input, ledger, manifest,
    testCommand: testCommandForCodingStage,
    pinnedSwivalProfileContent: pinnedSwivalProfileContent);
var result = await _dependencies.SubagentRunner.RunAsync(invocation, cancellationToken);
```

(search `"var testCommandForCodingStage ="` — `stage.Number is 6 or 8 ? targetedTestCommand : null`).

`BuildInvocation` lives in `src/VisualRelay.Core/Execution/RelayDriver.VerifyFix.cs`
(search `"private StageInvocation BuildInvocation("`). It passes `stage.Tier` straight through
as the invocation's `Tier`:

```csharp
return new StageInvocation(
    stage,
    stage.Tier,        // ← the tier that selects the model
    runId, ...
```

### How a stage's tier maps to an actual model

`src/VisualRelay.Core/Execution/RelayStages.cs` — the canonical stage table (search
`"public static IReadOnlyList<RelayStageDefinition> All"`). Relevant rows:

```csharp
Stage(4, "Plan",         "balanced", "some", "...", """{ "plan": string, "manifest": string[] }"""),
Stage(5, "Author-tests", "balanced", "all",  "all", """{ "testFiles": string[], "rationale": string }"""),
Stage(6, "Implement",    "balanced", "all",  "all", """{ "summary": string }"""),
Stage(7, "Review",       "frontier", "some", "all", """{ "verdict": "pass"|"changes", "issues": [] }"""),
```

The tier string is resolved to a swival profile in
`src/VisualRelay.Core/Execution/ProcessRunners.cs` (search
`"_config.TierProfiles.TryGetValue(invocation.Tier"`):

```csharp
var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
```

`TierProfiles` defaults (search `"[\"cheap\"] = \"cheap\""` in
`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`) map `cheap`/`balanced`/`frontier`
to like-named profiles. **The cheapest tier string is `"cheap"`.** A tier absent from the
map falls back to itself, so passing `"cheap"` is always safe.

### How to change a stage's tier (and prompt) at runtime — established precedent

`RelayStageDefinition` is a `record` (`src/VisualRelay.Domain/RelayStageDefinition.cs`,
search `"public sealed record RelayStageDefinition("`) with positional members
`Number, Name, Tier, Kind, Files, Commands, SystemPrompt, OutputContract`. `StageInvocation`
is also a `record` (`src/VisualRelay.Domain/StageInvocation.cs`).

Runtime tier mutation via `with` is ALREADY done in this codebase — the stall-retry
escalation path in `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs` does
(search `"currentInvocation with { Tier = nextTier }"`):

```csharp
currentInvocation = currentInvocation with { Tier = nextTier };
```

So down-shifting is exactly `invocation with { Tier = "cheap" }` — or, to also swap the
system prompt, build the invocation from a `stage with { Tier = "cheap", SystemPrompt = ... }`.

### The system prompt and contract come from the stage record

`src/VisualRelay.Core/Execution/ProcessRunners.cs` (search `"--system-prompt"`) passes
`invocation.Stage.SystemPrompt` as the swival `--system-prompt` argument. The prompt body
is assembled in `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs` `BuildPrompt`
(search `"internal static string BuildPrompt("`); it appends `invocation.Stage.OutputContract`
verbatim and includes the manifest and the prior-stage ledger. Therefore a down-shifted stage
must keep the SAME `OutputContract` (so the JSON contract still parses) but MAY carry a
different `SystemPrompt` (the "confirm/amend only" instruction). System prompts are defined in
`RelayStages.cs` `SystemPromptFor` (search `"private static string SystemPromptFor("`); the
existing Implement prompt begins `"Implement the change within the manifest files."`.

### The working-tree hash — what it hashes and when

`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs` (search
`"private static string WorkingTreeHash("`):

```csharp
private static string WorkingTreeHash(string rootPath, IReadOnlyList<string> manifest)
{
    var parts = new List<string>();
    foreach (var relative in manifest.Order(StringComparer.Ordinal))
    {
        var fullPath = Path.Combine(rootPath, relative);
        parts.Add(relative);
        parts.Add(File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty);
    }
    return Hashing.Sha256Hex(parts.ToArray());
}
```

It hashes the **current** on-disk content of the **manifest** files only. It is computed in
`RecordStageAsync` (`RelayDriver.Artifacts.cs`, search `"stage.Number >= 4 ? WorkingTreeHash"`)
ONLY for `stage.Number >= 4` — before stage 4 the manifest is empty so the tree hash is
`string.Empty`. It is also recomputed each fix-verify attempt in
`RelayDriver.VerifyFix.cs` (search `"var treeHash = WorkingTreeHash(rootPath, manifest);"`).

**Consequence (the crux of this task):** the manifest does not exist until stage 4 populates
it (see next section). A same-run baseline hash captured at stage 4 would ALREADY include any
edits front-loaded at stage 3, so an intra-run hash delta cannot reveal a stage-3 front-load.
The robust signal is therefore "do the manifest's implementation files already differ from
their committed (HEAD) content when we are about to run Implement?" — i.e. a diff against
HEAD, not against an intermediate in-run snapshot. See "What to build / detection."

### When the manifest exists, and the impl/test classifier

The manifest is populated in `RelayDriver.cs` at stage 4 (search `"if (stage.Number == 4)"`):
`manifest.Clear(); ... ReadStringArray(json, "manifest") ...`, task-dir entries dropped, then
`await WriteManifestAsync(...)`. So **manifest is available for stages 5–11**, never for 1–3.

`IsImpl(path)` (`RelayDriver.Artifacts.cs`, search `"private static bool IsImpl("`) classifies
a path as implementation code when its extension is NOT in `NonCodeExtensions`
(`.md .txt .json .yaml .yml .toml .csv`); extensionless paths are non-code. Stage 5's gate
already uses it (search `"manifest.Any(f => !testFiles.Contains"`). The set of "implementation
files in the manifest" is the natural unit for the front-load signal — authored TEST files are
expected to be written (by stage 5), so they must NOT count toward "implementation already
underway."

### Git invocation layer and the clean-tree-at-task-start guarantee

`src/VisualRelay.Core/Execution/GitInvoker.cs` (search
`"public static async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync("`) is the
pinned git process factory. `GitCommitter.cs` uses it for HEAD queries (search
`"[\"rev-parse\", \"--is-inside-work-tree\"]"` and `"[\"rev-parse\", \"HEAD\"]"`).
`RedGate.RestoreStashAsync` (search `"checkout\", \"--\", \".\""`) shows `git checkout -- .`
is the established tree-revert pattern.

The companion task `harness-reset-worktree-on-flag` resets the worktree to HEAD between
drained tasks, and a successful commit also leaves HEAD clean. **A drain run therefore starts
each task with a clean worktree (HEAD == task baseline).** The front-load detection relies on
this: "differs from HEAD" == "this task already wrote it." For non-git roots or a non-clean
start, the detection must degrade to OFF (never down-shift on an ambiguous baseline) — see
SAFETY.

### Existing tests of stage sequencing / tiering to mirror

- `tests/VisualRelay.Tests/ReviewTierGuardTests.cs` — guards `Review.Tier == "frontier"`
  (search `"ReviewStage_AlwaysRunsOnFrontierTier"`). Mirror this style for a new guard that
  the DEFAULT (un-down-shifted) Implement tier stays `"balanced"`.
- `tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs` — asserts on stage system-prompt
  text (search `"CodingStageSystemPrompt_ContainsFullGateProhibition"`). Mirror for the new
  confirm-prompt text.
- `tests/VisualRelay.Tests/RelayDriverTests.cs` — full-loop driver tests using
  `ScriptedSubagentRunner` (search `"RunTaskAsync_WritesLedgerSealsManifestAndStructuredEvents"`)
  and the existing front-load double `PrematureImplementationRunner` (search
  `"RunTaskAsync_StripsPrematureImplementationBeforeAuthorTestGate"`).
- Test doubles: `tests/VisualRelay.Tests/SubagentRunnerTestDoubles.cs`
  (`ScriptedSubagentRunner`, `CapturingSubagentRunner` — records every `StageInvocation` so a
  test can assert the down-shifted invocation's `Tier`) and
  `tests/VisualRelay.Tests/RelayDriverTestDoubles.cs` (`PrematureImplementationRunner` writes
  `src/status.cs` at stage 4 / a test file at stage 5). A new double that front-loads at
  **stage 3** is needed (see tests).

## What to build

Keep this MINIMAL and toolchain-agnostic. No assumptions about .NET, build systems, or test
frameworks — only "manifest file content vs HEAD" via git, and the existing tier/prompt
records.

### 0. Config flag (default ON, opt-out) — `RelayConfig`

Add one nullable/defaulted boolean to the `RelayConfig` record
(`src/VisualRelay.Domain/RelayConfig.cs`, append at the end of the parameter list to preserve
positional-construction compatibility, mirroring how `BoostTurnsTaskIds` was appended):

```csharp
// When true (default), if the agent front-loads implementation into an earlier
// stage (manifest impl files already differ from HEAD before Implement runs),
// the redundant Plan/Implement narration stages run on the cheapest tier with a
// "confirm/amend only" prompt instead of full freight. Set false to always run
// every stage on its declared tier. No effect on non-git roots or a dirty start.
bool DownshiftOnEarlyImplementation = true,
```

Wire it through `RelayConfigLoader` exactly like the existing optional bools (search
`"BaselineVerify"` in `RelayConfigLoader.cs` for the parse+default pattern; add a JSON key
`downshiftOnEarlyImplementation` defaulting to `true`). Mirror the loader test pattern in
`tests/VisualRelay.Tests/RelayConfigLoaderTests.cs`.

### 1. Detection helper — `EarlyImplementationDetector` (new file)

New file `src/VisualRelay.Core/Execution/EarlyImplementationDetector.cs`, an internal static
helper. Single responsibility: "are the manifest's implementation files already modified
relative to HEAD?" — robust, git-based, fail-safe to `false`.

```csharp
internal static class EarlyImplementationDetector
{
    /// <summary>
    /// Returns true when at least one IMPLEMENTATION file in the manifest already
    /// differs from its committed (HEAD) content — i.e. the agent front-loaded the
    /// change into an earlier stage. Returns FALSE (the safe default) when the root
    /// is not a git work tree, when HEAD is unavailable, when the manifest has no
    /// impl files, or on any git error. Test files (per IsImpl) are excluded.
    /// </summary>
    internal static async Task<bool> ImplementationAlreadyUnderwayAsync(
        string rootPath,
        IReadOnlyList<string> manifest,
        Func<string, bool> isImpl,
        CancellationToken cancellationToken)
    {
        var implFiles = manifest
            .Select(p => p.StartsWith('+') ? p[1..] : p)   // manifest may carry '+' new-file prefix
            .Where(isImpl)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (implFiles.Count == 0) return false;

        // Must be inside a git work tree, else we have no HEAD baseline → safe-off.
        var inside = await GitInvoker.RunAsync(rootPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0 || !inside.Output.Trim().StartsWith("true", StringComparison.Ordinal))
            return false;

        // `git diff --quiet HEAD -- <impl files>` exits 1 when any listed path differs
        // from HEAD (tracked-and-modified). New (untracked) files do not show here; they
        // are handled by the untracked check below. Exit 0 = clean, 1 = differs.
        var diff = await GitInvoker.RunAsync(
            rootPath, ["diff", "--quiet", "HEAD", "--", .. implFiles], cancellationToken);
        if (diff.ExitCode == 1) return true;
        if (diff.ExitCode != 0) return false; // any other code (e.g. no HEAD yet) → safe-off

        // An impl file the agent CREATED early is untracked, not "modified vs HEAD".
        // Detect new impl files that already exist on disk and are untracked.
        var untracked = await GitInvoker.RunAsync(
            rootPath, ["ls-files", "--others", "--exclude-standard", "--", .. implFiles], cancellationToken);
        if (untracked.ExitCode == 0 &&
            untracked.Output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Any())
            return true;

        return false;
    }
}
```

Notes:
- Use spread (`.. implFiles`) into the args list as elsewhere, OR `["diff","--quiet","HEAD","--"].Concat(implFiles)`
  — match the surrounding GitInvoker call style you find.
- This reuses the existing `IsImpl` semantics; pass `RelayDriver`'s `IsImpl` as the delegate
  so the classifier stays single-sourced (do NOT duplicate the extension allowlist). If
  `IsImpl` cannot be referenced cross-file cleanly, lift it to an `internal static` shared
  spot rather than copying it.
- Hard cap defensiveness: every git failure path returns `false`. A false negative merely
  reverts to today's behavior (full-freight stage); a false positive could under-power a
  stage that actually had work to do — so we bias hard toward `false`.

### 2. Use the signal to down-shift — in the stage loop (`RelayDriver.cs`)

In `RunTaskAsync`'s stage loop, compute the signal ONCE, immediately after the manifest is
finalized at stage 4 (after `TryPlanCompletenessRetryAsync` returns and the manifest is
written — search `"await WriteManifestAsync(taskDirectory, manifest, cancellationToken);"`
inside the `if (stage.Number == 4)` block, then the completeness-retry call right below it).
Store it in a method-local captured before the loop, e.g.:

```csharp
var implementationFrontLoaded = false;   // declared next to targetedTestCommand, before the loop
```

…and set it right after the stage-4 manifest is final:

```csharp
if (config.DownshiftOnEarlyImplementation)
{
    implementationFrontLoaded = await EarlyImplementationDetector
        .ImplementationAlreadyUnderwayAsync(rootPath, manifest, IsImpl, cancellationToken);
}
```

Then, where the per-stage invocation is built for the NON-driver branch, when the stage is the
**Implement** stage (`stage.Number == 6`) and `implementationFrontLoaded` is true, build the
invocation from a down-shifted stage definition instead of `stage`:

```csharp
var effectiveStage =
    implementationFrontLoaded && stage.Number == 6
        ? stage with { Tier = "cheap", SystemPrompt = ConfirmImplementationSystemPrompt }
        : stage;
```

and pass `effectiveStage` into `BuildInvocation(...)` in place of `stage` for that call only.
Everything else (cost tracking, `RecordStageAsync`, the contract parse, the `treeHash`) is
UNCHANGED — `RecordStageAsync` still receives the ORIGINAL `stage` so the ledger/seal record
the canonical stage identity (search `"RecordStageAsync(rootPath, runId, taskId, taskDirectory, stage, body, check, cost,"`
— pass the original `stage`, not `effectiveStage`, to recording).

`ConfirmImplementationSystemPrompt` is a new `const string` (place it in `RelayStages.cs`
beside `SystemPromptFor`, exported `internal`, so the prompt test can reference it):

```text
The implementation appears to already be in the working tree (an earlier stage wrote it).
Do NOT re-narrate or re-implement. Read the existing diff against the manifest, confirm it
matches the plan, and make ONLY small corrective amendments if something is missing or wrong.
Verify using ONLY the targeted test command shown in the ## Verify command section — do NOT
run the project's full check, lint, format, build, or screenshot gate; the harness runs the
full gate at the Verify stage.
```

(The closing two sentences mirror the existing Implement prompt so the
`CodingStageSystemPromptTests` invariants — "do NOT run", "## Verify command", "harness" —
still hold for the down-shifted variant if you choose to extend those theories to it.)

### 3. (Optional, same mechanism) down-shift Plan when the front-load predates the manifest

The observed waste also included **stage 4 Plan** re-narrating. Plan is the stage that
*produces* the manifest, so it cannot be skipped — but it CAN be detected one stage earlier
using a manifest-free signal and down-shifted. This is strictly optional; ship §1–§2 first.

If included: before stage 4 runs, compute a cheap "did stage 3 already dirty the tree?" signal
using `git diff --quiet HEAD --` with NO pathspec (whole tree, excluding the harness's own
`.relay/`, `.relay-scratch/`, `.swival/` and the tasks dir — reuse the prefix list documented
in `harness-reset-worktree-on-flag`). If dirty, build stage 4's invocation from
`stage with { Tier = "cheap", SystemPrompt = ConfirmPlanSystemPrompt }` where the confirm-plan
prompt says "an earlier stage already edited files; produce the plan + manifest by READING the
existing diff, do not redesign." Keep the SAME `OutputContract` (Plan must still emit
`manifest`). **Do NOT down-shift below the tier needed to produce a correct manifest if you
judge `cheap` too weak for manifest extraction — in that case leave Plan untouched and ship
only the Implement down-shift.** Document the decision in the PR.

### What must NOT change (hard constraints)

- **Stage 7 Review stays frontier, always.** Never down-shift, skip, or reprompt Review.
  `ReviewTierGuardTests` must stay green.
- **Stage 5 Author-tests is never down-shifted by this feature.** Front-loaded tests are
  handled by the EXISTING author-test red-gate (search
  `"author-tests passed after implementation files were stripped"` in `RelayDriver.cs`); this
  task must not touch that gate. (Stage 5 writing 0 new tests when tests were front-loaded is
  already covered by the red-gate's "already-resolved" branch — search
  `"Already-resolved\": no implementation delta to strip"`.)
- **Stages 8/10 (Fix / Fix-verify) are never down-shifted.** They run only when Review found
  issues or Verify went red — by definition there is real work to do.
- **No hard skip.** A down-shifted stage STILL RUNS (cheapest tier, confirm prompt) so it can
  catch a partial/incorrect front-load and amend it. Hard-skipping risks dropping real work;
  the confirm pass is the conservative floor.
- **Recording is unchanged.** `RecordStageAsync` receives the original `stage`; ledger
  headings, seals, `treeHash`, `artifactHash`, status entries, and `stage_done` events are
  byte-for-byte what they are today (a down-shift changes only which model ran + the system
  prompt, not the contract or the recorded identity).
- **The contract is unchanged.** `effectiveStage.OutputContract == stage.OutputContract`. The
  parse path (`TryParseContractJson`, `ReadStringArray`) is untouched.

### How false positives are avoided (SAFETY posture)

- **Bias to OFF.** The detector returns `false` on: non-git root, missing HEAD, any git error,
  empty impl set, and (by construction) test-only manifests. The worst a false negative does is
  pay today's cost.
- **Down-shift, not skip.** Even when the signal fires, the stage runs on `cheap` and is told
  to amend gaps — so a wrong/partial front-load is still finished, just cheaply. The
  subsequent **frontier Review (stage 7)** and the **Verify gate (stage 9)** remain the
  correctness backstops; if the cheap confirm pass under-delivers, Review/Verify catch it and
  the normal Fix / Fix-verify loops engage at full tier.
- **Clean-start dependency is explicit.** The signal means "differs from HEAD." In a drain,
  `harness-reset-worktree-on-flag` + successful-commit cleanup guarantee HEAD == task baseline.
  For a single-task run against a pre-dirtied tree, the detector may see pre-existing edits and
  down-shift Implement — acceptable because (a) it still runs the confirm pass and (b) Review +
  Verify gate the result. If this is judged too aggressive for ad-hoc single-task runs, gate
  the whole feature behind "tree was clean at task start" by capturing a `git status
  --porcelain` emptiness check at run start and AND-ing it into `implementationFrontLoaded`;
  prefer this only if a test surfaces a real false-positive.
- **Config kill-switch.** `downshiftOnEarlyImplementation:false` restores today's behavior
  exactly.

## Tests (TDD — write these first)

All under `tests/VisualRelay.Tests/`. Write and run them RED before any source change.

### `EarlyImplementationDetectorTests.cs` (unit, real temp git repo)

Use `TestRepository.Create()` (see existing usages, e.g. `RelayDriverTests.cs`) and the
`TestGit` helper (search `"TestGit.Run(repo.Root"`). Pass `RelayDriver`-equivalent `IsImpl`
(or the shared classifier) as the delegate.

- `ReturnsTrue_WhenTrackedImplFileModifiedVsHead`: commit `src/x.cs`, modify it on disk,
  manifest = `["src/x.cs"]` → `true`.
- `ReturnsTrue_WhenNewImplFileUntracked`: manifest = `["+src/new.cs"]`, write `src/new.cs`
  but do NOT commit → `true` (untracked-impl branch).
- `ReturnsFalse_WhenImplFilesCleanVsHead`: commit `src/x.cs`, leave it unchanged → `false`.
- `ReturnsFalse_WhenOnlyTestFilesModified`: manifest = `["tests/x.tests.cs"]` modified vs HEAD
  → `false` (test files excluded by `IsImpl`).
- `ReturnsFalse_WhenManifestHasNoImplFiles`: manifest = `["docs/README.md"]` → `false`.
- `ReturnsFalse_OnNonGitRoot`: plain temp dir, manifest with a modified file → `false`
  (no throw).
- `ReturnsFalse_OnEmptyManifest`: `[]` → `false`.

### `RelayDriverEarlyImplementationTests.cs` (driver-loop integration)

Mirror `RelayDriverTests.cs` setup (real git repo via `InitGitRepo(repo.Root)` — search that
helper). Add a new front-load double in `RelayDriverTestDoubles.cs` (or inline):
`Stage3FrontLoadRunner` that, at `invocation.Stage.Number == 3`, writes the manifest's impl
file(s) to disk (e.g. `src/status.cs`), then returns the same canned JSON as
`PrematureImplementationRunner` for every stage. Use `CapturingSubagentRunner`-style recording
(it records each `StageInvocation`) so the test can read the Implement invocation's `Tier`.

- `Implement_DownshiftedToCheap_WhenStage3FrontLoaded`: with the front-load double + a clean
  committed `src/status.cs`, run to completion. Assert the stage-6 invocation's `Tier ==
  "cheap"` AND its `Stage.SystemPrompt` contains "do NOT re-narrate" (or the confirm-prompt
  marker). Assert the run still reaches `Committed` and that the **stage-7 (Review)** invocation
  `Tier == "frontier"` (down-shift did not leak).
- `Implement_StaysBalanced_WhenNoFrontLoad`: with the plain `ScriptedSubagentRunner`
  (no early edits), assert the stage-6 invocation `Tier == "balanced"` (unchanged).
- `Downshift_Disabled_KeepsBalanced`: same front-load double but
  `downshiftOnEarlyImplementation:false` in config → stage-6 `Tier == "balanced"`.
- `Recording_Unchanged_WhenDownshifted`: after a down-shifted run, the seals/ledger for stage 6
  are recorded under the canonical identity (a `"n":6` seal exists; the ledger heading is
  `## Stage 6 - Implement`) — i.e. `RecordStageAsync` saw the original stage.

### Guard tests (mirror `ReviewTierGuardTests` / `CodingStageSystemPromptTests`)

- `DefaultImplementTierIsBalanced`: `RelayStages.All.Single(s => s.Name == "Implement").Tier ==
  "balanced"` (the down-shift is a runtime override, never a table change).
- `ConfirmImplementationPrompt_ProhibitsReNarrationAndFullGate`: the new confirm-prompt const
  contains "do NOT re-narrate" and "do NOT run" (full-gate prohibition) and "## Verify command".

## Done when

- **Failing tests written first**, then made green.
- `EarlyImplementationDetector` returns `true` only for modified/untracked IMPL manifest files
  vs HEAD; returns `false` on non-git roots, missing HEAD, test-only manifests, empty manifests,
  and any git error (no throw on any path).
- When the agent front-loads implementation before Implement and
  `downshiftOnEarlyImplementation` is on, **stage 6 runs on the `cheap` tier with the
  confirm/amend prompt**; otherwise stage 6 runs on `balanced` exactly as today.
- **Stage 7 Review still runs on `frontier`** in every scenario; `ReviewTierGuardTests` green.
- **Stages 5, 8, 10 are never down-shifted** by this feature; the stage-5 author-test red-gate
  is untouched.
- **The down-shift changes only the model + system prompt for that one invocation.** Recorded
  ledger/seals/`treeHash`/`stage_done` for the stage are identical to a non-down-shifted run of
  the same stage (verified by `Recording_Unchanged_WhenDownshifted`).
- The `OutputContract` of the down-shifted stage is byte-identical to the canonical stage; the
  contract parse path is unchanged.
- `downshiftOnEarlyImplementation:false` restores today's behavior exactly (guard test).
- No single source file exceeds 300 lines; `EarlyImplementationDetector.cs` is under ~80 lines;
  the `RelayDriver.cs` change is a small local flag + one `effectiveStage` expression (under
  ~15 added lines).
- **`IsImpl` is single-sourced** — not copied into the detector.
- **`./visual-relay check` is green** — all pre-existing tests pass unmodified (especially the
  full-loop driver tests, the plan-only tests, and `ReviewTierGuardTests`).
- **Smoke after merge:** because this touches the live stage loop, run `run-task` end-to-end on
  a tiny task (the pipeline mocks the process layer, so a passing `verify` does not prove the
  real loop) before relying on it in a batch drain.
- **Relationship to adjacent work:**
  - `harness-reset-worktree-on-flag`: provides the clean-tree-at-task-start guarantee this
    detector's "differs from HEAD" semantics depend on. This task is safe to land after it;
    if landed before, the single-task false-positive note in SAFETY applies.
  - `ReviewTierGuardTests` / the reverted review-tier-escalation experiment: this feature only
    ever LOWERS the Implement tier at runtime and never touches Review — it does not reopen
    that decision.
- **Conventional Commit** subject candidates:
  - `feat(driver): down-shift Implement to cheap confirm pass when work is front-loaded`
  - `feat(harness): detect early implementation and skip redundant narration cost`
  - `perf(relay): avoid paying frontier/balanced freight for already-done stages`
