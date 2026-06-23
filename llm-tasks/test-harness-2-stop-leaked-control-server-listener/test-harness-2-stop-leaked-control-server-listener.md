# Stop the test suite from starting (and leaking) the `vr-control` listener

The headless UI tests boot the real App, which spins up a loopback HTTP control server that no
test ever tears down. The suite stands up undisposed `vr-control` listeners on ephemeral ports —
fd/port churn, output noise, and a leak vector adjacent to the verify-gate hang.

## What actually happens (evidence)

A full `dotnet test` run prints three of these, on different ephemeral ports:

```
vr-control: listening on http://127.0.0.1:59976
vr-control: listening on http://127.0.0.1:59978
vr-control: listening on http://127.0.0.1:59980
```

i.e. three App instances each started a `ControlServer` listener during the run, and nothing
disposed them. When the App boots, `App.OnFrameworkInitializationCompleted`
(`src/VisualRelay.App/App.axaml.cs`) builds
`ControlServerOptions.FromEnvironment(new ProcessEnvironmentAccessor())` and constructs a
`ControlServer` (`src/VisualRelay.App/Services/ControlServer.cs`), whose `Start` logs
`vr-control: listening on http://127.0.0.1:{options.Port}`. Headless UI tests boot that same App,
so they start real listeners.

These listeners are in-process (they die with the test host), so they are not themselves the
~8-minute verify-gate hang investigated in `test-harness-1` — but a test suite should not be
standing up loopback HTTP servers it never disposes. It churns ephemeral ports/fds, can flake
under port pressure, and is exactly the class of "spawned-but-not-reaped" resource the gate hang
came from.

There is already an off switch — `ControlServerOptions.FromEnvironment`
(`src/VisualRelay.App/Services/ControlServerOptions.cs`) sets `Enabled: !disabled` from
`env.GetEnvironmentVariable("VR_CONTROL_DISABLE") == "1"` — but only one test uses it
(`tests/VisualRelay.Tests/ControlServerTests.cs` sets `["VR_CONTROL_DISABLE"] = "1"`); the
headless-App path does not.

## What to build

- Ensure the control server never starts during the suite. Preferred: have the **shared headless
  test App** set `VR_CONTROL_DISABLE=1` (or inject a disabled `ControlServerOptions`) before the
  App boots, so no listener is created. Locate the shared headless App bootstrap used by the
  `[AvaloniaFact]` UI tests (search the test project for the headless `AppBuilder` / test-app
  setup — prior art references a `HeadlessTestApp`) and gate the control server there. Note
  `App.axaml.cs` reads the **real** process env via `ProcessEnvironmentAccessor`, so setting the
  variable in the test process is honored.
- For tests that genuinely need the control server (e.g. the ControlApi / ControlServer tests),
  start it explicitly and **dispose it deterministically**. Confirm `ControlServer` stops its
  listener on dispose (it should be `IDisposable` and close the `HttpListener`); add that if it
  is missing.
- Add a regression guard: a test that boots the headless App and asserts no `vr-control` listener
  remains (control server disabled / no socket listening on its port), so this cannot silently
  return.

## Environment notes (Tart VM vs host)

- The control server starts on **any** App boot, host or VM — env-independent. Reproduce on the
  VM with a plain `./test.sh` run and grep stderr for `vr-control: listening`; you should see the
  leaked listeners there too.
- Acceptance here is a deterministic in-process assertion (no listener after headless App
  teardown), **not** reproducing the host-specific multi-minute gate hang — that is handled
  robustly by `test-harness-1`. This task is hygiene plus removing the most visible leak source.
- nono is on PATH in the VM but is not needed for this task.

> **Sequencing — second (1 → 2 → 3).** `test-harness-1` makes any leaked descendant non-fatal at
> the harness level; this task removes the known listener leak so the suite stops standing up
> undisposed loopback servers. Independent of `test-harness-3`. The implementer sees one task at
> a time; this file is self-contained.

## Done when

- A full `./test.sh` / `dotnet test` run starts no `vr-control` listener (no
  `vr-control: listening` lines), or any deliberately-started one is disposed within the test that
  started it.
- The headless App used by UI tests has the control server disabled by default.
- A regression test asserts no listener survives headless App teardown.
- `ControlServer` stops its listener on dispose, verified by a test (if not already the case).
- `./visual-relay check` green; files under 300 lines; Conventional Commit (e.g.
  `fix(tests): disable vr-control listener in the headless test app`).
