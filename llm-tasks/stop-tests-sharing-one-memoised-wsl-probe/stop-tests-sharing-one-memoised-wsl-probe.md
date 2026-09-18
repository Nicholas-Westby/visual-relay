# Stop tests sharing one memoised WSL probe

`WslContext` memoises the distro probe in three process-wide statics: `_prober`,
`_probed` (a `Lazy<Task<WslContext?>>`) and `_override`. That is right for the
app, which should probe once per process. It is a trap under a parallel test
run, because a test that sets or clears any of them changes what an unrelated
test on another thread resolves, and nothing tells either of them it happened.

This has cost real time once already. It is not urgent, because the six tests it
caught now state their host instead of resolving one. It is filed rather than
fixed because a quiet repair inside a commit about something else would leave
nobody able to point at the change that did it or the test that proves it.

## Evidence

Windows PC, 2026-09-18, on 0.412. Six `MainWindowViewModelInitTests` facts
failed in the full suite and passed alone.

- Frequency tracked a product change, not load: 1 run in 2 on 0.403, then 2 runs
  in 2 on 0.412, then **12 of 12 passing** with that class run alone.
- The failures were FAST: `CreateConfig_SetsValidatingStatusBeforeValidation`
  failed in **12 ms** in the suite and took **4 seconds** passing in isolation.
  That is the signature that found it. A timeout is slow; a contended temp
  directory is slow; twelve milliseconds means nothing ran.
- Cause: `CreateConfigAsync` resolves the sandbox host and RETURNS when
  `ProjectBootstrapper.RefusalFor` refuses, writing no config. Those tests
  injected no host, so they fell through to `SandboxHost.Current` — a real
  wsl.exe probe reading the statics above. With another test's value in place
  saying "no distro", the refusal fired.
- The same suite on macOS: two full runs, 5054 tests, **0 failed**. Not a
  failure to reproduce — macOS has no distro to lose, so `Current` is `Local`,
  the refusal cannot fire, and the platform is structurally incapable of
  showing it.

The refusal itself is correct and was added deliberately (a Windows host with no
distro previously ran the operator's command through `cmd.exe`, unsandboxed).
What it exposed is that tests were depending on a probe they never meant to make.

## Current state

`tests/VisualRelay.Tests/MainWindowViewModelInitTests.cs` now passes
`SandboxHostResolver = LocalHost` everywhere, so those six no longer resolve
anything. The statics are untouched and the trap is intact for the next test
that resolves a host for real.

## Prescribed approach

Make the shared probe impossible to observe across tests, rather than asking
every future test to remember to inject a host. Options, cheapest first:

1. An `AsyncLocal` or per-scope holder for the override so a test's value cannot
   be seen by another thread.
2. A test seam that hands each test its own resolver instance, with the
   process-wide memoisation kept only for the app's own composition root.
3. Leaving the statics and adding a guard test that fails when a type under
   `tests/` writes to them outside a scope that restores them.

Prefer whichever makes the WRONG thing hard rather than the right thing
mandatory. A convention that every test must inject a host is the state we are
in now, and it held only until someone wrote a test that did not.

## Tests

- A fact that proves the race: two tests resolving the host concurrently, one
  setting an override, with the other's answer unaffected. It must fail against
  today's statics, or it is not testing the thing.
- Keep the existing init facts green without their explicit `LocalHost`, since
  the point is that they should no longer need it.

## Verification

The race only shows on Windows, because macOS has no distro and the refusal
cannot fire. So the proving test has to drive the resolver directly with an
injected prober rather than relying on a real probe, and it should then fail on
any platform.

## Out of scope

- The refusal in `CreateConfigAsync`. It is correct and stays.
- `TestModuleInitializer.RedirectedXdgConfigHome`, which has its own documented
  process-wide window. Same family, different variable; do not bundle them.

## Rejected alternatives

- **Serialise the affected tests.** Hides the trap and slows the suite, and the
  next test to resolve a host would still be exposed.
- **Drop the memoisation.** The probe is six wsl.exe steps; running it per call
  would make the app slow for a test-only problem.
