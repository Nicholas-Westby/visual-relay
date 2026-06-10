# A wedged provider call can hold a stage hostage for the full stage cap

Observed live 2026-06-10 08:07–08:27: during watchdog-real-liveness-signal's Review, a
frontier (kimi-k2) request sat ESTABLISHED swival→litellm with zero bytes for 18+ min —
swival asleep on the socket, litellm's upstream call never producing a byte. The
configured `stream_timeout`/provider-fallback machinery did not fire for this request
shape (the config's own comments anticipate exactly this "rare byte-0 stall that no
timeout/fallback caught"; commit 5bbcf15's fresh-TCP-per-request didn't prevent it).
Recovery required a manual backend restart to sever the socket. Without intervention
the stage burns its entire cap (40 min) before flagging.

Defense-in-depth note: the inactivity-based stage watchdog (watchdog-real-liveness-signal)
will bound the damage pipeline-side; this task bounds it backend-side so the request
itself can never wedge that long, whichever lands first.

## Goal

No single LLM request through the backend can remain in flight beyond a hard ceiling
(suggest 4–6 min for frontier, less for cheap/balanced — above the slowest legitimate
single completion observed, far below the stage cap). On expiry litellm aborts the
upstream call and returns an error/fallback so the client retries immediately.

## Approach (suggested)

- In `tools/backend/litellm-config.yaml` (and the generator
  `tools/VisualRelay.GenBackendConfig` so generated configs inherit it): set per-model
  `request_timeout`/`timeout` hard ceilings — distinct from `stream_timeout` (TTFB) —
  per tier; verify with litellm's docs which knob bounds TOTAL request wall-clock for
  both streaming and non-streaming calls, and that the existing `fallbacks` fire on it.
- Validate empirically with a blackhole upstream (e.g. a mock provider that accepts and
  never responds): request errors out at the ceiling and the fallback chain engages —
  add this as a backend smoke test if the repo grows one, otherwise document the manual
  proof in the task ledger.
- Keep the ceilings comfortably above real slow completions (frontier reviews have
  legitimately taken 2–4 min of single-call time) — this is a wedge-breaker, not a
  latency budget.
