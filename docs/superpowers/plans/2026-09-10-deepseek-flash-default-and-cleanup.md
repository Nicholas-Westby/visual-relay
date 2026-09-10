# DeepSeek V4.1 Flash Default, Authorship Tool Removal, Control API Task Creation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make DeepSeek V4.1 Flash the head of the `cheap` and `balanced` tiers with the current models kept as ordered fallbacks, delete the `me.sh` authorship-claim tool and its engine, and give the control API a `create-task` command so tasks can be authored headlessly.

**Architecture:** Three independent, small changes on `main`. The model catalog is a set of parallel tables keyed by alias (route, price, tier chain, selectable list, request golden) guarded by parity tests, so a new model is added to every table in one commit. The authorship tool is a standalone CLI project plus one Core namespace with no other consumers. The control API dispatches property actions to view-model commands; `create-task` reuses the GUI's own creation command.

**Tech Stack:** C# / .NET 10, xunit v3, Avalonia (headless tests), Kestrel control server.

**Spec:** User request of 2026-09-10 (this session). Research notes with exact file:line lists: `/private/tmp/claude-501/-Users-admin-Dev-visual-relay/b4a8a6c0-864a-40c1-a57e-c9b1c0ef494f/scratchpad/notes/10-codebase.md` (sections C, D, F) and `30-deepseek.md`.

## Global Constraints

- Work is committed directly on `main`, one focused commit per concern, Conventional Commit subjects (`type(scope): summary`), subject at most 72 characters, lowercase after the prefix, no trailing period, no em dash anywhere, body of at most three `- ` bullets of at most 20 words each. Human commits must not name changed files (basenames or path-like `a/b` tokens) in the subject or bullets. The commit-msg hook enforces this; if it rejects, fix the message.
- The pre-commit hook bumps `VERSION` and stages it on every commit; that is expected. Stage only your own files (`git add <paths>`), never `git add -A`.
- Every `*.cs` and `*.axaml` file under `src/`, `tests/`, `tools/` stays at or under 300 lines.
- `./visual-relay test <ClassName>` runs one class; `./visual-relay test` runs the suite. `./visual-relay check` (guards, format, build, InspectCode at 0 findings, tests, screenshots) must exit 0 before the last commit of a task; it rewrites `docs/images/*.png`, so run `git checkout -- docs/images` afterwards.
- Tests never use real git (inject `GitSim`), real sleeps or wall-clock waits, `.Result`/`.Wait()`, or `new HttpClient` outside the existing seam; guards enforce this.
- `tests/VisualRelay.Tests/SplitGuardVerificationTests.cs` pins a `[Fact]` count baseline of 142 across a prefix list that includes `ModelCatalogTests.`, `ModelCatalogSelectableTests`, `ModelCatalogPerModelTimeoutTests`, `ModelCatalogVisionTierTests`, `ModelCatalogAliasConsistencyTests`. Edit assertions in those files; put any new `[Fact]` in a class outside the list.
- `llm-tasks/completed/**` are historical records: never edit them.
- Do not run the desktop app while working; it is not needed for these tasks.

---

### Task 1: Remove the authorship claim tool

**Files:**
- Delete: `me.sh`, `tools/VisualRelay.ClaimAuthorship/Program.cs`, `tools/VisualRelay.ClaimAuthorship/VisualRelay.ClaimAuthorship.csproj` (then remove the whole `tools/VisualRelay.ClaimAuthorship/` directory including untracked `bin/` and `obj/`), `src/VisualRelay.Core/Authorship/AuthorshipClaimer.cs`, `src/VisualRelay.Core/Authorship/AuthorshipClaimer.Replay.cs`, `src/VisualRelay.Core/Authorship/ClaimOutcome.cs`, `src/VisualRelay.Core/Authorship/ClaudeTrailerStripper.cs`, `tests/VisualRelay.Tests/AuthorshipClaimerTests.cs`, `tests/VisualRelay.Tests/ClaudeTrailerStripperTests.cs`.
- Modify: `VisualRelay.slnx` (remove the `<Project Path="tools/VisualRelay.ClaimAuthorship/VisualRelay.ClaimAuthorship.csproj" />` line), `tests/VisualRelay.Tests/ScratchRepo.cs` (the doc comment references `<see cref="AuthorshipClaimerTests"/>`; reword it to name the remaining users, `HistoryRewriterTests` and `CommitLintRunnerTests`, and rename the base directory string `"claim-authorship-tests"` to `"scratch-repos"`), `src/VisualRelay.Core/CommitLint/HistoryRewriter.cs` (doc comment mentions "the authorship-claim engine"; reword so it no longer refers to deleted code).

**Interfaces:**
- Consumes: nothing.
- Produces: nothing; the remaining `ScratchRepo` helper keeps its API.

- [ ] **Step 1: Confirm the consumer set is closed**

Run: `grep -rn "AuthorshipClaimer\|ClaudeTrailerStripper\|ClaimOutcome\|ClaimAuthorship\|me\.sh" --include="*.cs" --include="*.csproj" --include="*.slnx" --include="*.md" --include="*.sh" --include="*.rb" --include="*.ps1" . | grep -v "llm-tasks/completed" | grep -v "/bin/\|/obj/"`
Expected: only the files listed above (plus `ScratchRepo.cs` and `HistoryRewriter.cs` comments).

- [ ] **Step 2: Delete and edit**

`git rm` the nine tracked files, `rm -rf tools/VisualRelay.ClaimAuthorship`, apply the three edits.

- [ ] **Step 3: Build and run the affected tests**

Run: `./visual-relay build` then `./visual-relay test HistoryRewriterTests` and `./visual-relay test CommitLintRunnerTests`.
Expected: build succeeds (the solution no longer lists the project); both classes pass.

- [ ] **Step 4: Commit**

```bash
git add -A -- me.sh tools/VisualRelay.ClaimAuthorship src/VisualRelay.Core/Authorship tests/VisualRelay.Tests/AuthorshipClaimerTests.cs tests/VisualRelay.Tests/ClaudeTrailerStripperTests.cs VisualRelay.slnx tests/VisualRelay.Tests/ScratchRepo.cs src/VisualRelay.Core/CommitLint/HistoryRewriter.cs
git commit -m "chore: remove the authorship claim tool

- The wrapper script, its CLI project and the trailer-stripping engine had no other consumers
- Remaining scratch-repo tests keep the shared helper under a neutral directory name"
```

---

### Task 2: Lead the cheap and balanced tiers with DeepSeek V4.1 Flash

**Files:**
- Modify: `src/VisualRelay.Core/Llm/Routing/ProviderRoutes.cs` (add route), `src/VisualRelay.Core/Costs/RelayPricing.cs` (add row, re-price legacy rows), `src/VisualRelay.Core/Configuration/ModelCatalog.cs` (chains + comment), `src/VisualRelay.Core/Configuration/ModelCatalog.Selectable.cs` (selectable lists), `.env.example`, `docs/OPERATIONS.md` (alias list), `.relay/config.json` (this repo's own `tierModelOverrides`), and the tests listed in `30-deepseek.md` under "Must change".
- Create: `tests/VisualRelay.Tests/Goldens/request/deepseek-flash/{ideate,plan,plan-required-tools,ideate-no-reasoning}.json` (generated), `tests/VisualRelay.Tests/ModelCatalogDeepSeekFlashTests.cs` (new facts live here, outside the fact-count baseline).

**Interfaces:**
- Consumes: `ProviderRoute(Alias, ProviderName, Endpoint, UpstreamModel, ApiKeyEnvVar, ContextWindow, Timeouts)`; `ModelPricing(Input, Output, CachedInput, CacheWrite) { Windows }`; `ModelCatalog.Chains` entries `(Model, RequiredKey)`.
- Produces: alias `deepseek-flash` routable, priced, at the head of `cheap` and `balanced`, selectable in both, goldened.

**Decisions (binding):**
- Alias and upstream id: `deepseek-flash` (the official API id; there is no `deepseek-v4.1-flash`). Provider `"DeepSeek"`, endpoint the existing DeepSeek endpoint, key `DEEPSEEK_API_KEY`, context window `1_000_000` (the provider serves 1M; the catalog records real windows, GLM is recorded as 1M), timeouts `Fast`.
- Pricing row: `new(0.15, 0.60, 0.003, 0.15) { Windows = DeepseekPeakWindows }` (USD per 1M tokens, off-peak base; cache miss 0.15, output 0.60, cache hit 0.003, cache write equals input as for the other DeepSeek rows). Source: https://api-docs.deepseek.com/quick_start/pricing on 2026-09-10; peak windows unchanged (01:00-04:00 and 06:00-10:00 UTC weekdays at 2x, already encoded as Asia/Shanghai windows).
- Legacy rows `deepseek-v4-flash`, `deepseek-v4-flash-vision-exp`, `deepseek-v4-pro` are re-priced to the same V4.1 Flash figures, each with a dated comment: the two Flash names were retired on 2026-09-10 and are routed to V4.1 Flash; `deepseek-v4-pro` is routed to V4.1 Flash and billed at its price from 2026-09-14 04:00 UTC (until then it is under-counted by this row, which is acceptable for a fallback that is rarely reached).
- Chains: `cheap` = `deepseek-flash`, `deepseek-v4-flash-vision-exp`, `deepseek-v4-flash`, `deepseek-v4-pro`, `fallback`; `balanced` = `deepseek-flash`, `deepseek-v4-pro`, `kimi-k2`, `deepseek-v4-flash`, `fallback`. Frontier and vision chains unchanged. Rewrite the comment above the chains so it explains the new head and that the legacy names are now aliases of the same model.
- Selectable lists: prepend `deepseek-flash` to `cheap` and `balanced` (five entries each, under the cap of six); keep every existing name so saved overrides survive.
- `ProviderCapabilities` needs no change. Leave `ReasoningEffortValues` as they are.
- `.relay/config.json` (tracked): remove the `"balanced": "deepseek-v4-pro"` override entry (it pinned the tier to a model that will be served by V4.1 Flash anyway); keep the `vision` override.
- `.env.example`: reword the DeepSeek comment to say it backs the `cheap` and `balanced` tiers with DeepSeek V4.1 Flash, the V4 names stay as fallbacks, and images still go to the `vision` tier.
- `docs/OPERATIONS.md` alias list: add `deepseek-flash`.
- Tests: `tests/VisualRelay.Tests/FirstPartySubagentRunnerTests.cs` `ChainHops_ReachDifferentProviders` scripts 2 attempts x 3 DeepSeek models = 6 failures before the HF floor; with four DeepSeek models in the cheap chain it must script 8 (update the loop and its comment). Cost figures pinned on the cheap and balanced heads move to the new rates; recompute from first principles (`RelayCostEstimator` formula) rather than copying, then compare with the figures in `30-deepseek.md` (cheap fixture 0.00039868 becomes 0.0002871, peak 0.0005742; balanced 0.00143 becomes 0.000351, peak 0.000702; and the others listed there). `RelayCostEstimatorMeasuredTests` compares a served `deepseek-v4-pro` against `deepseek-v4-flash` as "dearer"; after the re-price they are equal, so switch that test's dearer model to `kimi-k2`.

- [ ] **Step 1: Write the failing tests**

In the new class `ModelCatalogDeepSeekFlashTests` add facts asserting: `ProviderRoutes.For("deepseek-flash")` has upstream `deepseek-flash`, key `DEEPSEEK_API_KEY`, context window 1_000_000; `RelayPricing.Default["deepseek-flash"]` equals (0.15, 0.60, 0.003, 0.15) with the DeepSeek windows; `ModelCatalog.Chains["cheap"][0]` and `["balanced"][0]` are `deepseek-flash`; the previous heads follow in the stated order; `SelectableModelsByTier` lists `deepseek-flash` first for both tiers; the three legacy rows carry the same prices as `deepseek-flash`.

- [ ] **Step 2: Run them to verify they fail**

Run: `./visual-relay test ModelCatalogDeepSeekFlashTests`
Expected: FAIL (route missing).

- [ ] **Step 3: Implement the route, price, chains and selectable lists; update the pinned tests**

Follow the "Must change" list in `30-deepseek.md` file by file. Regenerate goldens: `VR_UPDATE_GOLDENS=1 ./visual-relay test RequestGoldenTests`. Then prove the request shape live once: export `DEEPSEEK_API_KEY` from `~/.config/visual-relay/.env` into the shell and run `VR_RUN_LIVE_GOLDENS=1 ./visual-relay test LiveRequestAcceptanceTests`; record the outcome in the report (one small real request; if the key is missing or the provider is down, say so rather than skipping silently).

- [ ] **Step 4: Run the catalog, pricing and runner test families, then the full suite**

Run: `./visual-relay test ModelCatalog`, `./visual-relay test RelayPricing`, `./visual-relay test RelayCostEstimator`, `./visual-relay test CostPerModel`, `./visual-relay test FirstPartySubagentRunnerTests`, `./visual-relay test RequestGolden`, then `./visual-relay test`.
Expected: all green, no new warnings.

- [ ] **Step 5: Run the gate**

Run: `./visual-relay check` then `git checkout -- docs/images`.
Expected: exit 0, `inspect-code: 0 findings`.

- [ ] **Step 6: Commit**

Subject: `feat(models): make deepseek v4.1 flash the default cheap and balanced model`. Body bullets (at most three, at most 20 words each, no file names): the price figures and their source date; the legacy names being served by V4.1 Flash and re-priced (V4 Pro from 2026-09-14); the existing models kept as ordered fallbacks and the live golden check outcome.

---

### Task 3: Control API `create-task` command

**Files:**
- Create: `src/VisualRelay.App/Services/ControlApi.Tasks.cs` (partial of `ControlApi`), `tests/VisualRelay.Tests/ControlApiCreateTaskTests.cs`.
- Modify: `src/VisualRelay.App/Services/ControlApi.cs` (register the property action in `PropertyActions`, `InvokePropertyAction` dispatch, and `BuildCommandsMap`), `src/VisualRelay.App/Services/ControlRoutes.cs` and the `GET /` index page if it lists commands, `AGENTS.md` (control API section: document the command), `docs/OPERATIONS.md` only if it lists control commands.

**Interfaces:**
- Consumes: `MainWindowViewModel.NewTaskTitle`, `NewTaskBody`, `CreateNewTaskCommand` (IAsyncRelayCommand) in `MainWindowViewModel.Authoring.cs`; `RelayTaskWriter.Slugify` / `ValidateSlug`; `ControlJson.ReadString`.
- Produces: `POST /command/create-task` with body `{"title":"<text>","body":"<markdown, optional>"}`. Response `200 {"ok":true,"id":"<slug>","path":"<abs path of the task markdown>"}`. `400 {"ok":false,"error":"..."}` when the title is missing or slugifies to an invalid or already existing id. `409` when no root is open or the app is busy (mirror `CreateNewTaskCommand.CanExecute`). The created task appears in `/state.tasks[]` after the call returns (creation reloads the task list, as the GUI does).

- [ ] **Step 1: Read the existing pattern**

Read `ControlApi.cs`, `ControlApi.State.cs`, `MainWindowViewModel.Authoring.cs:233-284`, and one existing property-action test (`ControlApi*Tests.cs`, which builds the handler with `ControlServer.BuildHandler` on a headless view model) to copy the test harness.

- [ ] **Step 2: Write the failing tests**

`ControlApiCreateTaskTests` (use `[AvaloniaFact]` if the existing control tests do): creating a task writes `llm-tasks/<slug>/<slug>.md` starting with `# <title>` and the body, returns the slug and path, and `/state` then lists the task; a missing title returns 400; a duplicate slug returns 400; creating while `IsBusy` returns 409.

- [ ] **Step 3: Run them to verify they fail**

Run: `./visual-relay test ControlApiCreateTaskTests`
Expected: FAIL with 404 (unknown command).

- [ ] **Step 4: Implement**

In `ControlApi.Tasks.cs`, add `InvokeCreateTaskAsync(JsonObject body)` that validates the title, sets `NewTaskTitle`/`NewTaskBody` on the view model, checks `CreateNewTaskCommand.CanExecute(null)` (409 otherwise), awaits `ExecuteAsync(null)`, resolves the created slug via `RelayTaskWriter.Slugify(title)` and returns the JSON above. Wire it into the property-action table. Keep `ControlApi.cs` under 300 lines (it is 275).

- [ ] **Step 5: Run the control API test families and the full suite**

Run: `./visual-relay test ControlApi`, `./visual-relay test ControlServer`, then `./visual-relay test`.
Expected: green.

- [ ] **Step 6: Document and gate**

Add `create-task` to the AGENTS.md control API command list with its body and responses. Run `./visual-relay check`; `git checkout -- docs/images`.

- [ ] **Step 7: Commit**

Subject: `feat(control): add a create-task command for headless task authoring`. Body: one or two bullets on the request body and the reuse of the GUI creation path.
