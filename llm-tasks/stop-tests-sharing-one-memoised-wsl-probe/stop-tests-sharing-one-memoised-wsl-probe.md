# Stop tests sharing one memoised WSL probe

`WslContextResolver` (in `WslContext.cs`) memoises the distro probe in
process-wide statics: `_probed`, a `Lazy<Task<WslContext?>>` that captures the
prober it was built with, and `_override`. That is right for the app, which
should probe once per process. It is a trap under a parallel test run, because a
test that sets or clears either of them changes what an unrelated test on
another thread resolves, and nothing tells either of them it happened.

A third static has the same exposure: `_lastProbe`, which every `FromProbe` call
writes and which refusals are worded from, through `UnusableProbe`. A test that
writes an unusable probe there can change which failing check another test's
refusal names.

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

- The init facts, now split across `MainWindowViewModelInitTests.cs` and
  `MainWindowViewModelInitTests.Validation.cs`, pass
  `SandboxHostResolver = RunnableHost`: a Windows host with a stated distro on
  Windows, `SandboxHost.Local` elsewhere. It is not `Local` everywhere because on
  Windows that takes the POSIX branch and tries to start `/bin/sh`. So the six
  that failed no longer resolve anything, and the trap is intact for the next
  test that resolves a host for real.
- Two later fixes changed the resolver without touching this problem.
  `c18a1045` reads `_probed` under the lock that replaces it, and `bc5f4ae8`
  removed a separate `_prober` static by having the memo capture its prober when
  it is built. Both closed races inside the resolver; neither stops one test
  seeing another's values.
- The classes that write the statics, `WslContextResolverTests`,
  `SandboxHostResolveTests` and `NonoProfileEnsurerHostTests`, share
  `[Collection("WslContext")]`, which has no definition. They run one at a time
  against each other and in parallel with everything else, which is how a test
  elsewhere sees their values. `WslProberLoginPathTests` also calls `FromProbe`,
  from outside that collection.

## Prescribed approach

Make the shared probe impossible to observe across tests, rather than asking
every future test to remember to inject a host. Options, cheapest first:

1. An `AsyncLocal` or per-scope holder for the override so a test's value cannot
   be seen by another thread.
2. A test seam that hands each test its own resolver instance, with the
   process-wide memoisation kept only for the app's own composition root.
3. Leaving the statics and adding a guard test that fails when a type under
   `tests/` writes to them outside a scope that restores them.

Whichever is chosen has to cover all three statics, `_lastProbe` included, or
say why one of them can stay shared. The first option, as worded, covers only
the override.

Prefer whichever makes the WRONG thing hard rather than the right thing
mandatory. A convention that every test must inject a host is the state we are
in now, and it held only until someone wrote a test that did not.

## Tests

- A fact that proves the race: two tests resolving the host concurrently, one
  setting an override, with the other's answer unaffected. It must fail against
  today's statics, or it is not testing the thing.
- Leave `RunnableHost` on the init facts. Without it they fall back to
  `SandboxHost.Current`, which on Windows is a real wsl.exe probe of whatever
  machine runs the suite, so removing it would test the machine rather than this
  fix. The race fact above is the proof.

## Verification

The race only shows on Windows, because macOS has no distro and the refusal
cannot fire. So the proving test has to drive the resolver directly with an
injected prober rather than relying on a real probe, and it should then fail on
any platform. The seams exist: `UseProberForTests` installs a prober,
`ProbedForTestsAsync` reads the memo with the platform check skipped, and
`Override` is honoured on every platform. Off Windows, `TryGetCurrent` and
`TryGetCurrentAsync` answer from the override alone and never read the memo, so
a race on the memo has to be observed through `ProbedForTestsAsync`.

## Out of scope

- The refusal in `CreateConfigAsync`. It is correct and stays.
- `TestModuleInitializer.RedirectedXdgConfigHome`, which has its own documented
  process-wide window. Same family, different variable; do not bundle them.

## Rejected alternatives

- **Serialise the affected tests**, for example with a `DisableParallelization`
  definition for the `WslContext` collection. Hides the trap and slows the
  suite, and the next test to resolve a host would still be exposed.
- **Drop the memoisation.** The probe is six wsl.exe steps; running it per call
  would make the app slow for a test-only problem.
