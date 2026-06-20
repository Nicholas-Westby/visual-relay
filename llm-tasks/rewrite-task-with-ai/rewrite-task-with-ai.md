# "Rewrite with AI": a sandboxed frontier-model rewrite of a not-yet-started task spec

Add a **Rewrite with AI** action atop a selected task's spec. It runs the frontier model in an
isolated, sandboxed run that researches the codebase and overwrites the task's own markdown with a
tighter, better-grounded spec — without blocking the queue or other tasks. It is offered only for a
task that has **no run history yet**, guarded by an "Are you sure?" confirm, shows a live stopwatch
while it works, can be cancelled mid-flight, and can be reverted to the pre-rewrite text afterward.

This design is decided — implement exactly this, no alternatives. The whole point of the feature is
to turn a short prompt into a *good* spec, so the rewrite prompt itself encodes what "good" means
(succinct, code-grounded, one decided direction). Mirror that discipline in this file.

## Current state (researched)

**Where it goes.** `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` renders the selected
task. The Markdown tab has a **read-only toolbar** — `<Border Grid.Row="0" … IsVisible="{Binding
IsMarkdownReadOnly}">` holding `<Button Command="{Binding EditSelectedTaskCommand}" Content="Edit"/>`
and an `EditBlockedReason` hint. This toolbar sits directly above the spec text — the natural home
for the new action. **This XAML file is already 298 lines, against the hard 300-line guard**
(`tools/guards/check-file-size.sh`), so you MUST factor the new controls out: add a small
`Views/Controls/RewriteToolbar.axaml` (+ `.axaml.cs`) child control and host it in the read-only
`Border`. Do not inline more rows into `TaskDetailPanel.axaml`.

**Per-task action pattern.** Commands are CommunityToolkit MVVM `[RelayCommand(CanExecute =
nameof(CanX))]` methods on the `MainWindowViewModel` partials. `MainWindowViewModel.Authoring.cs`
is the model: `EditSelectedTask()`, `CanEditSelectedTask()` (sets `EditBlockedReason` and returns
false when `_runningTaskId == SelectedTask.Id`, when `SelectedTask.IsArchived`, or while
`IsEditingMarkdown`), `SaveEditAsync()` → `await RelayTaskWriter.SaveAsync(SelectedTask.Task,
EditBuffer)` then `await LoadSelectedTaskAsync(SelectedTask)`, and the confirm helper
`ConfirmRemoveAttachmentAsync` → `await ShowConfirmationAsync("Remove Attachment", …)`.

**"Not started yet" signal.** `RelayTaskItem` (`src/VisualRelay.Domain/RelayTaskItem.cs`) exposes
`CompletedStageCount`; `MetricsLine => CompletedStageCount == 0 ? "No run history" : …`. **`CompletedStageCount
== 0` is the canonical "no run yet" test.** Also gate on `!SelectedTask.IsArchived`.

**Selected task + markdown.** `SelectedTask` is a `TaskRowViewModel` (`.Id`, `.Task` →
`RelayTaskItem`, `.IsArchived`). `MainWindowViewModel.Commands.cs` `SelectTaskAsync` sets
`SelectedTaskMarkdown = input.Markdown` via `new RelayTaskRepository(RootPath).ReadTaskInputAsync(
task.Task)`; `LoadSelectedTaskAsync(task)` reloads. That live `SelectedTaskMarkdown` string is the
snapshot source for revert.

**Confirm dialog.** `MainWindowViewModel` holds `public Func<string,string,Task<bool>>?
ShowConfirmationAsync { get; set; }`, wired in `App.axaml.cs`
(`viewModel.ShowConfirmationAsync = (title, message) => ShowConfirmationAsync(window, …)`, a modal
`Window` returning `Task<bool>`). When null (headless/tests) callers proceed without prompting —
keep that contract.

**Stopwatch plumbing already exists.** `MainWindowViewModel.cs` owns `private DispatcherTimer?
_elapsedTimer;`, started once by `StartElapsedTimer()` (1 s interval, `Tick +=
UpdateRunningElapsedLabels`). `MainWindowViewModel.LiveState.cs` `UpdateRunningElapsedLabels()` loops
`_runningTaskIds` and sets `task.RunningElapsedLabel = ElapsedFormatter.Label(now - startedAt)` using
`_runStartedAt`. `ElapsedFormatter.Label(TimeSpan)` (`src/VisualRelay.Domain/`) renders `"2m 25s"`.
`TaskRowViewModel` already has a settable `RunningElapsedLabel` (raises change). Reuse this timer —
do not add a second one.

**Concurrency / non-blocking.** Multi-task running state lives in `MainWindowViewModel.cs`:
`_runningTaskIds` (HashSet), `_runStartedAt`, and the single "followed" `_runningTaskId`.
`_isBusy` / `IsBusy` gates the queue and run buttons via `[NotifyCanExecuteChangedFor]`. The drain
(`MainWindowViewModel.Execution.cs` `DrainQueueAsync`) loads `var config = await
RelayConfigLoader.LoadAsync(RootPath)` and builds runners as `new SwivalSubagentRunner(config,
eventSink: new ObservableRelayEventSink(HandleRelayEvent))`. `RelayQueueController.RefreshAsync()`
seeds its public `Tasks` from `ListPendingAsync()`, and `DrainAsync` runs `Tasks.Where(t =>
!t.NeedsReview)`.

**Sandboxed run primitives (reuse these — do not reinvent).**
- `ISubagentRunner.RunAsync(StageInvocation, CancellationToken)` →
  `SubagentResult(RawText, Json, IsValid, Error)` (`src/VisualRelay.Core/Execution/Interfaces.cs`).
  The impl `SwivalSubagentRunner` (`ProcessRunners.*.cs`, ctor `(RelayConfig config, string
  swivalBinary = "swival", IRelayEventSink? eventSink = null, …)`) already wraps every run in the
  capability sandbox: `BuildLaunchTarget` emits `nono run -p vr-guard --allow-cwd … --rollback --
  swival …` (profile `NonoProfile = "vr-guard"`). Writes are confined to `--allow-cwd`; writes
  outside it are rolled back. Cancelling the token kills the whole process tree. This is exactly
  "sandboxed like normal stages."
- `StageInvocation` (`src/VisualRelay.Domain/StageInvocation.cs`): `(Stage, Tier, RunId, TargetRoot,
  TaskName, TaskInput, LedgerSoFar, Manifest, LogSources, TraceDirectory, ReportFile, MaxTurns, …)`.
  `RelayStageDefinition` is `(Number, Name, Tier, Kind, Files, Commands, SystemPrompt,
  OutputContract)`. The **frontier** tier is a real, configured profile: `RelayStages.cs`
  `Stage(7, "Review", "frontier", …)`, and `RelayConfig.TierProfiles["frontier"] == "frontier"`
  (`RelayConfigLoader.cs`) resolves it through the backend proxy. Set `Tier = "frontier"`.
- Isolation: `PlanningWorktree` (`src/VisualRelay.Core/Execution/PlanningWorktree.cs`, public
  static) — `CreateAsync(repoRoot, taskId, runId, ct)` makes a detached HEAD worktree under a temp
  root; `CopyConfigIntoWorktree(repoRoot, worktree)` seeds `.relay/config.json`; `RemoveAsync(...)`
  deletes it; `PruneLeftoversAsync(...)` reaps leftovers. Planning stages already run sandboxed LLM
  work this way and throw the worktree away.
- `RelayConfig.TasksDir` defaults to `"llm-tasks"`; `RelayTaskItem.TaskDirectory` /`.MarkdownPath`
  give the task's folder and spec file. `WorktreeFilter.IsUnderTasksDir` confirms the "keep only the
  task folder" boundary is already a concept in the codebase.

## What to build

TDD — write the failing tests first; keep the core logic pure/injectable so it tests without a real
process launch or GUI (use `IGitInvoker` + a fake `ISubagentRunner`, as `WorktreeFilter` /
`PlanningWorktree` tests do). Headless UI assertions use `[AvaloniaFact]` (per `AGENTS.md`).

1. **`RewriteGuidance`** (`src/VisualRelay.Core/Execution/RewriteGuidance.cs`, pure static): the
   rewrite **system prompt** (the "what makes a good LLM task" rules — see next section, lift
   verbatim) plus `string BuildInput(string currentSpec, string specRepoRelativePath)` that frames
   the per-run instruction (current spec + the exact file to overwrite + "research, then rewrite in
   place; do not implement; keep the author's intent; touch nothing outside this task's folder").
   Unit-test that the input embeds the path and the current spec and never invents scope.

2. **`TaskRewriteRunner`** (`src/VisualRelay.Core/Execution/TaskRewriteRunner.cs`): orchestrates one
   isolated, sandboxed rewrite. `Task<RewriteOutcome> RunAsync(string rootPath, RelayTaskItem task,
   RelayConfig config, ISubagentRunner runner, CancellationToken ct, IGitInvoker? git = null)`:
   1. `runId = "rewrite-" + DateTimeOffset.UtcNow.Ticks` (a unique id; `DateTimeOffset.UtcNow` is
      fine in app/core code).
   2. `worktree = await PlanningWorktree.CreateAsync(rootPath, task.Id, runId, ct, git)`, then
      `PlanningWorktree.CopyConfigIntoWorktree(rootPath, worktree)`.
   3. **Seed the latest spec into the worktree**: copy the live `task.TaskDirectory` (which may hold
      uncommitted edits) into the worktree's matching path, so the model rewrites current content,
      not stale HEAD.
   4. Build a one-off `StageInvocation` with `TargetRoot = worktree`, `Tier = "frontier"`, a custom
      `RelayStageDefinition` (`Number = 0`, `Kind = "llm"`, `Files = "all"`, `Commands = "all"`,
      `SystemPrompt = RewriteGuidance.SystemPrompt`, a minimal `OutputContract` e.g.
      `{ "summary": string }`), `TaskInput = RewriteGuidance.BuildInput(currentSpec,
      specRepoRelativePath)`, `Manifest = [specRepoRelativePath]`, `MaxTurns` from `config`,
      `TraceDirectory`/`ReportFile` under the worktree's `.relay/<taskId>/rewrite/`.
   5. `var result = await runner.RunAsync(invocation, ct)` — this runs under `nono`/`vr-guard`
      automatically; the model may read broadly, edit, and run commands, all confined to the
      worktree.
   6. **On success only** (no cancellation, no throw): copy the worktree's task folder back over
      `rootPath/<task folder>` — and **only** that folder — so the rewritten spec (and any
      attachments the model added inside the folder) persist and nothing else does. Re-read the spec
      to report whether it actually changed.
   7. `finally`: `await PlanningWorktree.RemoveAsync(rootPath, worktree, ct, git)`. On cancellation
      or error, the copy-back in (6) is skipped, so the on-disk spec is **never** touched and the
      user's working tree is untouched — there is nothing to "restore."
   - Tests (fake `ISubagentRunner` that writes into the worktree spec and also scribbles a stray
     file outside the task folder): success copies the new spec back and the stray file does NOT
     appear in the main tree; the worktree is removed; a pre-existing dirty file in the main tree is
     untouched; cancellation/throw leaves the original spec byte-identical and removes the worktree.

3. **Rewrite state + commands on the VM** (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs`,
   new partial; add the backing fields to `MainWindowViewModel.cs`):
   `_rewritingTaskIds` (HashSet), `_rewriteStartedAt` (Dictionary), `_rewriteCts`
   (Dictionary<string, CancellationTokenSource>), `_rewriteUndo` (Dictionary<string,string> — the
   in-memory pre-rewrite text). Commands:
   - **`RewriteSelectedTaskAsync`** `[RelayCommand(CanExecute = nameof(CanRewriteSelected))]`.
     `CanRewriteSelected` = `SelectedTask is not null && !SelectedTask.IsArchived &&
     SelectedTask.Task.CompletedStageCount == 0 && !IsEditingMarkdown && !IsNewTaskDialogOpen &&
     id ∉ _runningTaskIds && id ∉ _rewritingTaskIds`. **Do NOT gate on `IsBusy`** — a rewrite must be
     allowed while the queue drains other tasks. Handler: confirm via `ShowConfirmationAsync(
     "Rewrite with AI", "Replace this task's spec with an AI-researched rewrite? The current text is
     kept so you can revert.")` (proceed when the delegate is null). On confirm: capture
     `_rewriteUndo[id] = SelectedTaskMarkdown`; add to `_rewritingTaskIds`; set `_rewriteStartedAt[id]
     = DateTimeOffset.UtcNow`; create `_rewriteCts[id]`; load `config`; build `new
     SwivalSubagentRunner(config, eventSink: new ObservableRelayEventSink(HandleRelayEvent))`;
     run `TaskRewriteRunner.RunAsync(...)` on a background task. Marshal all VM-state mutations and
     UI updates (status, `ReloadTaskListAsync(id)`, command `NotifyCanExecuteChanged`) onto the UI
     thread (reuse the drain's existing dispatch discipline), so the 1 s timer never races the
     fields. On completion: drop from `_rewritingTaskIds`/`_rewriteStartedAt`/`_rewriteCts`; if the
     spec changed, keep `_rewriteUndo[id]` and surface "Rewrote <id> — review and Revert if needed";
     if unchanged or errored, drop `_rewriteUndo[id]` and surface the reason. Always reload so the
     new text shows.
   - **`CancelRewriteSelected`**: `_rewriteCts[id]?.Cancel()`. Must be a no-op-safe if the run
     already finished/errored (guard the lookup). The background task's `finally` cleans up.
   - **`RevertRewriteSelected`** `[RelayCommand(CanExecute = nameof(CanRevertSelected))]`:
     `await RelayTaskWriter.SaveAsync(SelectedTask.Task, _rewriteUndo[id])`, drop `_rewriteUndo[id]`,
     `await LoadSelectedTaskAsync(SelectedTask)`. `CanRevertSelected` = undo exists for the selected
     id AND it is not currently rewriting.
   - **Stopwatch**: in `UpdateRunningElapsedLabels()` add a loop over `_rewritingTaskIds` setting a
     `RewriteElapsedLabel` on the matching `TaskRowViewModel` (mirror `RunningElapsedLabel`) and a VM
     property `SelectedTaskRewriteElapsed` (the selected task's elapsed, or empty) via
     `ElapsedFormatter.Label`. Bind the toolbar stopwatch to `SelectedTaskRewriteElapsed`.
   - Expose computed bind targets for the toolbar: `IsSelectedTaskRewriting`,
     `CanRewriteSelected`, `SelectedTaskHasRewriteUndo`. Because rewrite state is plain fields (not
     `[ObservableProperty]`), explicitly raise the relevant `…Command.NotifyCanExecuteChanged()` and
     `OnPropertyChanged(...)` on every state transition **and on selection change**.

4. **Mutual exclusion with runs.** A task mid-rewrite must not be executed until the rewrite ends
   (its on-disk spec is only finalized at the very end). Enforce: (a) `CanRunSelected` /
   `CanResumeSelected` return false when `SelectedTask.Id ∈ _rewritingTaskIds`; (b) in
   `DrainQueueAsync`, after `controller.RefreshAsync()` and the existing visible-order application,
   remove any `controller.Tasks` whose `Id ∈ _rewritingTaskIds` before the drain proceeds (the drain
   derives its queue from `controller.Tasks`). Add a test that a drain skips a rewriting task.

5. **Block editing while rewriting.** In `CanEditSelectedTask()` add, before the archived check:
   `if (SelectedTask is not null && _rewritingTaskIds.Contains(SelectedTask.Id)) {
   EditBlockedReason = "Cannot edit a task while it's being rewritten."; return false; }`.

6. **Drop the undo on the documented triggers.** Remove `_rewriteUndo[id]` (so Revert disappears)
   when: the task is saved after a manual edit (`SaveEditAsync`); a run/plan starts for that id (the
   spot in `MainWindowViewModel.LiveState.cs` where `_runningTaskIds.Add(...)` happens); a fresh
   rewrite starts for that id (replaced). It is in-memory only, so app quit clears it for free.

7. **`RewriteToolbar` control** (`src/VisualRelay.App/Views/Controls/RewriteToolbar.axaml` +
   `.axaml.cs`, `x:DataType="vm:MainWindowViewModel"`): a horizontal strip hosted in
   `TaskDetailPanel.axaml`'s read-only `Border` (alongside "Edit"). Three states, by binding:
   - idle & eligible → **"Rewrite with AI"** button (`RewriteSelectedTaskCommand`), visible only
     when `CanRewriteSelected`;
   - rewriting → a spinner/label "Rewriting… {SelectedTaskRewriteElapsed}" + **Cancel**
     (`CancelRewriteSelectedCommand`), visible when `IsSelectedTaskRewriting`;
   - done with undo available → **Revert** button (`RevertRewriteSelectedCommand`), visible when
     `SelectedTaskHasRewriteUndo`.
   Keep `TaskDetailPanel.axaml` **under 300 lines** after the edit (extracting the toolbar is what
   buys the headroom). Optionally show a compact "Rewriting · {elapsed}" chip on the queue row
   (`TaskRowViewModel`) so progress is visible while you view another task.

8. **Control API parity** (`src/VisualRelay.App/Services/ControlApi.cs` `ResolveCommand` switch +
   the `/state` commands map in `ControlApi.State.cs`): register `rewrite-selected`,
   `cancel-rewrite`, `revert-rewrite` → the three commands. This is how the implementer can drive and
   screenshot the feature headlessly (`AGENTS.md` "Driving the running app"); the null-confirm
   contract lets the command proceed without a modal.

## The rewrite prompt (lift into `RewriteGuidance.SystemPrompt`)

> You rewrite a single Visual Relay task spec into a better one. You are NOT implementing the task —
> you only improve its specification, then overwrite its markdown file in place. Preserve the
> author's intent and scope exactly; sharpen and ground it, never expand or redirect it.
>
> A good task spec:
> 1. **Is succinct.** A reader should grasp it in a minute or two. Cut restated requirements,
>    motivation padding, and hedging. Prefer the shortest spec that removes all ambiguity.
> 2. **Is grounded in the real codebase.** Cite concrete files, types, methods, and short verbatim
>    snippets as stable anchors (filename + symbol + a few words of code) — **never line numbers**,
>    which drift. Open each file and confirm the symbol exists before citing it.
> 3. **Gives one decided direction**, not a menu of options. Resolve trade-offs yourself; state the
>    chosen approach and that it is final. Never hand the implementer a choice.
> 4. **Is structured**: a one-paragraph what/why; a "Current state (researched)" section anchored to
>    code; an ordered, TDD-first "What to build"; and a "Done when" section of verifiable criteria.
> 5. **Names the repo's guardrails**: `./visual-relay check` must pass; keep changed C#/XAML files
>    under 300 lines (split into partials/child controls); a Conventional Commit subject; headless UI
>    tests use `[AvaloniaFact]`; per-machine state lives in XDG, never in-repo.
> 6. **Scopes tightly**: minimal diffs; change only what the task needs; do not reformat unrelated
>    code. Because the implementer sees only this one file, bake in any context they need.
>
> Research the codebase as needed (read, grep, build, run — all sandboxed). Then overwrite the task's
> own markdown file with the rewritten spec. Do not edit, create, or delete anything outside this
> task's own folder. End with the required JSON block.

`BuildInput` supplies the current spec verbatim, the spec's repo-relative path to overwrite, and a
one-line reminder to keep intent and stay in-folder.

## Done when

- New unit tests pass and fail against today's (absent) code first: `TaskRewriteRunner` success
  copies only the task folder back (stray out-of-folder writes discarded, pre-existing dirty files
  untouched, worktree removed); cancellation/error leaves the spec byte-identical; the drain skips a
  rewriting task; `CanEditSelectedTask` is blocked with the right reason while rewriting;
  `CanRewriteSelected` is true only for a `CompletedStageCount == 0`, non-archived, non-running,
  non-rewriting selected task and is **independent of `IsBusy`**.
- Live behavior: selecting a fresh task shows **Rewrite with AI**; clicking it confirms, then runs a
  frontier, `nono`/`vr-guard`-sandboxed rewrite in an isolated worktree while the stopwatch ticks
  every second and the queue/other tasks keep working; the task is not editable and not runnable
  meanwhile; **Cancel** interrupts gracefully (and is harmless if it already finished/errored);
  after success the spec is replaced and **Revert** restores the original; Revert disappears once the
  task is edited, a run starts, or the app quits; only the task's own folder is ever changed on disk.
- `./visual-relay check` is green; every changed/added C# and XAML file is < 300 lines (notably
  `TaskDetailPanel.axaml` after extracting `RewriteToolbar`); Conventional Commit subject, e.g.
  `feat(app): rewrite-with-ai sandboxed frontier task-spec rewriter`.
- **Self-contained:** the implementer sees only this file. No dependency on other queued tasks; the
  feature lands green on its own.
