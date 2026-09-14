# Keep /state responsive and accurate during bootstrap and cancel

Two gaps make the control API misleading at exactly the moments an operator
polls it. During bootstrap, which can take minutes because it runs each test
command candidate and the formatter check, `/state` reports `isBusy=false`
and the idle queue line ("0 pending"), so a script cannot tell bootstrap is
running and `run-all` is not blocked. And twice on the Windows arm `/state`
did not answer within 20 seconds, once right after `cancel`.

## Evidence

- Mac, 2026-09-14, 0.377, ThreeMammals/Ocelot: `POST /command/bootstrap` at
  07:37:55; `/state` at 07:38:24 and 07:38:59 read `busy=false status: 0
  pending` while `dotnet test Ocelot.Samples.slnx` and then `dotnet format
  Ocelot.slnx --verify-no-changes` were running; the bootstrap sentence
  appeared at about 07:39:15.
- Windows arm, 2026-09-14, 0.372, i18next: `curl -m 20 /state` timed out at
  about 07:40:20, right after `POST /command/cancel` at 07:40:16; it answered
  in 34 ms by 07:40:51. An earlier 20 s stall happened at 07:04:14 mid stage
  11 (no trigger identified).

## Current state (researched at 37403a8a)

- `App/ViewModels/MainWindowViewModel.Bootstrap.cs:28` `CanBootstrapProject`
  is `!IsBusy && Directory.Exists(RootPath)`; `BootstrapProjectAsync`
  (`:36-56`) never sets `IsBusy` or a status before
  `ProjectBootstrapper.BootstrapAsync`, and only writes `StatusText` at the
  end (after `RefreshAsync`, whose idle branch writes the queue count).
- `/state` is built in `App/Services/ControlApi.State.cs`; the cancel command
  path is in the control API's command handlers. Where the state snapshot is
  read (UI thread dispatch or a lock shared with cancel) decides whether a
  wind-down can block it; not yet traced.

## Prescribed approach

1. Bootstrap sets `IsBusy = true` and `StatusText = "Bootstrapping: checking
   test commands"` before starting, restores `IsBusy` in `finally`, and keeps
   the final sentence as today. The existing refusals for `run-all` and
   `create-task` while busy then apply during bootstrap as well.
2. Trace `/state` after `cancel`: add a `control_request_slow` log line with
   the endpoint and elapsed time for any control request over 2 s, reproduce
   by cancelling a drain mid-stage on the Mac with a loop polling `/state`
   every second, and remove whatever synchronous wait the cancel path holds
   on the thread `/state` needs (the wind-down must not block the dispatcher).

## Tests

- `MainWindowViewModelTests.Bootstrap`: while a fake bootstrapper is held
  open, `IsBusy` is true and the status names bootstrapping; `run-all` is
  refused; after release the final sentence is shown and `IsBusy` is false.
- A control API test with a cancel whose wind-down is held open by a fake:
  `/state` still answers within a second.

## Verification (through the control API)

Mac: `open-folder` on a .NET repo with a slow suite, `bootstrap`, and poll
`/state` every 5 s: expect `isBusy=true` and the bootstrapping status until
the final sentence. Then start a drain, `cancel` mid-stage, and poll `/state`
every second with `curl -m 2`: no timeouts, and no `control_request_slow`
line over 2 s in the app log.

## Out of scope

Bootstrap speed itself.
