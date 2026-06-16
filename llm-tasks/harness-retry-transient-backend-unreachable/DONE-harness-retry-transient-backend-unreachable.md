# Harness: retry a transient backend-unreachable preflight before flagging the task

Before each subagent run, Visual Relay runs a single ~2s **backend readiness probe**; if it comes
back not-ready, the run is **immediately flagged** with the "Can't reach the model backend" hint and
the task drops to needs-review. The probe is deliberately fail-fast (a cheap up-front check so a
genuinely-down backend fails in ~1-2s instead of burning ~36s of LLM-call retries), but a *single*
check cannot distinguish a backend that is **down** from one that is **momentarily unreachable**. In a
batch drain, tasks transition rapidly and the very first probe of a task can land in a sub-second
blip of the LiteLLM proxy.

**Observed 2026-06-16:** in a drain, `harness-inject-seams-not-global-statics` flagged at stage 5
~50 ms after it started with *"Can't reach the model backend at http://127.0.0.1:4000"* — while the
very next task in the same drain, whose run started **0.2 s later**, connected fine and ran to
completion. The backend was healthy throughout; one transient blip at the moment of that task's first
probe burned the whole task to a flag (and a manual re-run). A single recoverable connectivity hiccup
should not be task-fatal.

This task makes the **pre-run** readiness probe **retry a few times with a short backoff** before
concluding the backend is unreachable — preserving fail-fast for a genuinely-down backend, adding
**zero** latency on the happy path, and leaving the single-shot probe used by the UI status light
unchanged.

This is a **general harness-robustness change** (backend connectivity), not specific to any language,
toolchain, or repo layout — keep it that way.

## Current state (researched)

> **Freshness contract.** Line numbers/snippets are a 2026-06-16 snapshot and may have drifted.
> **Locate each anchor by searching for the quoted code, not by line number**; if a snippet no longer
> matches, re-read the file and adapt.

- The pre-run probe is invoked once, with no retry, in
  `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`:

  ```csharp
  var readiness = await _probe(cancellationToken);
  if (!readiness.IsReady)
      return new SubagentResult(string.Empty, null, false, readiness.Message);
  ```

- `_probe` is configured in `src/VisualRelay.Core/Execution/ProcessRunners.cs`:

  ```csharp
  private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
  private readonly Func<CancellationToken, Task<BackendReadiness>> _probe;
  // ...
  _probe = backendProbe ?? (token => BackendReadinessProbe.CheckAsync(ModelBackend.BaseUrl, ProbeTimeout, token));
  ```

  The `backendProbe` constructor parameter is the injection seam tests already use to supply a fake
  probe — keep it.

- `src/VisualRelay.Core/Execution/BackendReadinessProbe.cs` — `CheckAsync` is a never-throwing single
  reachability check (GET `/health/readiness`, ~2s). Its own doc-comment frames it as the cheap up-
  front probe. It is **also** used single-shot by the UI top-bar status indicator
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.cs`, search `BackendReadinessProbe.CheckAsync()`).
  That UI status poll must **stay single-shot** — a status light should reflect the instantaneous
  state, not retry.

- The flag message comes from `ErrorHintClassifier` (the `ConnectionHint` constant); the final
  failure after retries must keep producing that same actionable hint so a truly-down backend still
  tells the user to start LiteLLM.

- `RelayConfig` (`src/VisualRelay.Domain/RelayConfig.cs`) is a positional record; existing numeric
  knobs (`MaxStallRetries`, the timeout maps) show the precedent if you choose to make the retry
  count/backoff configurable (optional — see Decisions).

## What to build

Add a **bounded retry-with-backoff** around the readiness check on the **pre-run path only**:

- Introduce `BackendReadinessProbe.CheckWithRetryAsync(baseUrl, timeout, attempts, backoff, ct)` (or
  an overload) that calls the existing single-shot `CheckAsync` up to `attempts` times, returning
  ready as soon as one attempt succeeds, and sleeping `backoff` between attempts. It must remain
  **never-throwing**, and must **honor cancellation** between attempts (a paused/stopped run stops
  retrying promptly — `Task.Delay(backoff, ct)` and bail on `OperationCanceledException`). On
  exhausting all attempts it returns the same not-ready `BackendReadiness` (same hint message) the
  single check returns today.
- Make the **default `_probe`** in `ProcessRunners` use the retrying variant (e.g. 3 attempts with a
  short backoff). Do **not** change the injected-`backendProbe` behavior — when a test supplies its
  own probe, that fake is used verbatim (so tests stay in control).
- Leave the **single-shot** `CheckAsync` intact and keep the UI status-indicator call site on it.
- Keep the happy path free of added latency: a first-attempt success makes exactly **one** call and
  no delay.
- Bound the worst case so a genuinely-down backend still fails fast-ish: with connection-refused
  returning near-instantly, 3 attempts + short backoff is ~1-1.5 s; even if every attempt hits the
  full 2 s timeout it is ~6-7 s — still far below the ~36 s of LLM-call retries the probe exists to
  avoid. State the chosen numbers as named constants.

## Tests

Write the failing test(s) first. Use the existing `backendProbe` injection seam (see
`tests/VisualRelay.Tests/` — search `backendProbe` / `BackendReadiness` for the established pattern).

- **Transient blip recovers (the regression test).** A fake probe that returns not-ready on the first
  call and ready on the second causes `RunAsync` to **proceed** (no flag) — i.e. the run is not burned
  by a single transient failure. (If `CheckWithRetryAsync` is tested directly, a probe scripted
  `fail, succeed` returns ready.)
- **Genuinely-down backend still flags.** A fake probe that always returns not-ready causes `RunAsync`
  to return a failed `SubagentResult` carrying the connection hint, after the bounded number of
  attempts (assert the attempt count and that the message is the connection hint).
- **Happy path is single-call, no delay.** A probe that returns ready on the first call is invoked
  exactly once and `RunAsync` proceeds with no added wait.
- **Cancellation stops retries.** Cancelling the token between attempts stops further probing promptly
  (no full attempts×backoff wait) and does not throw out of the never-throwing probe.
- **UI status path unchanged.** The single-shot `CheckAsync` still makes exactly one call (the top-bar
  indicator does not retry).
- No regression in existing `ProcessRunners` / readiness tests.

## Done when

- The pre-run backend readiness check retries a small, bounded number of times with a short backoff
  before flagging, so a sub-second transient blip no longer flags an otherwise-healthy task; a
  genuinely-down backend still flags fast with the same "start the LiteLLM proxy" hint.
- The UI status-indicator probe remains single-shot; the happy path adds no latency; retries honor
  cancellation.
- Retry count and backoff are named constants (or config keys — see Decisions).
- `./visual-relay check` green; changed files under 300 lines; Conventional Commit subjects
  (e.g. `fix(backend): retry transient readiness blips before flagging a run`).

## Decisions

1. **Retry on the pre-run probe only, not the UI status poll.** The pre-run guard gates whether a
   whole task runs, so it should tolerate a blip; the top-bar status light should show the
   instantaneous state, so it stays single-shot. Implement the retry as a new method/overload rather
   than changing `CheckAsync` semantics, so the UI path is untouched.
2. **Constants, not config (unless trivial).** Default to named constants (≈3 attempts, ≈500 ms
   backoff). Promoting them to `.relay/config.json` keys is acceptable if it follows the existing
   optional-numeric precedent cleanly, but is **not required** — do not over-build.
3. **Preserve fail-fast intent.** The retry budget must stay well under the ~36 s the probe was
   designed to save; bound it explicitly and assert the bound in a test.

## Notes

- Don't touch the watchdog/stall path (that already retries via `maxStallRetries` for *mid-run*
  silence). This task is only about the *up-front* reachability probe that runs before the process
  launches.
- Don't mask a real outage: after the bounded retries, the behavior and message are exactly today's.
