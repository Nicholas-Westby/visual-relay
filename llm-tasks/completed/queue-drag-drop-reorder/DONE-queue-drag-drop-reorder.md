# Replace the queue's Up / Down buttons with drag-and-drop reordering, and make "Run All" honor that order

The left QUEUE panel reorders tasks with two **Up** / **Down** buttons that nudge the selected
task one slot at a time. Replace that with direct drag-and-drop: grab a task card and drop it where
you want it, and the two buttons go away. **And close the gap that makes reordering feel broken:**
today the manual order changes only the *visible* list — **"Run All" ignores it** and executes in
alphabetical order. After this task, Run All plans/executes in exactly the order shown in the app.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; if a snippet has drifted, re-read the file and adapt.

**The buttons.** `src/VisualRelay.App/Views/Controls/QueuePanel.axaml` has a horizontal
`StackPanel` holding the two buttons:

```xml
<StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="6">
  <Button Command="{Binding MoveUpCommand}" Classes="primary" Padding="10,4" MinHeight="28" Content="Up"/>
  <Button Command="{Binding MoveDownCommand}" Classes="primary" Padding="10,4" MinHeight="28" Content="Down"/>
</StackPanel>
```

This whole `StackPanel` is removed (mind the parent grid's `RowDefinitions`/`RowSpacing` so the
layout doesn't leave a gap — re-read the surrounding `<Grid Row="0" RowDefinitions="Auto,Auto,Auto" …>`).

**The list to make draggable.** Same file, the `ListBox x:Name="TaskQueueList"`:

```xml
<ListBox Grid.Row="1" x:Name="TaskQueueList" Margin="10,0,10,8"
         IsVisible="{Binding !NeedsInitialization}"
         ItemsSource="{Binding Tasks}"
         SelectedItem="{Binding SelectedTask}">
```

Single selection, `ItemsSource` bound to `Tasks`, item template is a `TaskRowViewModel` card.

**What the buttons do (view-model side).**
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs`, `MoveUp` / `MoveDown` operate on
the in-memory `Tasks` `ObservableCollection`:

```csharp
var index = Tasks.IndexOf(SelectedTask);
if (index > 0) { Tasks.Move(index, index - 1); }      // MoveUp
…
if (index >= 0 && index < Tasks.Count - 1) { Tasks.Move(index, index + 1); }  // MoveDown
```

`CanMoveUp` / `CanMoveDown` (in `MainWindowViewModel.Helpers.cs`) gate on `HasSelection()` =
`SelectedTask is not null && !IsBusy && !ShowArchive`.

**Why Run All ignores all of that — the gap to close.**
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs`, `DrainQueueAsync` spins up a
**fresh** controller and refreshes it from disk, never consulting the VM's reordered `Tasks`:

```csharp
var controller = new RelayQueueController(RootPath, new GuiTaskRunner(…), …);
await controller.RefreshAsync();
// Wire pause.
if (PauseRequested) controller.RequestPause();
var results = await controller.DrainAsync();
```

`src/VisualRelay.Core/Queue/RelayQueueController.cs`:
- `public ObservableCollection<RelayTaskItem> Tasks { get; } = [];`
- `RefreshAsync` does `Tasks.Clear(); foreach (var task in await _repository.ListPendingAsync(...)) Tasks.Add(task);` — disk order.
- `DrainAsync` executes **in `Tasks` order**: it starts `var queue = Tasks.Where(task => !task.NeedsReview).ToList();`, Phase 1 planning iterates that `queue`, and Phase 2 executes serially (`var task = queue[0]; queue.RemoveAt(0);`).
- It already exposes reorder hooks: `public void MoveUp(string taskId)` / `MoveDown(string taskId)` that `Tasks.Move(...)`.

So **the controller already drains in its own `Tasks` order** (the existing test
`RelayQueueControllerTests.DrainAsync_UsesManualOrderAndPausesAtTaskBoundary` proves it via
`controller.MoveDown("alpha")`). The only missing link is feeding the GUI's visible order into the
controller before draining.

**Default orders already match (so this is a no-op until you drag).** The VM list
(`RelayTaskRepository.ListAsync`) and the controller list (`ListPendingAsync`, which calls
`ListAsync(includeNeedsReview:false)`) both end with
`.OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase)`. Identical default order ⇒ aligning
them changes nothing unless the user has manually reordered.

**Order is in-memory only — keep it that way.** Nothing persists order; on refresh/restart the
alphanumeric disk order returns. This repo is shared between a host user and a VM user, so
persisting a manual order into the repo tree would cause cross-machine churn. **Do not add order
persistence.**

**Wiring that will break when the buttons' commands are removed — fix it.**
`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs`:

```csharp
Tasks.CollectionChanged += (_, _) =>
{
    MoveUpCommand.NotifyCanExecuteChanged();
    MoveDownCommand.NotifyCanExecuteChanged();
};
```

Removing `MoveUpCommand`/`MoveDownCommand` won't compile here. Remove/repurpose this block, and
delete now-dead `CanMoveUp`/`CanMoveDown`/`HasSelection` if nothing else uses them (grep first —
`HasSelection` is currently only used by those two).

**Stack & precedent.** Avalonia **12.0.4**. No existing drag-drop anywhere in the app, so you're
establishing the pattern — keep it self-contained.

## What to build

TDD — write the failing tests first (test the reorder seams, not the gesture).

### 1. A testable reorder seam on the view model
Extract the reorder into one method, e.g. `internal void MoveTask(int fromIndex, int toIndex)`, that
bounds-checks, calls `Tasks.Move(fromIndex, toIndex)`, keeps the moved row selected, and is a no-op
when `IsBusy` or `ShowArchive`. Re-express `MoveUp`/`MoveDown`'s old behavior through it (then delete
the commands). Keeps reordering unit-testable with no UI.

### 2. Drag-and-drop on the queue ListBox
Wire dragging a `TaskRowViewModel` card to a new position and call `MoveTask(from, to)`. Pick the
cleanest Avalonia 12 mechanism (e.g. `PointerPressed`/`PointerMoved` threshold → `DragDrop.DoDragDrop`
with a custom data format, computing the target index from the cursor in `DragOver`/`Drop`; or an
attached behavior). Requirements:
- Disable drag reordering while `IsBusy` or `ShowArchive` (mirror the old gate).
- The dragged row stays selected after the drop.
- Clear drag affordance / insertion feedback so it's discoverable now that the labelled buttons are gone.
- Gesture/visual code in the view layer; route the mutation through `MoveTask` so logic stays testable.

### 3. Remove the buttons and clean up
Delete the Up/Down `StackPanel`, the `MoveUp`/`MoveDown` commands, their `CanExecute` helpers if
unused, and the `CollectionChanged` notify block above. Leave no dead members (InspectCode is
enforced repo-wide).

### 4. Make "Run All" execute in the visible order
- Add `public void ApplyOrder(IReadOnlyList<string> orderedIds)` to `RelayQueueController` that
  **stable-reorders** its `Tasks` by each item's position in `orderedIds`; ids not present in
  `orderedIds` keep their relative order at the end. In-memory only, no persistence. Sketch:
  ```csharp
  public void ApplyOrder(IReadOnlyList<string> orderedIds)
  {
      var rank = new Dictionary<string, int>(StringComparer.Ordinal);
      for (var i = 0; i < orderedIds.Count; i++) rank.TryAdd(orderedIds[i], i);
      var sorted = Tasks
          .Select((t, i) => (t, key: rank.TryGetValue(t.Id, out var r) ? r : int.MaxValue, orig: i))
          .OrderBy(x => x.key).ThenBy(x => x.orig)   // stable
          .Select(x => x.t).ToList();
      Tasks.Clear();
      foreach (var t in sorted) Tasks.Add(t);
  }
  ```
- In `DrainQueueAsync`, immediately **after `await controller.RefreshAsync();`**, align the
  controller to the app's visible order:
  ```csharp
  controller.ApplyOrder(Tasks.Select(t => t.Id).ToList());
  ```
  (then the existing pause-wire and `await controller.DrainAsync();` run in that order). Since the
  controller already drains in `Tasks` order, this one line is the whole fix.
- **GUI drain only.** Leave any CLI/headless drain path on disk order — there is no "app order" there.

## Tests / verification
- **Reorder seam (red→green):** `MainWindowViewModelTests` — build a queue of 3+ tasks, select one,
  call `MoveTask(from, to)`, assert `Tasks.Select(t => t.Id)` is the expected order and the moved
  task is still `SelectedTask`. Replaces the old `CanMoveUp`/`CanMoveDown` CanExecute tests (remove them).
- Guard: `MoveTask` is a no-op when `ShowArchive` (and/or `IsBusy`).
- **Run All order (red→green):** in `RelayQueueControllerTests`/drain tests, refresh a controller,
  call `ApplyOrder` with a non-default id order (and an unknown id to prove it sorts last, stable),
  then `DrainAsync` and assert tasks ran in that order. This composes with — and must not break —
  the existing `DrainAsync_UsesManualOrderAndPausesAtTaskBoundary` (uses `MoveDown`, unaffected).
- If the existing DrainQueue VM-level tests make it cheap, assert `DrainQueueAsync` calls
  `ApplyOrder` with the visible order; otherwise the controller-level test plus the one-line wiring
  is sufficient (note this in the PR).
- Manual smoke (note in PR): drag three tasks into a custom order, click **Run All**, confirm they
  execute top-to-bottom in that order; with no reorder, order is unchanged.

## Decisions (settled)
1. **In-memory reorder only — no persistence.** *Why:* matches today's behavior and avoids host/VM
   repo conflicts on shared state.
2. **Run All honors the visible order via `controller.ApplyOrder(...)` at drain time**, not by
   persisting order or refactoring the controller's drain loop (it already drains in `Tasks` order).
   *Why:* smallest change that makes the feature whole; default order is untouched.
3. **Reorder logic lives in VM (`MoveTask`) + controller (`ApplyOrder`); gestures live in the view.**
   *Why:* both reorder paths stay unit-testable without driving pointer input.
4. **Both buttons are removed** (not hidden). *Why:* drag-drop fully replaces them.

## Notes
- **Known limitation (acceptable, document in PR):** manual order is per-drain. After a drain
  completes or pauses, `RefreshTasksAfterDrainAsync()` → `ReloadTaskListAsync()` reloads the VM list
  in alphanumeric order, so the reorder doesn't survive the run; a *resumed* Run All re-derives order
  from the then-current visible list. Persisting order across restarts remains out of scope (shared
  host/VM repo). If the user later wants sticky order, that's a separate task.
- **Coordination:** `queue-drag-drop-reorder` and `readable-status-and-hf-gate-banner` both edit
  `QueuePanel.axaml`, in different regions (this task: the Up/Down `StackPanel` + the `ListBox`; the
  other: the bottom status `Border` + a new HF banner). Locate your anchor by the quoted string and
  re-read if the file drifted because the other task landed first.
