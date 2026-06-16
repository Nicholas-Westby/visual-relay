# Await the load Task instead of polling for its side effects

A flaky UI test, `MainWindowViewModelTests.LoadRunHistoryAsync_CompletedRun_AllStagesShowComplete`,
fails intermittently under suite CPU load. The mechanism is not a product bug: the test
sets `viewModel.SelectedTask`, then **polls** a derived property
(`SelectedTaskMetricLabel != "No run history"`) with a 1-second hard cap, because the
work that sets that property is launched **fire-and-forget** and the test has no handle
to `await`. When the suite's CPU-burn watchdog tests starve the scheduler, the awaited
disk read + view-model population exceeds 1 s and the poll's final `Assert.True` trips —
a false failure that passes instantly in isolation.

The root cause is the discarded task handle, and the fix is to stop discarding it. Make
the selection-load **awaitable** so tests `await` the real operation (deterministic, no
wall-clock budget), surface the load fault instead of swallowing it into `_`, rewrite
every polling call site to await, then **ban** the polling helper so the anti-pattern
cannot return and **retire** it. Finally, supersede the one line of repo doctrine that
endorsed condition-polling, so the standard is unambiguous: await the Task; do not poll.

Scope guard: this is a UI/view-model change only. It is **distinct** from the
parallel-isolation race handled by `harness-inject-seams-not-global-statics`
(process-global env/git statics). The sibling polling-symptom test
`RelayDriverStatusTests.RunTaskAsync_StatusJson_*` is **out of scope here** — its
`RunTaskAsync` is already fully awaited, so it is an env/parallel-isolation race, not a
fire-and-forget poll; do not touch it in this task.

## Current state (researched)

> **Freshness contract.** Every file path, line number, member name, call-site count,
> and quoted snippet below was read on 2026-06-15 against the working tree. Line numbers
> drift — before editing, re-grep each anchor by **symbol** (`WaitUntilAsync`,
> `WaitUntilWithDispatcherAsync`, `LoadSelectedTaskAsync`, `OnSelectedTaskChanged`,
> `_ = LoadSelectedTaskAsync`, `SelectedTaskMetricLabel`, `HasSelectedTaskError`) and
> trust the symbol, not the line. If an anchor no longer matches (a call site moved,
> a *new* `WaitHelpers` user appeared that is not in the inventory below, or the
> fire-and-forget site already returns a task), STOP and re-research: the call-site
> inventory is the spine of this task and must be exact before any edit.

### The polling helper (the anti-pattern to remove)

`tests/VisualRelay.Tests/WaitHelpers.cs` — a `public static class` with two methods.
Both poll a `Func<bool>` 50×20 ms (1 000 ms hard cap) then `Assert.True(condition())`:

```csharp
public static async Task WaitUntilAsync(Func<bool> condition)
{
    for (var i = 0; i < 50; i++)
    {
        if (condition()) return;
        await Task.Delay(20);
    }
    Assert.True(condition());          // 1 s cap → false failure under load
}

// WaitUntilWithDispatcherAsync: same loop, but RunJobs() before each check
// and after the final check, so Avalonia-bound state has a chance to settle.
public static async Task WaitUntilWithDispatcherAsync(Func<bool> condition) { ... }
```

`tests/VisualRelay.Tests/WaitHelpersTests.cs` covers both (5 facts: immediate-return,
becomes-true-before-timeout, never-true-fails-after-all-polls, dispatcher variants).
These tests exist only to validate the helper; they retire with it.

### Exhaustive `WaitHelpers` call-site inventory (the spine)

A full sweep (`grep -rn "WaitUntilAsync\|WaitUntilWithDispatcherAsync\|WaitHelpers"
tests/`) finds usages in **exactly six** files. Comment lines ("WaitUntilAsync is
provided by WaitHelpers.") are not call sites and are listed separately.

| File | `WaitUntilAsync` calls | `WaitUntilWithDispatcherAsync` calls |
| --- | --- | --- |
| `MainWindowViewModelTests.cs` | 6 (`:19, :153, :159, :178, :188, :190, :199, :201, :216`) — **9 total** | 0 |
| `MainWindowViewModelTests.Status.cs` | 2 (`:48, :108`) | 0 |
| `ConfigInitEmptyStateUiTests.cs` | 0 | 1 (`:70`) |
| `KeySetupPanelUiTests.cs` | 0 | 2 (`:132, :143`) |
| `WaitHelpers.cs` | (definition) | (definition) |
| `WaitHelpersTests.cs` | (its own tests) | (its own tests) |

> Recount note: `MainWindowViewModelTests.cs` has **9** `WaitUntilAsync` call sites
> (lines 19, 153, 159, 178, 188, 190, 199, 201, 216), not 6 — the table row above lists
> every line. Re-grep and enumerate exactly before rewriting; the count is load-bearing
> for the "no call sites remain → delete the helper" step.

What each call site waits for, and **what it should await instead**:

- **The metric-label / stage-population polls** — `Status.cs:48`
  (`SelectedTaskMetricLabel != "No run history"`), `MainWindowViewModelTests.cs:188,199`
  (`== "No run history"`), `:190` (`!= "No run history"`): all wait for
  `LoadRunHistoryAsync` (reached via the selection load) to finish populating
  `SelectedTaskMetricLabel` + `Stages`. **Await the selection-load task.**
- **The error-banner polls** — `MainWindowViewModelTests.cs:153,178,201`
  (`HasSelectedTaskError`), `:159` (`!HasSelectedTaskError`), `Status.cs:108`
  (`HasSelectedTaskError`): wait for the same selection load (it sets `SelectedTaskError`
  inside `LoadRunHistoryAsync`). **Await the selection-load task.** Note `Status.cs:108`
  sets `SelectedTask` *implicitly through init* (`LoadInitialAsync` →
  `ReloadTaskListAsync` auto-selects `Tasks.Single()`), so it must await the load the
  **init path** kicked off (see A2 below) — not a load the test triggered by assignment.
- **The trace-count polls** — `MainWindowViewModelTests.cs:19,216`
  (`TraceEntries.Count == 2` / `== 1`): these follow `await viewModel.LoadInitialAsync()`
  and wait for the init-path selection load to populate trace entries. **Await the
  init-path selection-load task.**
- **The UI dispatcher-settle polls** — `ConfigInitEmptyStateUiTests.cs:70`
  (`!viewModel.NeedsInitialization` after a real button click runs `CreateConfigAsync`),
  `KeySetupPanelUiTests.cs:132,143` (`SelectedTaskMarkdown.Contains("Alpha"/"Beta")`
  after `vm.SelectedTask = …`): these are `[AvaloniaFact]` headless tests where the
  awaited work is dispatched onto the Avalonia UI thread. `:132,143` wait for the
  selection load (markdown is set in `LoadSelectedTaskAsync` before `LoadRunHistoryAsync`)
  → **await the selection-load task**. `:70` waits for an async **command**
  (`CreateConfigCommand`) — `IAsyncRelayCommand` exposes `ExecuteAsync`, so the test can
  `await viewModel.CreateConfigCommand.ExecuteAsync(null)` directly (it already obtains
  the command); after awaiting, a **single** `Dispatcher.UIThread.RunJobs()` flush
  settles bindings. No loop, no time cap.

### Production: the discarded task handle (the bug)

`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs` —
`partial void OnSelectedTaskChanged(TaskRowViewModel? value)` is the
**CommunityToolkit-generated synchronous setter hook** for the `[ObservableProperty]
_selectedTask` field (declared in `MainWindowViewModel.cs`). Because the hook is `void`,
the load it triggers cannot be awaited by the setter, so it is fired-and-forgotten — the
last line is:

```csharp
        _ = LoadSelectedTaskAsync(value);          // Commands.cs:167 — task discarded
```

The chain: `OnSelectedTaskChanged` → `LoadSelectedTaskAsync(value)` (`Commands.cs:170`)
→ `await LoadRunHistoryAsync(task.Id)` (`MainWindowViewModel.RunHistory.cs:7`), which
sets `SelectedTaskMetricLabel` (`RunHistory.cs:11`), `SelectedTaskError`
(`RunHistory.cs:17`, guarded by `_runningTaskId != taskId`), and repopulates `Stages`,
`Events`, `TraceEntries`. `LoadSelectedTaskAsync` also reads task input from disk and
may lazily `PromoteToNestedAsync` — all I/O that can throw. Discarding the task means
**(a)** tests have no handle to await (hence the polling), and **(b)** any thrown
exception is silently swallowed (an unobserved faulted Task).

`SelectedTask` is set from two directions, both of which must end up awaitable:
- **Tests** assign directly: `viewModel.SelectedTask = viewModel.Tasks.Single(...)`.
- **Init** assigns indirectly: `LoadInitialAsync` (`MainWindowViewModel.cs:222`) →
  `RefreshAsync` → `ReloadTaskListAsync` (`MainWindowViewModel.Helpers.cs:96`) auto-selects
  `Tasks.FirstOrDefault()` / the preferred id at `Helpers.cs:124-126`, which fires
  `OnSelectedTaskChanged`. This is why `Status.cs:108` and `MainWindowViewModelTests.cs:19,216`
  poll even though the test never assigns `SelectedTask` itself.

**Second caller — do not break it.** `LoadSelectedTaskAsync` has a *second* production
caller that already awaits it: `MainWindowViewModel.Authoring.cs:66`
(`await LoadSelectedTaskAsync(SelectedTask);` in `SaveEditAsync`, to refresh markdown
after a save). The refactor must keep this awaited call working. The **only**
fire-and-forget site is `Commands.cs:167`.

`[assembly: InternalsVisibleTo("VisualRelay.Tests")]` is already present
(`src/VisualRelay.App/Properties/AssemblyInfo.cs:3`), so an `internal` awaitable surface
on the VM is visible to tests with no new plumbing.

### How the VM surfaces errors today (decides where the fault goes)

Two existing channels — pick one for the swallowed load fault and be explicit:
- **`SelectedTaskError`** (`MainWindowViewModel.cs:140`, `[ObservableProperty]` with
  `[NotifyPropertyChangedFor(nameof(HasSelectedTaskError))]`; `HasSelectedTaskError =>
  !string.IsNullOrEmpty(SelectedTaskError)`, `:142`) — the **per-task error banner**.
  Set today by `LoadRunHistoryAsync` from the *status record's* `Flagged` entries; it
  represents "this task's latest run reported an error," **not** "the UI failed to load
  the task." Overloading it with infrastructure load faults would make
  `HasSelectedTaskError` ambiguous and could collide with the existing
  error-banner tests (`SelectingTask_SurfacesErrorFromFailedLatestRun…`).
- **`StatusText`** via `RunBusyAsync` (`MainWindowViewModel.Helpers.cs:129-149`): the
  established "something went wrong in an operation" channel — its `catch (Exception ex)
  { StatusText = ex.Message; }` is exactly how the VM reports operation faults to the
  user today (e.g. `RefreshAsync` runs inside it). This is the natural home for a load
  fault: it does not overload the per-task error semantics.

### Detection landscape (how bans are enforced here)

- **BannedApiAnalyzers** is wired: `tests/VisualRelay.Tests/BannedSymbols.txt` is an
  `<AdditionalFiles>` (`VisualRelay.Tests.csproj:29`), the package is referenced
  (`csproj:17-20`), and **RS0030 is `severity = error`** (`.editorconfig:70`) — a banned
  symbol **fails the build**. The file currently bans two **types** (`T:` prefix):
  `T:Avalonia.Headless.HeadlessUnitTestSession` and
  `T:Avalonia.Headless.XUnit.AvaloniaTestFrameworkAttribute`. Member-level bans use the
  `M:` documentation-comment ID prefix (method) — this task adds the first `M:` entries.
- **Reflection/source-scan convention tests** live in
  `tests/VisualRelay.Tests/SplitGuardVerificationTests.Conventions.cs` — e.g.
  `NoTestFile_CallsEnvironmentSetEnvironmentVariable` (`:136`) walks every `*.cs` in the
  tests dir, skips a small documented allowlist (itself, `TestDoubles.cs`, `RepoSetup.cs`),
  and `Assert.Empty(violations)` on a forbidden string. That is the exact model for an
  optional complementary source-scan guard here.

### What must NOT be touched (false-positive guard)

`Stopwatch` and `Task.Delay` are used **legitimately and pervasively** by time-based
watchdog/timeout tests — `SwivalSubagentRunnerWatchdogTests.*`, `*.Timeout.cs`,
`RelayQueueControllerParallelTests`, `FdLeakTests`, and others (`WaitHelpersTests.cs`
itself times its asserts with `Stopwatch`). **Do not ban these BCL primitives.** The
anti-pattern is *specifically* `WaitHelpers.WaitUntilAsync` /
`WaitUntilWithDispatcherAsync`. Banning a **project-owned symbol** (`M:VisualRelay.Tests.
WaitHelpers.*`) is surgically safe precisely because it names the repo's own helper and
leaves `System.Threading.Tasks.Task.Delay` / `System.Diagnostics.Stopwatch` untouched.

### The doctrine line to supersede

`llm-tasks/DONE-deflake-timing-sensitive-tests.md` (Goal section, the bullet ending the
"Goal" paragraph) explicitly endorses *"generous-but-bounded waits driven by condition
polling rather than fixed sleeps."* That endorsement is now obsolete for awaitable
operations: the correct standard is to await the real `Task`. The line must be
superseded so a future author cannot cite it to reintroduce a polling helper.

## What to build

> Ordered so the build never breaks: production becomes awaitable **first** (1), tests
> migrate to awaiting it **second** (2), only then is the helper banned (3) and deleted
> (4); doctrine updated (5). Production *runtime behavior is unchanged* except that a
> load fault is no longer silently lost.

### 1. Production (A2) — make the selection load awaitable + stop swallowing faults

In `MainWindowViewModel.Commands.cs`:

- Extract the body of `LoadSelectedTaskAsync(TaskRowViewModel? task)` into an
  **`internal Task SelectTaskAsync(TaskRowViewModel? task)`** (name to taste, e.g.
  `LoadSelectedTaskCoreAsync`) so it is awaitable from tests via `InternalsVisibleTo`.
  Keep the existing `private` caller name working for the already-awaited
  `Authoring.cs:66` call (either rename that call to the new method, or have the private
  method delegate to the internal one — both must remain awaited).
- At the **single** fire-and-forget site (`OnSelectedTaskChanged`, currently
  `_ = LoadSelectedTaskAsync(value);` at `Commands.cs:167`), capture the task into an
  **`internal Task? LastSelectionLoad { get; private set; }`** so tests can
  `await viewModel.LastSelectionLoad`. The hook stays `void` (it is the generated
  synchronous setter callback and cannot become async) — it assigns
  `LastSelectionLoad = SelectTaskAsync(value);` instead of discarding into `_`.
  - The init path needs no extra wiring: `ReloadTaskListAsync` sets `SelectedTask`, which
    fires `OnSelectedTaskChanged`, which updates `LastSelectionLoad` — so after
    `await viewModel.LoadInitialAsync()` a test can `await viewModel.LastSelectionLoad`
    to deterministically await the init-triggered load.
- **Surface the fault instead of discarding it.** The captured `LastSelectionLoad` must
  not be an unobserved faulted task. **Decision: route load faults to `StatusText` via
  the VM's existing operation-error channel** (`RunBusyAsync`-style), *not* to
  `SelectedTaskError`/`HasSelectedTaskError` — that property is reserved for "the task's
  latest run reported an error" and must not be conflated with "the UI failed to load."
  Concretely: wrap the load body so a thrown exception sets `StatusText = ex.Message`
  (consistent with `RunBusyAsync`'s `catch`) and the task still completes observed
  (faulted-then-handled, or completed-after-setting-status — either, as long as the fault
  is observed and reported, not swallowed). A test that awaits `LastSelectionLoad` after
  a forced load failure must see the error reflected in `StatusText` and must not see an
  unobserved-task-exception escape.
  - State explicitly in the implementation comment that **runtime behavior is otherwise
    unchanged**: the same properties are set in the same order on success; the only
    observable difference is that a previously-swallowed load fault now appears in
    `StatusText`.

> Why `LastSelectionLoad` rather than "return the task from the hook": the hook is the
> generated `partial void` — it cannot return a value. A captured property is the
> idiomatic way to expose the in-flight task without changing the generated signature,
> and it naturally covers **both** the test-assignment and init-assignment paths with
> one seam.

### 2. Rewrite every polling call site to await (no time cap anywhere)

Using the inventory above, replace each `WaitHelpers` call with a deterministic await.
Every `[Fact]` must keep asserting the same behavior — this is a wiring change, not a
coverage change.

- **`MainWindowViewModelTests.Status.cs`**
  - `:48` (`LoadRunHistoryAsync_CompletedRun_AllStagesShowComplete`): the test assigns
    `viewModel.SelectedTask = Assert.Single(viewModel.Tasks)`. Replace the assignment +
    `WaitUntilAsync(...)` with `await viewModel.SelectTaskAsync(task)` **or** keep the
    assignment and `await viewModel.LastSelectionLoad`. Prefer the explicit
    `await viewModel.SelectTaskAsync(task)` where the test controls the assignment — it
    reads as "do the load, then assert," with no derived-property guessing.
  - `:108` (`LoadRunHistoryAsync_MidPipelineFlagged_…`): same shape — assign + await.
- **`MainWindowViewModelTests.cs`** (9 sites)
  - `:153, :159` (select broken, then clean, in `SelectingTask_SurfacesError…`): each
    follows a `SelectedTask = …` assignment → replace each `WaitUntilAsync` with
    `await viewModel.LastSelectionLoad` (or convert the assignment to
    `await viewModel.SelectTaskAsync(...)`).
  - `:178, :188, :190, :199, :201` (`StartingRunOnPreviouslyFailedTask_…`): this test
    deliberately navigates `SelectedTask = null` then back, polling the metric label as
    a "load settled" signal between hops. Replace **each** assignment-then-poll pair with
    `await viewModel.SelectTaskAsync(value)` (covers `null` too — `SelectTaskAsync(null)`
    runs the null branch that sets `"No run history"`). The structure becomes a clean
    sequence of awaited selections with assertions between them; the metric-label and
    `HasSelectedTaskError` checks become **post-await assertions**, not poll conditions.
  - `:19, :216` (`SelectStageCommand_…`, `RevealStageArtifacts…`): both follow
    `await viewModel.LoadInitialAsync()` and poll `TraceEntries.Count`. Replace with
    `await viewModel.LastSelectionLoad;` (the init-path load populates trace entries),
    then assert the count directly.
- **`ConfigInitEmptyStateUiTests.cs:70`** (`[AvaloniaFact]`): the wait follows a real
  mouse-click that runs `CreateConfigCommand`. The test already resolves the button;
  replace the click-then-`WaitUntilWithDispatcherAsync(!NeedsInitialization)` settle with
  `await viewModel.CreateConfigCommand.ExecuteAsync(null);` followed by a **single**
  `Dispatcher.UIThread.RunJobs();`. (Driving via `ExecuteAsync` is the deterministic
  await; keep one real click earlier in the test if it is asserting click wiring, but the
  *settle* is the command await, not a poll.) No loop, no 1 s cap.
- **`KeySetupPanelUiTests.cs:132,143`** (`[AvaloniaFact]`): each follows
  `vm.SelectedTask = vm.Tasks[i]`. Replace with `await vm.LastSelectionLoad;` (markdown is
  set in the selection load) then assert `SelectedTaskMarkdown.Contains(...)`, plus a
  single `Dispatcher.UIThread.RunJobs();` if any binding-driven assertion needs the
  dispatcher flushed.

**Dispatcher-settle helper decision.** A genuine *single* `Dispatcher.UIThread.RunJobs()`
flush is still legitimate for `[AvaloniaFact]` binding settle — but it is a **one-line
call, not a loop and not time-bounded**, so it does **not** need `WaitHelpers`. Inline
`Dispatcher.UIThread.RunJobs();` at the (≤3) sites that need it. **No sanctioned helper
survives** — `WaitHelpers` is deleted entirely (step 4). If, while implementing, a real
need for a *non-looping* shared flush emerges, a one-line helper named to make its
non-looping nature obvious (e.g. `FlushDispatcher()` that calls `RunJobs()` once) may
live in a test-support file, but it must contain **no loop and no `Task.Delay`** and must
not be named `WaitHelpers`/`WaitUntil*`. Default: inline.

### 3. Ban the anti-pattern (build error on reintroduction)

Add member-level entries to `tests/VisualRelay.Tests/BannedSymbols.txt` (the `M:`
doc-comment-ID form; method parameters spelled out per BannedApiAnalyzers' format). Both
helpers are removed in step 4, so ban both:

```text
M:VisualRelay.Tests.WaitHelpers.WaitUntilAsync(System.Func{System.Boolean});Do not poll for an async operation's side effects on a wall-clock budget — it false-fails under CPU load (the 1 s cap). Await the operation Task directly (e.g. await viewModel.LastSelectionLoad / await viewModel.SelectTaskAsync(task), or await the IAsyncRelayCommand's ExecuteAsync). See llm-tasks/harness-await-not-poll-async-tests and the deflake doctrine.
M:VisualRelay.Tests.WaitHelpers.WaitUntilWithDispatcherAsync(System.Func{System.Boolean});Do not poll on a wall-clock budget. Await the operation Task; for [AvaloniaFact] binding settle use a single Dispatcher.UIThread.RunJobs() flush (no loop). See llm-tasks/harness-await-not-poll-async-tests.
```

> Implementation note: confirm the exact doc-comment ID against the actual signatures
> when authoring (both methods take a single `System.Func{System.Boolean}` today). If
> step 4 deletes the type entirely, these `M:` rules are effectively
> "this symbol may never be reintroduced" — a banned member ID still matches if someone
> re-adds the method, which is the regression tripwire we want. Keep both lines even
> though the type is gone, so re-creating `WaitHelpers.WaitUntilAsync` fails the build.

**False-positive rationale (state it in the spec/PR):** the ban names a
**project-owned** symbol (`VisualRelay.Tests.WaitHelpers.*`). It does **not** ban any BCL
primitive, so `Stopwatch` and `Task.Delay` — used legitimately by the watchdog/timeout
tests — are untouched. RS0030 only fires on a literal call to the named member.

**Optional complementary guard (defense in depth).** Add a source-scan `[Fact]` to
`SplitGuardVerificationTests.Conventions.cs` (model:
`NoTestFile_CallsEnvironmentSetEnvironmentVariable`, `:136`) named e.g.
`NoTestFile_ReintroducesConditionPollHelper`: walk every `*.cs` in the tests dir, skip
the convention file itself, and FAIL if any source declares a polling helper
(`for (... < 50; ...)` + `Task.Delay` + `Assert.True(condition` pattern) **or** contains
the literal `WaitUntilAsync`/`WaitUntilWithDispatcherAsync`. This catches a hand-rolled
re-creation (renamed helper that dodges the symbol ban) and self-documents the rule.
Keep it cheap and string-based; it is a tripwire, not the primary mechanism.

### 4. Retire `WaitHelpers`

Once **zero** call sites remain (verify with a fresh
`grep -rn "WaitHelpers\|WaitUntilAsync\|WaitUntilWithDispatcherAsync" tests/` returning
only the BannedSymbols lines / the optional convention literals):

- Delete `tests/VisualRelay.Tests/WaitHelpers.cs`.
- Delete `tests/VisualRelay.Tests/WaitHelpersTests.cs` (it only validated the helper).
- Remove the now-stale "WaitUntilAsync is provided by WaitHelpers." comment lines in
  `MainWindowViewModelTests.cs:276`, `ConfigInitEmptyStateUiTests.cs:84`,
  `KeySetupPanelUiTests.cs:53` (and `MainWindowViewModelTests.Status.cs` if present).

(No sanctioned `RunJobs`-flush helper survives by default — see step 2.)

### 5. Update the doctrine

In `llm-tasks/DONE-deflake-timing-sensitive-tests.md`, **supersede** the line endorsing
*"generous-but-bounded waits driven by condition polling rather than fixed sleeps."*
Replace/annotate it so the standard is unambiguous: **await the real operation `Task`
(no polling helper); a `Func<bool>` poll on a wall-clock budget is itself a flake source
under CPU load — it is banned (see `harness-await-not-poll-async-tests`).** For
`[AvaloniaFact]` binding settle, a single non-looping `Dispatcher.UIThread.RunJobs()`
flush is the only sanctioned "settle." Keep the rest of the deflake doctrine (fake clocks,
isolation collections) intact; only the condition-polling endorsement is corrected.

## Tests

- **Determinism proof (the whole point):** the rewritten tests await the real load Task,
  so there is **no 1 s cap anywhere** and no wall-clock dependence. Run the full suite
  **N=5 consecutive** times under load (reuse the loop the deflake task established),
  including on a `/tmp` native-disk worktree, and confirm
  `LoadRunHistoryAsync_CompletedRun_AllStagesShowComplete` (and its siblings) are green
  every run. The acceptance is "green under repeated full-suite load," not "green in
  isolation" (it always was).
- **Exception-surfacing coverage:** add a `[Fact]` proving the previously-swallowed load
  fault is now surfaced — force `SelectTaskAsync` to fault (e.g. a task whose input/run
  read throws, or an injected failure on the load path), `await viewModel.LastSelectionLoad`
  (or `await SelectTaskAsync(...)`), and assert the fault appears in `StatusText` and that
  **no unobserved-task exception escapes** (the task completes observed). This is the test
  the prompt requires to cover the VM exception-surfacing decision.
- **Ban fires on reintroduction:** confirm RS0030 (`severity = error`) **fails the build**
  if `WaitHelpers.WaitUntilAsync` is re-added and called — verify by temporarily
  re-introducing one call and observing the build error names the banned symbol, then
  reverting. The optional source-scan `[Fact]` must go red on a hand-rolled re-creation.
- **Helper gone:** `tests/VisualRelay.Tests/WaitHelpers.cs` and `WaitHelpersTests.cs` no
  longer exist; a repo-wide grep for `WaitUntilAsync`/`WaitUntilWithDispatcherAsync`
  returns only the BannedSymbols entries (and the optional convention-test literals).
- **Watchdog/timeout tests untouched:** `Stopwatch`/`Task.Delay` remain in
  `SwivalSubagentRunnerWatchdogTests.*`, `*.Timeout.cs`, `RelayQueueControllerParallelTests`,
  `FdLeakTests`, etc. — confirm none were changed and the ban does not name them.
- **No `run-task` smoke needed:** this is a UI/view-model-only change — it does not touch
  the process/exec/git layer. (The VM exception-surfacing is covered by the unit test
  above; no end-to-end `run-task` is required.)

## Done when

- The selection load is **awaitable**: `MainWindowViewModel` exposes
  `internal Task SelectTaskAsync(TaskRowViewModel?)` and `internal Task? LastSelectionLoad`,
  the only fire-and-forget site (`OnSelectedTaskChanged`) captures the task instead of
  discarding it into `_`, and the already-awaited `Authoring.cs` caller still works.
- The previously-swallowed load fault is **surfaced** (to `StatusText`, the VM's existing
  operation-error channel — not conflated with `SelectedTaskError`/`HasSelectedTaskError`),
  proven by a dedicated `[Fact]`; production behavior is otherwise byte-for-byte unchanged.
- **Every** `WaitHelpers` call site (all six files; 9 + 2 `WaitUntilAsync`, 1 + 2
  `WaitUntilWithDispatcherAsync`) is rewritten to `await` the real Task (or, for the one
  async-command settle, `await ExecuteAsync` + a single `RunJobs()` flush). No
  `WaitUntil*` poll remains; no test relies on a 1 s budget.
- `WaitHelpers.WaitUntilAsync` and `WaitUntilWithDispatcherAsync` are **banned** in
  `BannedSymbols.txt` (`M:` member entries with the await-the-Task message); RS0030
  fails the build if either is reintroduced. `Stopwatch`/`Task.Delay` are **not** banned.
- `WaitHelpers.cs` and `WaitHelpersTests.cs` are **deleted**; the stale "provided by
  WaitHelpers" comments are removed.
- `llm-tasks/DONE-deflake-timing-sensitive-tests.md` no longer endorses condition-polling
  for awaitable operations; it directs authors to await the real `Task` (single
  `RunJobs()` flush for `[AvaloniaFact]` settle only).
- Full suite green **5× consecutively** on both the repo checkout and a `/tmp` worktree,
  with the formerly-flaky test deterministic under load.
