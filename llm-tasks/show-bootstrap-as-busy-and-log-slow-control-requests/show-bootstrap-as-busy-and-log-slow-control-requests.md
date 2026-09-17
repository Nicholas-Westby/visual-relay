# Show bootstrap as busy in /state, and log slow control requests

Scripts drive Visual Relay through its control API and poll `GET /state` to
learn when the app is ready for the next command. Two gaps were seen on
2026-09-14. During bootstrap, which can take minutes because it runs each test
command candidate and the formatter check, `/state` reports `isBusy=false` and
the idle queue line ("0 pending"), so a script cannot tell that bootstrap is
running, and `run-all` is not refused. That one is fixed here. And twice on
the Windows arm `/state` took more than 20 seconds to answer, once right after
`cancel`. Nobody can reproduce that yet, so this task does not hunt for it: it
adds the one log line that will say which request was slow the next time it
happens.

## Evidence

- Mac, 2026-09-14, 0.377, ThreeMammals/Ocelot: `POST /command/bootstrap` at
  07:37:55; `/state` at 07:38:24 and 07:38:59 read `busy=false status: 0
  pending` while `dotnet test Ocelot.Samples.slnx` and then `dotnet format
  Ocelot.slnx --verify-no-changes` were running; the bootstrap sentence
  appeared at about 07:39:15.
- Windows arm, 2026-09-14, 0.372, i18next: `curl -m 20 /state` timed out at
  about 07:40:20, right after `POST /command/cancel` at 07:40:16; it answered
  in 34 ms by 07:40:51. An earlier 20 s stall happened at 07:04:14 mid stage
  11, with no trigger identified.

## Current state (researched at f8a0d07b)

- `App/ViewModels/MainWindowViewModel.Bootstrap.cs:28` `CanBootstrapProject`
  is `!IsBusy && Directory.Exists(RootPath)`. `BootstrapProjectAsync`
  (`:36-56`) never sets `IsBusy` or a status before
  `ProjectBootstrapper.BootstrapAsync` (`:41`); it writes `StatusText` once,
  after the closing `RefreshAsync`, because that refresh's idle branch ends by
  writing the queue count.
- A command that is disabled answers 409 with its reason
  (`App/Services/ControlApi.cs:90-95`, `ControlApi.DisabledReason.cs`);
  `RunBlocker.Busy` (`MainWindowViewModel.RunBlockers.cs:42`) is what refuses
  the run commands, and `create-task` checks `IsBusy` itself
  (`ControlApi.Tasks.cs:37`). All of that starts to apply to bootstrap the
  moment bootstrap sets `IsBusy`.
- `/state` is serialized on the UI thread (`ControlApi.State.cs:18`) and every
  command runs there too (`ControlApi.cs:83`), so whatever holds that thread
  stalls both. `CancelRun` (`MainWindowViewModel.Cancel.cs:30`) only sets
  flags; what held the thread for 20 seconds is not known.
- One handler wraps every route: `App/Services/ControlServer.Routing.cs:13-26`
  `BuildHandler`. The control server already writes operator-facing lines to
  stderr with a `vr-control:` prefix (`ControlServer.cs:41`, `:71`, `:75`;
  documented at `AGENTS.md:78`).

## Prescribed approach

1. Bootstrap is busy. `BootstrapProjectAsync` sets `IsBusy = true` and
   `StatusText = "Bootstrapping: checking test commands"` before it starts,
   and sets `IsBusy = false` in a `finally` that runs before the closing
   `RefreshAsync`, so the final sentence is still written after the refresh as
   today. The existing refusals for the run commands and `create-task` then
   cover bootstrap with no further change. `AGENTS.md`'s `bootstrap` entry
   gains: `/state.isBusy` is true for as long as bootstrap runs.
2. Slow requests are logged. `BuildHandler` times every request and, for one
   that took longer than 2 seconds, writes `vr-control: slow request <method>
   <path> took <n> ms` to stderr after the response. `GET /screenshot` is
   exempt: it renders and is slow by nature. The threshold is a named
   constant. `TROUBLESHOOTING.md` gains a short entry: what the line means,
   and that a slow `/state` points at work holding the UI thread at that
   moment.
3. Nothing else about the stall. If the line shows up in a real run, the
   request it names and the `run.log` lines around its time are the evidence
   for a follow-up task.

## Tests

- `MainWindowViewModelTests.Bootstrap`: while a fake bootstrapper is held
  open, `IsBusy` is true and the status names bootstrapping; `run-all` and
  `create-task` are refused with the busy reason; after release `IsBusy` is
  false and the final sentence is the one shown; a bootstrap that throws also
  ends with `IsBusy` false.
- `ControlServerKestrelHandlerTests` (the in-memory handler from
  `BuildHandler`, with an injected clock and a captured log writer; there is
  also `ControlApiBootstrapStatusTests` for the first item's API side): a
  request held for 3
  seconds logs one `slow request` line naming its method and path; one that
  takes 100 ms logs nothing; a slow `/screenshot` logs nothing.
- Watch each fail first.

## Verification (through the control API)

Mac: `open-folder` on a repository whose suite takes a minute or more,
`bootstrap`, and poll `/state` every 5 seconds. Expected: `isBusy=true` with
the bootstrapping status until the final sentence appears, and a `run-all`
sent in between answers 409 with the busy reason. Then start a drain, `cancel`
it mid-stage, and poll `/state` every second with `curl -m 2` for a minute:
report whether any poll timed out and whether a `slow request` line appeared
on the app's stderr. Put the poll results and any such line in the commit
body.

## Out of scope

Finding or fixing the 20-second stall (no reproduction yet; the log line is
the first step); bootstrap's own speed; moving `/state` off the UI thread.

## Rejected alternatives

- Tracing the cancel path now, as the first version of this spec asked: two
  sightings on one machine and no way to trigger it make that a search without
  a test to end it.
- A separate "bootstrapping" flag in `/state`: `isBusy` is what scripts
  already poll and what the refusals already key on.
