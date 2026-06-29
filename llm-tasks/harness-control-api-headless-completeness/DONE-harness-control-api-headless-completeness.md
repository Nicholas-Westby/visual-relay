# Harness/Control API: make the loopback control surface fully drivable + screenshot-verifiable headlessly

The desktop app exposes a loopback HTTP control surface (`http://127.0.0.1:<port>/`;
`src/VisualRelay.App/Services/ControlServer.cs`, `ControlServer.Routing.cs`, `ControlApi.cs`,
`ControlApi.State.cs`, `ControlScreenshot.cs`) with `/state`, `/screenshot`, and
`POST /command/{name}` so an operator (or an automated check) can drive the app from curl
exactly as if clicking its buttons. Two gaps stop it being driven and visually verified
end-to-end. Fix both. Keep it general (key on command/tab identity, not one specific screen).

## Problem A — confirm-gated commands hang when driven via the API
Dispatch resolves an `ICommand` and fires it forget-style — `command.Execute(null)` then returns
`{ok:true}` immediately (`ControlApi.cs:58-83`, ResolveCommand at `:28-46`, the `Execute` at `:73`).
But a confirm-gated command then awaits the GUI confirmation delegate, which the real app wires to a
**modal dialog**: `MainWindowViewModel.ShowConfirmationAsync` (`MainWindowViewModel.cs:202`) is set
in `App.OnFrameworkInitializationCompleted` (`App.axaml.cs:36-37`) to a builder that calls
`await dialog.ShowDialog(owner)` (`App.axaml.cs:96-157`) and blocks awaiting a human Cancel/Confirm
click. The control server runs on the **same** non-null-delegate VM, so an API caller can never
satisfy the prompt: the command stalls (its archive/rewrite side-effect never runs) and the modal
may block the window. Headless tests dodge this by setting `ShowConfirmationAsync = null`
(e.g. `tests/VisualRelay.Tests/MainWindowViewModelMarkDoneTests.cs:20`), which the live API does not.

Confirm-gated paths in the codebase (all call `ShowConfirmationAsync`): **mark-done**
(`MainWindowViewModel.MarkDone.cs:15-23`) and **rewrite-selected**
(`MainWindowViewModel.Rewrite.cs:31-39`) — both exposed as control commands — plus
**remove-attachment** (`MainWindowViewModel.Authoring.cs:216-225`), not yet exposed but the same
trap if it ever is. Fix this at the confirmation seam so it covers every confirm-gated command, not
per-command.

**Do:** make an API-driven confirm-gated invocation **not** open the interactive dialog — treat API
invocation as pre-confirmed (and/or honor an explicit `{"confirm":true}` POST body, refusing/no-op
when a destructive command is invoked without it). Pick the cleanest plumbing, e.g. route the
confirmation to auto-resolve when driven via the control API (a confirm-aware invocation path /
injected confirmation responder), so the command runs to completion and takes effect. **Preserve the
human-GUI behavior:** an actual button click must still open the modal and honor Cancel.

## Problem B — no way to navigate to a UI state before `GET /screenshot`
`/screenshot` renders the **live** MainWindow as-is (`ControlScreenshot.cs:18-46`,
`RenderTargetBitmap.Render(window)`), so a caller can only ever capture whatever tab/state the window
already shows. There is no command to switch the **activity-panel tabs** (Run Log / Commands /
System / Input / Output — `TabControl SelectedIndex="{Binding ActivityTabIndex}"`,
`Views/Controls/ActivityColumn.axaml:50`) or the **task-detail tabs** (Markdown / Context /
Attachments — `SelectedIndex="{Binding SelectedTabIndex}"`, `Views/Controls/TaskDetailPanel.axaml:88`).
Both are plain `int` index `[ObservableProperty]`s — `ActivityTabIndex`
(`MainWindowViewModel.Layout.cs:40-41`) and `SelectedTabIndex` (`MainWindowViewModel.cs:186-187`) — so
a command can set them on the UI thread the same way `boost-turns` sets a VM property
(`ControlApi.cs:112-129`), and the next `/screenshot` will then capture that tab. So UI changes living
in a non-default tab (e.g. the Output-tab accordions) currently cannot be screenshot-verified.

**Do:** add two control commands that select a tab by identity (accept a tab name and/or index in the
POST body, mirroring `select-task`'s `{"id":...}`), e.g. `select-activity-tab` and
`select-detail-tab`, wired as property actions in dispatch (`ControlApi.cs:49-50`,
`InvokePropertyAction`) and surfaced in the `/state` `commands` map
(`BuildCommandsMap`/`IcommandNames`, `ControlApi.State.cs:75-102`, property entries at `:88-92`).
**Stretch (if feasible):** also let the API open the rewrite/confirm dialog and resolve it, so the
dialog UI itself is screenshot-verifiable — likely by modeling the pending confirmation as inspectable
VM state the view renders (so it is both screenshottable and resolvable headlessly), with a command to
confirm/cancel it; only do this if it stays clean.

## Done when
- `POST /command/mark-done` (and every other confirm-gated command, e.g. `rewrite-selected`) completes
  via the API **without** a hung/interactive dialog and **takes effect** — mark-done actually archives
  the task and it leaves the queue, observable in `/state`.
- An actual GUI button click for those commands still shows the confirmation modal and a Cancel aborts
  (human-confirm behavior unchanged).
- New tab-select commands appear in `/state`'s `commands` map and change which tab the live window
  shows, so a subsequent `/screenshot` captures that tab's content; commands key on tab identity, not
  a single hard-coded screen.
- Loopback-only binding and the optional `X-VR-Token` gate are unchanged
  (`ControlServer.cs:20`, `ControlServer.Routing.cs:15-24`) — no new remote surface.
- Headless `[AvaloniaFact]` tests cover the new behavior (a confirm-gated command run via
  `ControlApi.InvokeCommandAsync` completes and takes effect without a dialog; each tab-select command
  sets the bound tab index and is present/enabled in the `/state` `commands` map), alongside the
  existing `tests/VisualRelay.Tests/ControlApiTests.cs` patterns.
- General-purpose: no VR-repo-specific screen baked into the API. `./visual-relay check` green;
  Conventional Commits.
