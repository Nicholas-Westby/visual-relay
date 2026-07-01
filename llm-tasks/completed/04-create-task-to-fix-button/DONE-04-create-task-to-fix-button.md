# "Create Task to Fix" Button

## Problem / motivation

When a run flags for reasons unrelated to the task's own change — flaky tests, latent ordering bugs, a
guard tripped by pre-existing state — the user is left to hand-author a follow-up task to fix the
underlying issue. Give them a one-click path instead: a button that hands the **failed run's issues** to
an LLM and ends with a **new llm-task** in the queue that is designed to fix those issues. This is a
general coping mechanism for anything that blocks VR from completing work (the flaky verify gate being
the motivating example).

The button **creates** the fix task; it does **not** auto-run it. The user reviews and runs it when ready.

## Where the button goes

In the **"LATEST RUN FAILED"** banner in the task detail pane —
`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`, the block gated by
`IsVisible="{Binding HasSelectedTaskError}"` that renders the `"LATEST RUN FAILED"` label and
`SelectableTextBlock Text="{Binding SelectedTaskError}"`. Add the button inside/adjacent to that banner
so it appears exactly when the selected task has a failed/flagged run (`HasSelectedTaskError == true`).
Suggested label: **"Create task to fix"**.

## Precedent to model on: "Rewrite with AI"

VR already has an LLM-authors-task-markdown feature — reuse its shape rather than inventing new infra:
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Rewrite.cs` — a `[RelayCommand]` with a
  `CanExecute` predicate, a **test-seam factory** (`RewriteRunnerFactory : Func<RelayConfig, ISubagentRunner>`;
  production builds a real `SwivalSubagentRunner`, tests inject a fake), busy/status handling, and a
  confirm modal.
- `TaskRewriteRunner.RunAsync(...)` — does the subagent call and returns a `RewriteOutcome(success, error)`.
- The shared modal seam `ConfirmAsync(title, message, confirmLabel)`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Confirmation.cs`, returns `Task<bool>`) — a human
  click opens the modal; headless/API runs auto-resolve, so it is test-friendly.
- Task creation lives at `CreateNewTaskAsync` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Authoring.cs`),
  backed by `RelayTaskRepository`.

## Behavior

Add a command `CreateFixTaskForSelectedAsync` (new partial, e.g.
`src/VisualRelay.App/ViewModels/MainWindowViewModel.FixTask.cs`), modeled on the Rewrite command.

1. **Gather the failed-run context** from `.relay/<taskId>/` (add a small reader in Core, e.g.
   `FailedRunContext`):
   - the flag reason and which stage flagged — `NEEDS-REVIEW` and `status.json` (the `error` + `check`
     of the flagged stage);
   - the **authoritative gate failures** — the `stage*-attempt*.verify-output.txt` files, especially the
     last attempt's `[FAIL]` lines / exceptions / summary. Prefer these over any agent self-reported
     "passed" text (the authoritative gate is the source of truth);
   - the per-stage `ledger.md` summaries (the agent's own diagnoses are useful signal).
2. **Author the fix task via a subagent** (reuse `SwivalSubagentRunner` through a
   `FixTaskRunnerFactory : Func<RelayConfig, ISubagentRunner>` test seam, exactly like `RewriteRunnerFactory`;
   put the actual call in a new `FixTaskAuthorRunner.RunAsync` modeled on `TaskRewriteRunner`). Directive
   to the model: *"Here are the failures from a flagged VR run: <context>. Author a new llm-task
   (markdown) that will make these failures stop recurring — make flaky / non-deterministic tests
   deterministic, fix root causes, and address the enabler where possible. Never weaken, skip, or delete
   tests. Output the task markdown: title, problem, root cause, fix, and constraints."* The runner returns
   the authored markdown, a one-line summary of what it addresses, and a suggested slug.
3. **Write the new task** by reusing the task-creation path (`CreateNewTaskAsync` / `RelayTaskRepository`):
   create `llm-tasks/<slug>/<slug>.md` with the authored markdown. Ensure the slug is **unique** (append a
   disambiguator if it collides with an existing task). Then reload the task list so it appears in the
   queue. Do not add a numeric prefix — leave prioritization to the user (they can reorder).

## UX — be explicit

- **Idle:** the "Create task to fix" button sits in the red "LATEST RUN FAILED" banner, enabled only when
  the selected task has a failed run and nothing else is running.
- **Working:** on click, the button enters a busy state — label to something like "Creating fix task…",
  disabled to prevent double-submit, with a spinner/`StatusText` update (this is a multi-second subagent
  call; reuse the busy pattern from Rewrite).
- **Success:** show a **dismissable completion modal** via `ConfirmAsync` —
  `title: "Fix task created"`, `message:` the new task's name plus the one-line summary of the issues it
  addresses, `confirmLabel: "View task"`. If the user clicks **View task** (`ConfirmAsync` returns
  `true`), select the newly created task (set `SelectedTask` to it) so it opens in the detail pane /
  markdown view. If they dismiss (`false`), just close — the task is already in the queue. The queue now
  shows the new task.
- **Error / no data:** if there is no failed-run data the button is not enabled. If the subagent errors or
  returns nothing usable, write **nothing** and surface a clear message (an error `ConfirmAsync` or a
  `StatusText`): *"Couldn't create fix task: <reason>."* Re-enable the button so the user can retry. Never
  crash.

## Constraints & done criteria

- **Wire the command's `CanExecute` correctly.** Add `[NotifyCanExecuteChangedFor(nameof(CreateFixTaskCommand))]`
  to every observable property that gates it (e.g. `SelectedTask`, `IsBusy`, and whatever drives
  `HasSelectedTaskError`). A missing `NotifyCanExecuteChangedFor` silently leaves a button disabled even
  when its predicate would allow it — make sure every gating property notifies this command.
- Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines; no weakening/skipping/deleting tests.
- The generated task must not auto-run; creation is the terminal action.
- Tests (inject a fake `ISubagentRunner` via the factory seam, like the Rewrite tests):
  - the button is enabled only when the selected task has a failed run;
  - clicking authors markdown and writes a new task under `llm-tasks/<slug>/<slug>.md`, and the task
    appears in the queue;
  - slug collisions are disambiguated;
  - the completion modal (`ConfirmAsync`) is invoked, and "View task" selects the new task;
  - the error path (subagent failure) writes nothing and surfaces a message;
  - headless runs auto-resolve `ConfirmAsync` so the flow completes without a real UI.
- Manual verification: select the flagged `disable-new-buttons-when-project-isn-t-selected` task, click
  "Create task to fix", confirm a new task is authored from its `.relay` failure logs, the completion
  modal appears, and "View task" opens it.

## Files likely in scope (the plan stage will finalize the manifest)

- `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` — the button in the failure banner
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.FixTask.cs` (new) — command + flow + `CanExecute` wiring
- a new Core runner `FixTaskAuthorRunner` + a `FailedRunContext` reader (model on `TaskRewriteRunner`)
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` — `[NotifyCanExecuteChangedFor]` on gating props
- tests under `tests/VisualRelay.Tests/`
