# Advisory calls answer in one tool-less turn, with reasoning off

Two model calls in the harness are advisory: they read what a run left behind
and answer a question, and nothing they say may change the tree. The stage-5
diff audit has been sent without tools since c68fd37d, after it spent its only
turn calling two of them. The fix-task author has not: it is built from the
same factory as a real stage, so it is offered the full fourteen-tool catalog,
write and delete included, for up to 200 turns, against the operator's own
working tree rather than a worktree, while its own doc comment calls it "a
read-only prompt-and-parse step". Separately, no call in the harness sends a
reasoning effort, so `deepseek-flash` reasons at its default `high` on every
one-shot answer, on a route whose 30-second first-byte budget is the one to
watch. This task makes the advisory shape a single named thing: one turn, no
tools, reasoning off where the provider allows it.

## Evidence

- Review of 2026-09-11, items 6 and 12.
- `ViewModels/MainWindowViewModel.FixTask.cs:63-66` builds the fix-task runner
  through `SubagentRunnerFactory.Create`, whose `BuildTools`
  (`Agent/SubagentRunnerFactory.cs:46-53`) registers `FileToolset` (nine tools,
  `WriteFileTool`, `EditFileTool` and `DeleteFileTool` among them,
  `Tools/FileToolset.cs:21-29`) and `CommandToolset` (five, `Tools/CommandToolset.cs:19-28`).
  `Execution/FixTaskAuthorRunner.cs:72-85` then passes `TargetRoot: rootPath`,
  `MaxTurns: config.MaxTurns` and no `WithoutTools`. Nothing structural keeps
  that call from editing the tree; only the prompt asks it not to.
- c68fd37d: offered a catalog, the audit "spent that one turn on two lookups
  and reported no answer at all"; `StageInvocation.WithoutTools`
  (`Domain/StageInvocation.cs:50-54`) fixed it for the audit alone
  (`Execution/AuthorTestDiffAuditor.cs:184-187`).
- `Agent/AgentTurnLoop.Model.cs:36` builds every request as
  `new ChatRequestOptions(options.Model, Stream: true, MaxTokens: maxTokens)`;
  neither `AgentLoopOptions` (`Agent/AgentLoopOptions.cs:36-46`) nor
  `StageInvocation` carries an effort, so `ChatRequestBuilder.cs:72-76`, which
  already sends `reasoning_effort` and withholds an unsupported value, never
  receives one. `Llm/ProviderCapabilities.cs:53-56`: DeepSeek accepts
  `none|low|medium|high`, and `none` clears `reasoning_content` (`:44`); Z.AI
  takes `low|high|max` (`:59-62`); Moonshot and Hugging Face take nothing.
- `Llm/Routing/ProviderRoutes.cs:56-65`: `deepseek-flash` "defaults to
  reasoning effort high, which is why the Fast profile's 30s time to first byte
  is the budget to watch" (`Fast`, `:33-37`). Fifteen validation runs saw no
  timeout; the cost is the tokens, billed as completion.

## Current state (researched at d5bf93cc)

- The advisory calls: the audit (`AuthorTestDiffAuditor.Invocation`, `:153-188`,
  `MaxTurns: 1`, `WithoutTools: true`, tier `cheap`) and the fix-task author
  (`FixTaskAuthorRunner.RunAsync`, `:40-143`, tier `balanced`, its report at
  `.relay/<task>/fix-task/fix-task.log`). Both build a `RelayStageDefinition`
  with `Number: 0` by hand. `TaskRewriteRunner.cs:87-112` is also `Number: 0`
  but is a real multi-turn stage in its own worktree (`Files: "all"`,
  `Commands: "all"`); it is not advisory.
- Two more one-shot turns reason at the provider default: the contract re-ask
  (`Agent/FirstPartySubagentRunner.Contract.cs:62-99`, a tool-less
  `AgentTurnLoop(client, [], …)` at `:78` with `MaxTurns: 1` at `:84`) and the
  bootstrap test-command guess (`Init/ProviderTestCommandCompleter.cs:40-44`,
  which builds its own `ChatRequestOptions`).
- The runner reads `invocation.WithoutTools` once (`FirstPartySubagentRunner.Run.cs:51`)
  and builds `AgentLoopOptions` at `:55-68`.
- Tests: `AuthorTestDiffAuditorTests.RunAsync_AsksForOneToollessTurnAgainstATopLevelContract`
  (`:111`) pins the audit's shape; `FirstPartySubagentRunnerTests.AToollessStage_SendsNoToolsAtAll`
  (`:229`) reads the request body off `ScriptedModelTransport`;
  `ChatRequestBuilderTests.SupportedReasoningEffort_IsSent` (`:192`) and
  `UnsupportedReasoningEffort_IsWithheld` (`:182`) pin the builder's filter;
  `MainWindowViewModelFixTaskTests` (`:135` `Click_AuthorsAndWritesNewTask`)
  drives the fix-task path through `FixTaskFakeRunner`.
- The stage report records `stats.tool_calls_total` and
  `measured_usage.reasoning_tokens` (`Agent/AgentReportWriter.cs:71,100`), so
  both properties of a call are observable after the fact.

## Prescribed approach

1. Thread the effort. `StageInvocation` gains `string? ReasoningEffort = null`
   (null means the provider's default); `AgentLoopOptions` gains the same;
   `FirstPartySubagentRunner.Run.cs:55` passes `invocation.ReasoningEffort`
   and `AgentTurnLoop.Model.cs:36` passes `options.ReasoningEffort` into
   `ChatRequestOptions`. The builder's existing filter decides the wire: `none`
   reaches DeepSeek and is withheld from Z.AI, Moonshot and Hugging Face, so
   the same invocation is safe on every route in every chain.
2. `Execution/AdvisoryInvocation.cs`: `internal static StageInvocation Create(
   RelayStageDefinition stage, string tier, string runId, string rootPath,
   string taskId, string prompt, string traceDirectory, string reportFile,
   int ceilingMs)` returning `MaxTurns: 1`, `WithoutTools: true`,
   `ReasoningEffort: "none"`, empty ledger, manifest and log sources. The
   advisory shape lives here and nowhere else. `AuthorTestDiffAuditor.Invocation`
   and `FixTaskAuthorRunner.RunAsync` both build through it; the audit keeps its
   trace-directory numbering, the fix-task author keeps its `fix-task` directory.
3. The fix-task author's system prompt (`:24-34`) keeps its wording; its doc
   comment (`:17-21`) now says what is true: one turn, no tools, no worktree
   needed because nothing can write. `MaxTurns` from the config no longer
   applies to it.
4. The contract re-ask sets `ReasoningEffort: "none"` on its `AgentLoopOptions`
   (`Contract.cs:79-88`): it re-emits a block it already wrote. The bootstrap
   guess passes `ReasoningEffort: "none"` at `ProviderTestCommandCompleter.cs:43`.
   Every ordinary stage keeps the provider default; this task changes nothing
   about how a stage reasons.
5. `docs/relay-artifacts.md`, the audit paragraph (`:58-62`), gains one
   sentence: the audit and the fix-task author are single tool-less turns sent
   with `reasoning_effort: none` where the provider takes it, so neither can
   touch the tree. `docs/OPERATIONS.md:130` ("one cheap-tier call") stays.

The commit body carries the live re-run of c68fd37d's proof at `none`: the
audit's answer on an assert-only diff and on a logic change, and the served
model. If the logic-change hunk is missed at `none` and found at `low`, the
advisory effort becomes `low`, the constant changes in one place, and the body
says so with the two answers.

## Tests

- `AdvisoryInvocationTests.Create_IsOneToollessTurnWithReasoningOff`: `MaxTurns`
  is 1, `WithoutTools` is true, `ReasoningEffort` is `none`, manifest, log
  sources and ledger are empty, trace directory and report file are the ones given.
- `MainWindowViewModelFixTaskTests.Click_SendsOneToollessTurn`: the
  `FixTaskFakeRunner` captures its invocation; `WithoutTools`, `MaxTurns == 1`,
  `ReasoningEffort == "none"`, `TargetRoot` is the root; `Click_AuthorsAndWritesNewTask`
  still passes.
- `AuthorTestDiffAuditorTests.RunAsync_AsksForOneToollessTurnAgainstATopLevelContract`
  gains the `ReasoningEffort == "none"` assertion.
- `FirstPartySubagentRunnerTests.AnInvocationsReasoningEffort_ReachesTheRequestBody`
  (a DeepSeek route through `ScriptedModelTransport`: the body carries
  `"reasoning_effort":"none"`) and `AStageWithoutAnEffort_SendsNoEffortField`
  (an ordinary invocation: the key is absent).
- `FirstPartySubagentRunnerContractTests.TheReAsk_SendsReasoningOff`: the
  second request body on a DeepSeek route carries `none`; the first does not.
- `ChatRequestBuilderTests.NoneIsWithheldFromAnAlwaysThinkingProvider`: `none`
  against the Z.AI capabilities leaves the body without the key.
- `ProviderTestCommandCompleterTests`: the captured body carries `none` on a
  DeepSeek route.

## Verification (through the control API)

Clone pallets/click as `/Users/admin/Dev/vr-work/click.prep.md` describes, set
`authorTests.diffAudit` to `always` in its `.relay/config.json`, then:

    curl -s -X POST -d '{"path":"<clone>"}' http://127.0.0.1:8765/command/open-folder
    curl -s -X POST -d '{"title":"Probe task","body":"<one of the nine validation tasks>\n"}' http://127.0.0.1:8765/command/create-task
    curl -s -X POST -d '{"id":"probe-task"}' http://127.0.0.1:8765/command/select-task
    curl -s -X POST http://127.0.0.1:8765/command/run-selected
    # poll /state until isBusy is false, then:
    jq '{tools: .stats.tool_calls_total, reasoning: .stats.measured_usage.reasoning_tokens, model: .served_model}' <clone>/.relay/probe-task/stage5-audit1.report.json
    grep -c author_test_audit <clone>/.relay/probe-task/run.log

Expected: `tools` 0, `reasoning` 0 with `deepseek-flash` served, one
`author_test_audit` event with `hunks` 0, and a stage-2 report in the same task
whose `reasoning_tokens` is greater than 0 (ordinary stages are untouched). The
fix-task path has no control-API command: flag a task (cancel it mid-run),
click "Create task to fix" in the GUI, and read the same two numbers from
`.relay/<task>/fix-task/fix-task.log`. Put all four numbers in the commit body.

## Out of scope

A per-stage effort knob in `.relay/config.json` (no evidence any stage wants
one); `TaskRewriteRunner` (a real stage); a read-only catalog for a multi-turn
research call that does not exist yet; the `Fast` profile's budgets.

## Rejected alternatives

- A read-only tool catalog (`FileToolset` minus write, edit and delete) for
  advisory calls, as the review suggested. A one-turn call with any catalog
  spends its turn calling a tool: c68fd37d measured exactly that. A multi-turn
  read-only advisory call would be a new kind of stage, with a budget, a
  watchdog profile and a cost line of its own, for two callers whose whole
  input is already in the prompt.
- A `Kind: "advisory"` on `RelayStageDefinition` checked by the runner: the
  runner cannot tell an advisory `Number: 0` from the rewrite's, and a check
  there would be a second place to keep the shape. The factory is the one place.
- `reasoning_effort: none` on every cheap-tier stage: a Research or Plan stage
  reasons over what it reads; the 30-second first byte held for fifteen runs at
  `high`, so there is no timeout to buy back, only tokens, and those are cheap
  where the reasoning is doing work.
