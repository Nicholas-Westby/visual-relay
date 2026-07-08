# Add a Parallel "Visual Review" Stage on the Vision Tier

The Review stage (stage 7, `frontier` tier) reads the diff — it cannot see rendered UI. Two runs
have now shipped or nearly shipped visual defects past a green review: a card-styles task passed
review while square-corner artifacts remained visible in the running app, and the button
composition refactor's review returned `{"verdict":"pass","issues":[]}` while eleven UI test
files were broken by the change (caught only later). Add a **Visual review** stage that runs on
the vision tier, **in parallel with** the text Review stage, whose findings are combined with
Review's and fed to the Fix stage.

## Current state (researched)

- **Stage table** — `src/VisualRelay.Core/Execution/RelayStages.cs`: `RelayStages.All` is the
  ordered list of `RelayStageDefinition(number, name, tier, kind, files, commands, systemPrompt,
  contract)` records. Today: 1 Ideate, 2 Research, 3 Diagnose, 4 Plan, 5 Author-tests,
  6 Implement, 7 `Stage(7, "Review", "frontier", "some", "all", …verdict/issues contract…)`,
  8 Fix, 9 Verify, 10 Fix-verify, 11 Commit (kind `"driver"`). Stage prompts live in
  `SystemPromptFor(name)` in the same file; the Fix prompt starts "Resolve every blocker and
  warning from review."
- **How stage output reaches later stages** — each stage's JSON answer is appended to
  `.relay/<taskId>/ledger.md` as a `## Stage N - <Name>` section, and later invocations receive
  it via `StageInvocation.LedgerSoFar` (input assembly:
  `src/VisualRelay.Core/Execution/ProcessRunners.StageInput.cs`, 64 lines). "Combined output to
  Fix" therefore means: both review stages' sections are in the ledger before Fix runs.
- **The driver is strictly sequential** — `src/VisualRelay.Core/Execution/RelayDriver.cs` runs
  stages in order, one `SubagentRunner.RunAsync(StageInvocation…)` at a time, with per-stage
  artifacts `stageN-attemptM.input.json` / `.report.json` / trace dir, per-stage watchdog and
  ceiling (`ProcessRunners.RunAsync.cs` / `ProcessRunners.Watchdog.cs`), and `run.log` events
  tagged `sN/<tier>`. **`RelayDriver.cs` is at 298/300 lines (file-size guard) — all new
  orchestration must go in a new partial file** (e.g. `RelayDriver.ReviewPair.cs`).
- **Where vision lives today — and why it has plausibly NEVER worked.** *No* stage runs on the
  vision tier. The vision model is wired as a swival profile (`SwivalProfileSession.DefaultToml`
  → `[profiles.vision]`, `model = "vision"`, resolved through `tierProfiles` in
  `.relay/config.json`; pricing exists in `RelayPricing`). The mechanism for actually seeing an
  image is swival's built-in **`view_image` tool**. Trace evidence from run
  `.relay/improve-task-card-styles/` shows every observed attempt failed:
  - stage 1 (`files "none"`): `view_image` on the task's attached PNG → `error: … resolves to
    BASEDIR/llm-tasks/…png, which is outside .swival/ (filesystem access is disabled)` — the
    stage's file scope blocks it;
  - stage 2 (`files "some"`): `view_image` aimed at a stale worktree path from the ledger →
    `file not found`; the agent then dumped raw PNG bytes via `git cat-file` (37 KB of binary
    into a cmd-output file) and "described" the image from that — i.e. confabulation, not
    vision.
  So the failure causes are (a) stage file scopes blocking image reads and (b) agents fumbling
  paths — not a missing model. This task must make `view_image` actually work for the new stage:
  correct file scope, PNGs placed where the scope allows, and **exact** image paths handed to
  the agent in its input. VR's own input assembly attaches no images
  (`ProcessRunners.StageInput.cs` has no image handling) — the paths-in-prompt + `view_image`
  route is the mechanism.
- **Rendering machinery exists in this repo** — `tools/VisualRelay.Screenshots/Program.cs`
  builds a real `MainWindowViewModel` with demo tasks (including a running+selected card via
  `RestoreRunningTaskState(...)` + `SelectedTask = task`) and renders window PNGs; it already
  runs as part of
  `./visual-relay check`. It is this repo's natural `visualRenderCmd` implementation — but VR
  is general-purpose, so the pipeline must only ever invoke rendering through the config key,
  never through a hardcoded tool path.
- **Stage-number dependents (renumber checklist)** — inserting a stage renumbers everything
  after 7. Known dependents to sweep: the flagged-work resume range
  (`RelayDriver.FlaggedWork.cs`: `firstStageToRun is < 5 or > 10`); Verify / Fix-verify
  mechanics and comments keyed to stages 9/10 (`RelayDriver.VerifyFix.cs`,
  `RelayDriver.VerifyObservability.cs`); the Commit driver stage number; the stage board and
  `MainWindowViewModel.Stages` consumers, including index-based demo seeding in
  `tools/VisualRelay.Screenshots/Program.cs` (`viewModel.Stages[1]`, `Stages[2]`, …); any tests
  that assert stage numbers, names, or counts; `docs/` and `README` mentions of the pipeline.
  Ledger and seal entries record `n` dynamically and need no format change.

## What to build

1. **Stage definition.** Insert
   `Stage(8, "Visual-review", "vision", "some", "git,ls,cat", <same contract as Review>)` into
   `RelayStages.All` and renumber: Fix→9, Verify→10, Fix-verify→11, Commit→12. Sweep the
   renumber checklist above; extend the flagged-work resume range to `5..11`.
2. **System prompt** (new arm in `SystemPromptFor`): review the *rendered UI*, not the code —
   e.g.: "You are reviewing rendered screenshots of the application built from the current
   working tree, plus the task's own attached images. Read the PNG files listed in your input to
   view them. Identify concrete visual defects relevant to the task: geometry, corner radii,
   clipping, overlap, alignment, spacing, color/contrast, missing or wrong states. **If the
   task's changes are not visual, or the renders show nothing wrong relevant to the task's
   intent, return `{"verdict":"pass","issues":[]}` immediately — a fast clean exit is the
   expected common case; never manufacture findings.** Do not review code style or correctness —
   the parallel text Review covers that. Do not edit files." Output contract identical to Review
   (`{ "verdict": "pass"|"changes", "issues": [] }`) so Fix consumes both uniformly.
   **Contingency:** if the vision-tier model proves unable to follow the JSON contract or the
   review instructions reliably (smaller VLMs sometimes can't), chain instead: the vision model
   only describes each image (plain prose), and a `cheap`-tier pass converts descriptions +
   task intent into the contract JSON. Prefer the single-model form; implement the chain only if
   the smoke test shows it is needed.
3. **Driver: pre-render, then run the pair concurrently** (new partial
   `RelayDriver.ReviewPair.cs`):
   - **Triage decides, not file extensions.** Visual Relay is language- and framework-agnostic,
     so the driver must NOT gate on file patterns (no `.axaml`/`Views/`-style heuristics —
     those assume one stack). Stage 8 runs in two parts:
     **(a) Visual-triage** — a small cheap-tier text invocation launched at the same time as
     Review: input is the task description, the working-tree diff summary (changed paths +
     stats, plus small hunks where cheap), and the list of task image attachments; output
     contract `{ "visualReview": "needed"|"skip", "reason": string }`; read-only, hard-capped
     at a modest turn budget (e.g. `MaxTurns: 12` — the input already carries the diff summary,
     and twelve turns leaves room to open several files for context; observed real stages run
     ~1 turn for Verify up to ~20–37 for Research/Ideate, so 12 keeps triage decisively smaller
     than a reasoning stage without starving it), with its own artifacts (e.g.
     `stage8-triage-attempt1.{input,report}.json`). Its prompt asks a capability question, not
     a framework question: "would a reviewer benefit from LOOKING at rendered output for this
     change — UI markup/styles/layout in any framework, web frontends, terminal UI, images or
     other visual assets, charts, generated documents?" When genuinely uncertain, prefer
     `needed` (a vision pass costs cents; a missed visual defect costs a run).
     **(b) Visual-review proper** — launched only when triage says `needed`. On `skip`: ledger
     note carrying the triage reason, stage 8 recorded as skipped, Review alone feeds Fix.
   - **Rendering is repo-configured, not hardcoded.** Add an optional `.relay/config.json` key
     **`visualRenderCmd`** (precedent: `testCmd`/`testFileCmd`/`formatCmd`; parse via the
     existing optional-string pattern in `RelayConfigLoader.cs`, new `RelayConfig` field): a
     shell command that renders the project's current visual state as PNGs into the directory
     VR substitutes for an `{outDir}` token (token precedent: `{files}` in `testFileCmd`). When
     triage says `needed` and the key is set, the driver runs it — executed under the same
     sandbox/timeout regime as the other configured commands — with `{outDir}` →
     `.relay/<taskId>/visual-review/`. Key unset → Visual-review runs on the task's attached
     images only, and its input states that fresh renders are unavailable. A render failure is
     not a skip — feed the failure output to the stage; a tree whose UI cannot render is itself
     a finding. For THIS repo, set `visualRenderCmd` to a `tools/VisualRelay.Screenshots`
     invocation that writes into `{outDir}` (verify and, if needed, extend that tool's CLI to
     accept an output directory).
   - **Exact paths into the stage input:** list the repo-relative paths of the rendered PNGs
     plus task-folder image attachments, with an explicit instruction to open each via
     `view_image` — the observed failures were bad paths and blocked scopes, so leave the agent
     nothing to guess. Confirm the stage's `files` scope permits those locations (`"none"`
     hard-blocks `view_image`; pick the scope/placement the smoke test proves works).
   - **Parallel pair:** launch stage 7 immediately and triage concurrently; when triage returns
     `needed`, render and launch stage 8 as a second concurrent `SubagentRunner.RunAsync`
     invocation (`Task.WhenAll` over whatever is in flight), each with its own report file,
     trace dir, watchdog, and ceiling exactly as any stage has today. Verify interleaved
     `run.log` events remain coherent (they are already tagged `s7/…` and `s8/…`).
   - **Mechanical skip stays minimal:** the only driver-level hard skip is the `vision` tier
     being unconfigured/unreachable (mirror however the backend reports a missing tier key —
     the Settings dialog already models "key missing"); everything else is triage's call.
   - **Join:** await both; append both outputs to the ledger in fixed order (Review, then
     Visual-review) before Fix. If one of the pair fails or times out, let the sibling finish
     (its output is still useful), then apply the existing per-stage failure handling to the
     failed one.
4. **Fix consumes both.** Update the Fix prompt's first sentence to "Resolve every blocker and
   warning from review and visual review." No other Fix changes.
5. **Tests** (existing patterns: plain xUnit + fakes; `TestRepository`):
   - stage table: count 12, order, tiers, kinds, contracts — pins the renumbering;
   - pair orchestration with a fake `SubagentRunner`: Review + triage launched concurrently
     (assert overlap via recorded start/finish), triage `needed` → render then Visual-review
     launched, triage `skip` → no vision invocation + ledger note with the reason, ledger
     receives both review sections in fixed order, render-failure-as-finding path,
     `visualRenderCmd` parsing + `{outDir}` substitution + unset-key attachment-only path,
     vision-unconfigured mechanical skip, sibling-survives-failure path;
   - flagged-work range and Verify/Fix-verify renumber regressions;
   - prompt content assertions consistent with however existing stage-prompt text is tested.
6. **Smoke the vision path FIRST — before building anything else.** No successful `view_image`
   call exists in any recorded trace; the entire feature rests on this working. Run a minimal
   Visual-review-shaped swival invocation (vision profile, `files "some"`, a known PNG at an
   exact given path) and confirm the model returns a description that proves it saw the pixels
   (e.g. ask it to name a distinctive element). If `view_image` cannot deliver image content to
   the model on the pinned swival, stop and report a blocker — do not ship a stage that cannot
   see, and do not fall back to describing byte dumps.

## Gotchas to design for (anticipated — address each explicitly)

- **`view_image` scope/path failures** — the two recorded failure modes (blocked file scope,
  guessed-wrong path). Addressed by: smoke test first; exact paths in the stage input; scope
  verified; renders placed where the sandbox allows.
- **Triage economics and bias** — triage must stay tiny (cheap tier, hard `MaxTurns` cap, diff
  *summary* rather than full diff for large changes) so it never bottlenecks the pair; bias
  toward `needed` under uncertainty and toward `skip` only for clearly non-visual changes (pure
  backend logic, docs, config). Triage output is advisory routing only — it must never reach
  Fix looking like review findings.
- **`visualRenderCmd` is untrusted repo config** — substitute `{outDir}` literally, run the
  command under the same sandbox/timeout regime as `testCmd`-family commands, and treat a
  non-zero exit as reviewable content (a finding), not a driver failure.
- **Two stages active at once in the UI** — the stage board, queue card ("Stage 08 · Fix"),
  status text, `RestoreRunningTaskState`, and stage-selection/"follow running" logic may all
  assume a single running stage. Audit `MainWindowViewModel` stage-state plumbing and render
  both cards as running (the stage board already shows per-stage state; the single-stage
  assumptions are the risk). Add a UI test for the dual-running presentation.
- **Combining two outputs into one downstream input** — both outputs enter the ledger as
  ordinary `## Stage N` sections in a fixed order (Review first), so Fix's input needs no new
  format. Contradictions between the two reviews are acceptable — Fix reads both; do not build
  a merge/dedup layer.
- **run.log and seal interleaving** — events are already tagged `sN/<tier>`, but the seal
  ledger appends stage entries in completion order; two concurrent completions must not
  interleave a single entry's write. Serialize seal/ledger appends behind the join.
- **Retry/stall/timeout semantics per pair member** — stall retries and ceiling kills apply to
  the failed member only; the sibling's completed result must not be discarded or re-run.
  Flagged-work snapshots remain safe because Visual-review is read-only (the tree only has one
  writer at a time in the pair — Review is also read-only; neither edits).
- **Resume mid-pair** — if the run dies during the pair, resume re-enters at stage 7 and
  re-runs BOTH members (simplest correct rule); never resume into stage 8 alone with a stale
  stage-7 result unless its report exists and parsed.
- **Backend concurrency and cost** — two simultaneous model streams through the litellm backend
  (different models); confirm the backend handles parallel requests (it serves the app +
  agents already) and that per-stage cost attribution stays separate (each invocation already
  reports its own stats).
- **MSBuild contention** — the pre-render build completes before the pair launches, and both
  reviewers are read-only (targeted test runs allowed to Review only), so concurrent-build
  clashes are avoided by construction; keep it that way (do not give Visual-review build/test
  commands).

## Done when

- The pipeline is: 1 Ideate … 7 Review ∥ 8 Visual-review (concurrent) → 9 Fix → 10 Verify →
  11 Fix-verify → 12 Commit, with stages 7 and 8 demonstrably overlapping in a driver test.
- A task triaged `needed` produces a `stage8-attempt1.report.json` whose verdict/issues came
  from actually reading images (fresh `visualRenderCmd` renders when the repo configures one,
  task attachments otherwise); its findings appear in the ledger next to Review's, and Fix's
  input contains both.
- A task triaged `skip` records the triage reason in the ledger with no vision invocation;
  setups without a vision tier skip mechanically; nothing anywhere gates on file extensions or
  framework-specific paths.
- All renumber dependents updated (resume range, Verify/Fix-verify, UI stage board, screenshots
  seeding, tests, docs); `./visual-relay check` passes.

## Guardrails

- `RelayDriver.cs` is at 298/300 — orchestration goes in a new partial; keep every touched file
  under the guard.
- Parallelism is scoped to exactly this pair; no other stages run concurrently, and Commit stays
  the last, driver-kind stage.
- Each pair member keeps the standard independent watchdog/ceiling; do not invent a combined
  budget.
- The Visual-review stage is read-only (`files "some"`, minimal commands) — it must never edit
  the tree; findings flow to Fix.
- Do not gate Fix on the verdicts (Fix already runs unconditionally today); this task only adds
  the second reviewer and merges outputs.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs.
