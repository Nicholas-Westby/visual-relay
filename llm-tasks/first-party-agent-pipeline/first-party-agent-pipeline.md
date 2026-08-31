# Own the LLM pipeline: replace LiteLLM and Swival with first-party C#

Visual Relay drives an agent it cannot see, through a proxy it cannot fix, in a
language it does not otherwise use. Measured on this machine at 0.106: 843 MB of
Python across two independently-versioned interpreters (a 482 MB LiteLLM venv of
216 packages on Python 3.13, and a 361 MB Swival install on 3.14), plus a `uv`
prerequisite and a 5-second proxy boot on every launch. The cost of that distance
is not aesthetic. It is measurable, and it is currently being paid in five places:

**Blindness.** Swival is launched `-q` behind `nono --silent`, and its streaming
gate is `verbose && sys.stderr.isatty()` — both false for a supervised
subprocess. It also writes its whole JSONL transcript at process exit; across six
multi-minute stages, every record in a trace file is stamped within 50–190 ms of
the others. So `RelayTraceTailer`'s 200 ms poll loop, its byte-offset
bookkeeping, and `watchdog.Pulse("trace")` are all inert during a stage. Measured
across 15 stages of one drain: **26.0 minutes of agent wall time, 25.9 of it
(99.7%) with zero model-output visibility.** The Commands tab is blank for the
entire duration of every stage. `docs/DESIGN.md:43` claims Visual Relay "tails
Swival JSONL traces live"; it does not.

**A tool ceiling that cannot be raised and lies about itself.** `MAX_TIMEOUT = 240`
at `swival/tools.py:2454`, clamped at `:2460` and `:3059`, with no flag, env var
or config key anywhere in the package. The model may request a timeout and
requests above 240 s are silently reduced — and the failure text then reads
`error: command timed out after 240s`, a value the model never asked for, so it
cannot learn to adapt and retries at the same doomed number. In this repo's own
history: 5,750 command-tool calls, **378 asked for more than the cap**, 7 were
killed at exactly 240 s. Those were real tasks — `virtualize-watchdog-test-waits`
and `group-consecutive-watchdog-heartbeats-in-run-log` each asked for 600 s,
`fix-vision-tier-backend-routing` for 360 s. Stages 5, 6, 9 and 11 explicitly
instruct the agent to self-verify with the targeted test command, so on any
target repo whose tests exceed 240 s the agent **structurally cannot comply**, on
every attempt, forever.

**Invented cost.** Swival reports no output-token count and emits `usage: {}` in
20,381 of 20,381 assistant trace records, so `RelayCostEstimator` fabricates
output tokens as `ceil(answer.Length / 4) + llm_calls * 50`. Input is worse: the
estimator takes the last cumulative `prompt_tokens_est` on the documented
assumption that context is "monotonically non-decreasing", and **133 of 1,006
reports (13%) violate it**, correlating exactly with compaction count. Worst
observed: `speed-up-automated-tests-july-17/stage2-attempt2` bills 62,064 input
tokens against a telescoped ~462,586 — an **87% undercount**. Every dollar figure
the app has ever displayed descends from these two numbers. Recorded `model` is a
tier alias resolved to the chain head, so a fallback hop is invisible too.

**Upstream fixes that cannot arrive.** `BackendVenv.Ensure` installs
`litellm[proxy]` unpinned but exactly once: if the probe succeeds it returns
early (`BackendVenv.cs:37-38`) and there is no version check or `--upgrade`
anywhere. This machine has been frozen on 1.92.0 since 2026-07-15, six stable
releases behind. LiteLLM fixed the DeepSeek image-stripping bug in PR 38397,
merged five days after DeepSeek shipped the model, and this repo cannot consume
it. Unpinned-but-frozen is also not reproducible: two machines on the same commit
run different proxies and nothing records which.

**Safety theatre at the edges.** `--command-middleware` is wired only when
`<targetRoot>/.githooks/command-guard` exists (`ProcessRunners.cs:124-129`), and
Visual Relay never provisions that file into target repos — so **every repo except
this one runs with no command guard at all**. Even here, Swival's `python` tool
dispatches without consulting the middleware or the command policy
(`swival/tools.py:3806-3824`); 89 such calls in this repo's history were never
inspected. And Swival never writes a report on SIGTERM — its handler computes
`"interrupted"` and re-raises without persisting — which is why 1,109 reports
contain zero interrupted outcomes and why `preserve-trace-when-stage-killed`
exists.

Meanwhile the boundary itself costs roughly 4,180 lines of C# whose only purpose
is inferring, from outside, what an opaque process is doing: CPU-tree sampling as
a liveness proxy, OS TCP-table scraping for wedge detection, stdout failure
distillation, fenced-JSON contract extraction, prompt-echo detection by
positional-argument containment, killed-output autopsy files, and a 391-line
ref-counted registry that exists solely because Swival's config is a mutable file
at a fixed path in the repo under test.

## Prescribed approach

**Build one in-process pipeline, not two replacements.** The proxy exists only
because Swival is a separate process that speaks HTTP; when Visual Relay owns the
agent loop, the HTTP hop has no reason to exist and no local listener should be
built to replace it. This is a single change, not two sequenced ones: all twelve
Swival profiles point at `http://127.0.0.1:4000`, so removing the proxy first
would mean rebuilding most of what was deleted.

**Design for this consumer, and do not port.** Read Swival and LiteLLM freely to
understand behaviour and failure modes; transcribe neither. Much of what they
carry is generality Visual Relay does not buy: an adaptive learned-context-window
apparatus for unknown backends when the window is known; a tool-call scavenger
that fired **0 times in 1,109 runs**; `turn_drops` likewise **0**; a 15-entry tool
alias table used only to build error strings; goals, skills, MCP, A2A, REPL and
subagents, all unused. Earn back only what the corpus shows earning its place:
the compaction ladder (414 firings), the consecutive-error guardrail (95), the
repeat-call storm breaker (41, with the tuning escape hatch it currently lacks),
unknown-tool-name recovery (13), and schema-aware argument repair.

**Four components**, each with one job:

1. **`VisualRelay.Core/Llm` — provider clients.** Direct HTTPS to the six
   providers, streaming and non-streaming, tool calls, and real `usage`. One
   `HttpClient` whose `HttpMessageHandler` is constructor-injected, behind an
   `IProviderTransport` seam. No local server, no YAML.
2. **`VisualRelay.Core/Llm/Routing` — policy.** Tier aliases, the provider-
   diversified fallback cascade, per-model timeouts, retries and backoff. The
   routing table moves from generated YAML into the existing
   `BackendConfigGenerator` catalog, which already holds the whole policy in C#;
   `tools/backend/litellm-config.yaml` is deleted.
3. **`VisualRelay.Core/Agent` — the turn loop.** Request construction, response
   triage, tool dispatch, context management, repair. Implements the existing
   `ISubagentRunner` seam, so `RelayDriverDependencies` and the ~80 stage-level
   test doubles keep working untouched.
4. **`VisualRelay.Core/Agent/Tools` — the tool set.** Every model-invoked shell
   command runs as its own child process through the existing `BuildNonoPrefix`
   sandbox path, in its own POSIX process group, with its own watchdog.

**Run the loop in-process, sandbox each command individually.** Measured on this
machine: bare spawn 3.6 ms, `nono` without rollback **127 ms**, `nono` with
rollback **730 ms**. Against 603 real stages (median 6 shell calls, p99 48),
per-command sandboxing costs a median stage **+0.8 s** and a p99 stage **+6.1 s**
on stages that run 145–342 s — under 2% at the tail. So drop nono's rollback on
the agent path and rely on the git-based undo Visual Relay already has
(`run-base.txt`, `pre-run-untracked.txt`, `WorktreeResetter`, `RewriteUndoStore`);
the verify path already runs sandboxed with `rollback: false`. `SandboxedTestRunner`
already sandboxes an individual command through the same prefix builder, so this
is an existing hardened path, not new security surface.

Two objections do not survive examination. `RELAY_COMMIT_TOKEN` is **not** a
constraint: the driver process never holds the variable — it is injected into one
`git commit` `ProcessStartInfo` (`GitCommitter.cs:199-206`) — so non-inheritance
is structural and process-model-neutral. The boundary buys nothing there today
anyway, since the nonce sits in `.relay/ACTIVE/info.json` which the agent can
read, and the command guard's `SkipEnvPrefix` walks past `VAR=value` prefixes
without stripping them. Concurrency is likewise already solved at driver level:
three subagents run concurrently today via plain unawaited tasks, and every
per-run object is already per-invocation.

**The one real cost is unconditional kill,** and the spec accepts it explicitly.
`kill(-pgid, SIGKILL)` reaps a wedged agent from any state; in-process, liveness
depends on the loop reaching an await. Four distinct kill outcomes shipped over
~25 commits because agents wedge in novel ways. Mitigate deliberately: keep every
heavy or native operation in its own killable process group, add the
`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`
handlers the app currently lacks, bound conversation retention and tool-output
size explicitly (process exit no longer reclaims), and keep a per-stage top-level
catch.

**Observability is the product of this change, not a side effect.** Emit one
structured event stream from the loop and let everything derive from it: the live
UI, the trace file, the cost ledger, and the watchdog. Streaming model output to
the Activity column then requires no new protocol — publish a `RelayEvent` onto
the sink → dispatcher → `ObservableCollection` path that already works and is
currently fed by nothing until a stage ends.

**Fix the four things the boundary has been hiding, as part of this work:**

- **Model-chosen tool timeouts with no hidden ceiling.** The model sets a
  per-invocation timeout; the only cap is the stage budget. If a request is ever
  reduced, say so in the tool result text so the model can adapt.
- **DeepSeek image input works.** Do not flatten multimodal content lists on the
  DeepSeek route. Verified upstream behaviour: an image posted straight to
  `api.deepseek.com` yields 435 prompt tokens and a correct description, versus
  96 through the current proxy.
- **Measured cost per LLM interaction.** Read real `usage` per call, attribute it
  to the concrete model actually served (not the tier alias), and record it per
  interaction rather than per stage. The `answer.Length / 4` heuristic, the
  `OutputTokensPerTurn = 50` constant, the telescoping input derivation and the
  false monotonicity assumption all disappear together.
- **A report on every exit, written atomically.** Including SIGTERM and watchdog
  kill, via temp file plus `File.Move(overwrite: true)`.

**Implementer: invoke the `nick-claude:how-i-work` skill before planning, and
follow it throughout** — TDD via `superpowers:test-driven-development` for every
component, `superpowers:systematic-debugging` when something misbehaves, evidence
over assertion at every completion claim, and a commit per coherent step rather
than one lump at the end. This spec is large; work it in the phase order below
and commit at each phase boundary.

### Steps

**Phase 0 — baselines. Do this first; the change is unprovable without it.**

1. Record, in the phase-0 commit body: clean and incremental build wall-clock
   (`./visual-relay build`, currently 21.4 s incremental); full suite
   (`./visual-relay test`, currently 37.0 s, 3542 tests, 3445 passed, 97 skipped
   — trust only the TRX `<Times>` delta, since summed per-test durations are
   scheduler artifacts inflated up to ~1000×); dependency footprint
   (`backend-venv` 482 MB / 216 packages, `swival` 361 MB, `uv` 52 MB,
   `python@3.14` 168 MB); cold start (grep `backend: ready` and `vr-control:
   listening`, currently 5 s); the `check` red baseline (171 InspectCode findings
   with its rule histogram, so a moved count is not misread as this change's
   fault); the guard inventory (21 matchers, 4,023 lines, 35 guard-test files,
   202 facts); and one end-to-end task run's wall-clock and cost, recording the
   time of day because DeepSeek's weekday peak windows double the rate on the
   tiers backing most stages.
2. Snapshot the corpus that the offline differential depends on: 1,109 reports,
   967 traces, 118 task dirs, 393 MB under `.relay/`. Record the corpus stat
   baselines that later become regression thresholds — per stage: turns 21.4
   mean / 14 median, tool calls 31.6 / 23, failed tool calls 0.95, compactions
   0.37; per stage-instance retry rate **2.3%**; outcomes 1,092 success / 10
   error / 7 exhausted; and `turn_drops` and `scavenged_calls` **0 across the
   entire corpus**, so any non-zero from the replacement is a regression needing
   no statistics.

**Phase 1 — the transport seam and its test apparatus. No behaviour change yet.**

3. Add `IProviderTransport` with `SendAsync` and `StreamAsync`, plus
   `LiveProviderTransport` (constructor-injected `HttpMessageHandler`),
   `RecordingTransport` and `ReplayTransport`. `ReplayTransport` **throws loudly
   on a cassette miss** — never falls through to the network — mirroring
   `GitSimCommandRouter`'s behaviour on an unmodelled argv, and the miss message
   carries a structural diff against the nearest cassette so the changed field is
   visible.
4. Cassettes live at `tests/VisualRelay.Tests/Cassettes/<provider>/<scenario>/`,
   one JSON file per exchange, keyed by SHA-256 over a canonical form of
   `(method, path, model, allowlisted headers, body)` with `Authorization`,
   `x-api-key`, request ids, timestamps and nonces elided and object keys sorted
   recursively. Version the canonicalizer in the file so a canonicalizer change
   is a deliberate re-record rather than a silent mass miss. Add to the csproj
   copy glob beside `Fixtures\**\*`, and mark the directory `-diff` in
   `.gitattributes`.
5. Two guards, both Tier-2 guard-as-tests (see Guardrails on why not `check`
   steps): a secret-redaction guard scanning every committed cassette for `sk-`,
   `hf_`, `Bearer `, each of the six key names in `.env.example`, the live values
   of any of those currently set in the environment, and any 32+ char base64url
   run outside a known-safe field; and a no-`new HttpClient` guard modelled on
   `FakeClockGuard`, allowlisting one composition root by filename. Make
   `RecordingTransport` redact on write too, so the guard is a second line.
6. Make the fast suite hermetic: install a `SocketsHttpHandler` factory that
   throws on any connect attempt. One test opening a socket fails the suite.

**Phase 2 — the provider layer.**

7. Build on `Microsoft.Extensions.AI` 10.9.0 (GA) rather than raw `HttpClient`:
   `Microsoft.Extensions.AI.OpenAI` covers the five OpenAI-compatible providers,
   and Anthropic now ships an **official first-party C# SDK** (`Anthropic`
   12.44.0, MIT) with `AsIChatClient()` built in. `UsageDetails` already models
   this domain 1:1 — input, output, cached-input and reasoning counts plus a typed
   `AdditionalCounts` bag. Reach past the abstraction in exactly three places:
   `RawRepresentation as StreamingChatCompletionUpdate` for incremental tool-call
   deltas (the adapter otherwise buffers them to stream end),
   `ChatOptions.RawRepresentationFactory` plus `JsonPatch` for per-provider
   request shaping, and `RawRepresentation as ChatCompletion` for vendor usage
   fields. `<NoWarn>$(NoWarn);MEAI001;SCME0001</NoWarn>` is required under this
   repo's `TreatWarningsAsErrors`.
8. Encode the wire quirks that were measured live, not assumed. Provider
   disagreements that will silently corrupt results if abstracted over:
   **usage lands in four different places** (top-level on the `finish_reason`
   chunk for DeepSeek and Z.AI, nested in `choices[0].usage` for Moonshot, a
   trailing `choices: []` chunk for HF and OpenAI) and Moonshot emits it
   **twice** — so take last-writer-wins over the union, never accumulate.
   **Cached tokens are a subset of `prompt_tokens` on the OpenAI family but
   Anthropic's `input_tokens` excludes cache entirely**, and the two M.E.AI
   adapters faithfully reproduce that opposite convention behind one
   `InputTokenCount` property; pricing it at the plain rate over-charges roughly
   3.4× on a cached Anthropic call, so compute uncached explicitly and regression-
   test it on day one. **Z.AI can return HTTP 200 with an error body**, so probe
   every response for an `error` key rather than trusting the status. Error
   envelopes differ five ways (HF's `error` is a string on auth failure, an object
   on bad model, and absent entirely on provider passthrough), so accept `error`
   as string or object and `code` as string or integer. Two status codes an
   OpenAI-shaped classifier misses: **Anthropic's 529** for overload and **HF's
   410** for a model a provider dropped. 429 means both "slow down" and "out of
   money" on every provider, so discriminate on the body code before retrying.
   HF model ids are **case-sensitive on request but lowercased in the response**,
   so echoing a response id into a retry 404s.
9. Request real usage everywhere, including `stream_options: {include_usage:
   true}` on the OpenAI-compatible streaming path, which Swival never sets.
   Reasoning tokens are **included in `completion_tokens`** on every reasoning
   provider — measured, and documented by none of them — which is why the current
   `answer.Length / 4` estimate under-counts output cost by close to an order of
   magnitude: on one GLM measurement, reasoning was 137 of 141 completion tokens.
10. Replace LiteLLM's single timeout knob with four separate budgets, because the
    per-model ceilings in the config rest on a false premise. Streaming TTFB was
    measured at 0.43 s (DeepSeek Pro), 1.12 s (GLM 5.3 Flash) and 0.87 s
    (Kimi), with a **maximum inter-chunk gap of 1.12 s across 1502 chunks** —
    reasoning streams as it is produced, so the "up to ~410 s to first token"
    that justifies `stream_timeout: 600` is a *non-streaming* observation. Use
    connect 10 s, time-to-first-byte 30 s, **inter-chunk idle 30–60 s**, and total
    wall clock 600 s. The idle budget is the real stall detector and has a 30×
    margin over the measured worst gap; it would have cut the 18- and 30-minute
    byte-0 wedges at 30 seconds.
11. Never cap output low on a reasoning model, and treat
    `finish_reason == "length"` with empty content as a **distinct retryable
    outcome** — retry with a larger budget or lower effort rather than reporting
    an empty answer. Reproduced on all three reasoning models at `max_tokens: 20`,
    and GLM emitted zero content deltas even at `max_tokens: 1500`. Note GLM 5.3
    Flash genuinely cannot disable reasoning (`thinking:{"type":"disabled"}` and
    `reasoning_effort:"minimal"` both 400 with code 1210), but **DeepSeek can** via
    `reasoning_effort: "none"` — which also unlocks `tool_choice: "required"` and
    named tool choice, both of which 400 while thinking is on. That is a useful
    escape hatch the config does not record; `tool_choice: "required"` is not
    portable and the abstraction must either disable reasoning or fall back to
    prompt-level coercion.
12. Carry `reasoning_content` forward verbatim on every assistant message bearing
    tool calls. Whether DeepSeek *enforces* this is contested: one probe recorded
    a hard 400 when the field is absent, but a direct test across all three
    DeepSeek models, both the bare and `/beta` paths, and three shapes of the
    replayed message returned 200 in all eighteen combinations on 2026-08-31.
    Replaying it is free and quality-relevant on Moonshot and Z.AI regardless, and
    Anthropic does enforce the equivalent for signed thinking blocks. Do not build
    on the assumption that omission is safe.
13. Write the SSE parser as a unit-testable component with no transport
    dependency, covering: an event split mid-UTF-8-codepoint, `data: [DONE]`,
    comment keepalives classified **as keepalives**, multi-line `data:`
    concatenation, CRLF and LF, two complete events plus a partial third in one
    chunk, an empty chunk mid-stream, and first-event latency that does not
    require buffering the whole stream. Treat a stream that ends **without its
    terminator** (`[DONE]`, or `message_stop` on Anthropic) as a failure, and
    check every chunk for a top-level `error` key before parsing it as a delta —
    that is how HF and OpenAI signal a mid-stream error on an already-200
    response, while Z.AI signals it only through `finish_reason` and Anthropic
    through an `event: error` frame. Key tool-call accumulation **strictly on
    `index`**, never on `id`: HF emits a duplicate `id` with an empty `function`
    object, and a client that starts a new call on each `id` invents a phantom
    one. Z.AI emits a whole tool call in a single delta, so any logic assuming the
    first delta has empty arguments is wrong.
14. Delete `Connection: close`, and correct the diagnosis in the process. The
    byte-0 stall is real but the config's explanation is not: it was reproduced
    deterministically as a **server-side hang triggered by request content** —
    sending `role: "developer"` to an HF-routed model hangs indefinitely with zero
    bytes instead of returning 400, and the hang persisted on a fresh TCP
    connection carrying an explicit `Connection: close`. The header is inert
    anyway, twice over: all four probed endpoints negotiate HTTP/2, where
    `Connection` is a prohibited connection-specific header, and in .NET
    `HttpRequestMessage.Headers.ConnectionClose = true` sends the header while the
    socket is reused regardless — only `PooledConnectionLifetime` or
    `PooledConnectionIdleTimeout` actually forces a new connection. Give each
    provider its own `SocketsHttpHandler` with a short `PooledConnectionLifetime`
    (which also fixes stale DNS against these load balancers) and let the
    inter-chunk idle budget catch stalls. Also map `developer` → `system` for
    every provider except OpenAI; three of the others return 400 on it and the
    fourth hangs.
15. Golden the serialized request body per model at
    `tests/VisualRelay.Tests/Goldens/request/<model>/<stage>.json`, refreshed by
    `VR_UPDATE_GOLDENS=1`. **Every golden must be paired with a live-suite
    assertion that the named provider accepts that body**, and no golden may be
    added or updated without a passing live run in the same commit — LiteLLM's
    `drop_params: true` has been silently stripping parameters that providers
    reject, so goldens alone would encode bodies no provider will take.

**Phase 3 — routing and the catalog.**

16. Move tier resolution, the fallback cascade and per-model timeouts into
    `BackendConfigGenerator`'s existing structures as the single source of truth,
    and delete `tools/backend/litellm-config.yaml`. Preserve the key-gated
    semantics exactly: the vision and claude tiers are **omitted entirely** when
    their key is absent so a request errors rather than silently degrading to a
    text model, and every other chain terminates in the fallback tier.
17. Rewrite `ModelCatalogParityTests` and `BackendConfigGeneratorTemplateCoverageTests`
    against the C# catalog, keeping each guard's negative control. Keep the
    per-model-timeout and alias-consistency guards alive. Derive the required
    golden set from the catalog constant so adding a model without pricing or a
    golden fails the build — the self-maintaining pattern `ControlIndexPageTests`
    already uses for commands and routes.
18. Delete the proxy lifecycle: `BackendLifecycle*.cs`, `BackendProcess.cs`,
    `BackendVenv.cs`, `BackendStartOptions.cs`, `BackendSocketProbe.cs`,
    `BackendConfigStep.cs`, `BackendReadinessProbe.cs`, `BackendPaths.cs`,
    `tools/VisualRelay.Backend/`, `tools/VisualRelay.GenBackendConfig/`, their
    ~26 test files, the proxy-log regex scraping in
    `ProcessRunners.Diagnostics.cs:128-191`, and the GUI backend status dot and
    one-click recovery. Port `LlmTestCommandFinder` onto the provider layer,
    keeping `BuildPrompt` and `ExtractCommand` semantics and finally covering the
    transport, which has zero test coverage today.
19. Drop `depends_on "uv"` from `packaging/visual-relay.rb`. `nono` stays — it is
    the sandbox, not a thing being replaced.
20. Derive Hugging Face pricing from the live catalog rather than hand-copying it.
    `GET https://router.huggingface.co/v1/models` works unauthenticated and returns
    per-model `providers[]` with `status`, `context_length`, `pricing.{input,output}`,
    `supports_tools` and latency — so an unpinned model's envelope is discoverable
    instead of guessed. This matters because an unpinned route has **no stable
    price or context window**: `Qwen3-VL-235B` is 131,072 tokens at 0.30/1.50 on
    novita and 262,144 at 0.20/0.88 on deepinfra, and the catalog currently records
    only one of them. Either pin the provider suffix so the envelope is fixed, or
    price the worst case; do not leave both floating. Confirm the `gpt-5` target
    while here — OpenAI marks that alias deprecated and scheduled for shutdown.

**Phase 4 — the agent loop.**

21. Implement the turn loop against `ISubagentRunner`. Return the stage contract
    as a **typed result**, not fenced JSON on stdout: `FencedJsonExtractor`,
    `ValidateContractShape`'s key-presence regex, the prompt-echo heuristic that
    reads `arguments[^1]`, and the nono banner-noise distiller all delete. Keep
    the per-stage contract *shapes* in `RelayStages.cs` unchanged.
22. Port the resilience machinery the corpus justifies, and only that: a
    compaction ladder, the consecutive-error guardrail, the repeat-call storm
    breaker **with a configurable window and threshold** (its hardcoded 6/3
    suppresses a legitimate re-run of the same test command to check for
    flakiness — 41 real suppressions), unknown-tool-name recovery, and
    schema-aware argument repair. Do not port the scavenger or `turn_drops`
    unless evidence appears. Do not reproduce Swival's two `AgentError` landmines
    that abort the whole run on a second repair failure — the driver owns
    escalation and is the right layer to decide.
23. Tools: `read_file`, `read_multiple_files`, `write_file`, `edit_file`,
    `list_files`, `grep`, `outline`, `run_command`, `run_shell_command`, `think`,
    `todo`, `view_image`, `delete_file`, `snapshot`. **`view_image` and
    `list_files` must keep those exact names** — `RelayStages.cs:86` and
    `RelayDriver.ReviewPairTriage.cs:99,113,118` name them in prompts. Every
    command tool goes through the sandboxed-command path; the guard applies to
    all of them, closing the `python`-tool bypass.
24. Reimplement the command-guard policy **in-process**: strip `--no-verify`
    unconditionally, strip `-n` and the `n` from combined short flags only within
    a `git commit`, fail-open for non-git and fail-closed for git commit. This
    stops being an external binary execed by a Python process, which also means
    it applies in every target repo rather than only in this one. Extend
    `CommandGuardDecider*Tests` to the new call path and keep both argv-mode and
    shell-mode coverage.
25. Emit one structured event stream. Publish token deltas, tool-call start and
    finish with real timestamps and durations, turn boundaries, compactions,
    guardrail and storm interventions, retries, fallback hops, and per-call
    usage. The trace file, the cost ledger and the watchdog all derive from it.
    Throttle or coalesce before `Dispatcher.UIThread.Post`, which is
    fire-and-forget with no backpressure today.
26. Rewrite the watchdog against direct signals — last token received, last tool
    call started and returned, request state — replacing CPU-tree sampling and
    TCP-table scraping. Preserve all five `ActivityWatchdog.Outcome` values and
    the `HardAbort` classification (`absolute_ceiling`, `output_silence_ceiling`
    and `socket_wedge` never escalate; a plain stall escalates one tier), and keep
    producing `KillSignature` and the autopsy file. **Assert that an SSE keepalive
    does not reset the output-silence clock while a content delta does** — this is
    the CPU-versus-output distinction that
    `04-detect-hung-llm-requests-before-absolute-ceiling` was written to fix, and
    a naive port recreates that 44-minute hang in a new guise. Add the missing
    `vision` entries to `firstOutputTimeoutMsByTier` and `inactivityTimeoutMsByTier`
    while here; stage 8 silently inherits the flat fallbacks today.
27. Write the report atomically on every exit path including cancellation, and
    promote `result.outcome` and `result.error_message` to a typed result the
    driver branches on. Ten historical failures carried an exact cause and the
    driver saw only "exit 1"; seven carried `exhausted` and it escalated by luck.
28. Cost: record real `usage` per interaction against the concrete served model.
    Emit both the measured value and the legacy estimate during the transition,
    keeping the estimate authoritative until the Phase 6 benchmark completes,
    then switch and delete the estimator's fabrication path. Fix the input-token
    derivation as part of the switch.
29. Delete what the boundary required: `SwivalProfileSession*.cs` and its
    391-line ref-counted pinned registry, the five Python-containment environment
    overrides in `ProcessRunners.SandboxEnv.cs` (`HF_HOME`, `XDG_CACHE_HOME`,
    `UV_CACHE_DIR`, `PYTHONDONTWRITEBYTECODE`, `PYTHONPYCACHEPREFIX`, and the two
    Windows `PYTHON*` ones), and `SwivalGate`. Keep the `dotnet` and MSBuild ones
    — they belong to the target's test command.

**Phase 5 — verification.**

30. Build the scripted fake model at turn granularity (today's doubles script one
    canned contract per stage and never reach inside a turn). It must express, and
    the loop must be asserted against, at minimum: unknown tool name; arguments
    failing schema; truncated JSON arguments; a truncated fenced answer; a closing
    fence on a content line (fixture exists); a JSON array root; an empty response;
    empty content with `finish_reason: length` (the GLM shape, retried with a
    larger budget rather than escalated); mid-stream disconnect with partial
    content preserved; a malformed SSE frame; context overflow driving compaction;
    429 with `Retry-After` honoured on virtual time to the exact second; 429
    without it, with bounded backoff and an exact attempt count; **accept-then-
    never-write** with and without synthetic CPU activity; a slow-but-healthy
    stream over 50 virtual minutes that must **not** be killed; a tool-call storm;
    a recovered response; a connection reset; a 5xx falling through the tier chain;
    and turn-budget exhaustion producing `outcome: "exhausted"` with exit code 2.
    Expose the captured requests too — half of what is under test is what the loop
    *sends* after a fault. All of it on `ManualTimeProvider`, zero network.
31. Cancellation: user-stop and watchdog-kill must remain separate tokens, as
    `ProcessCapture.RunAsync` separates them today. Cover cancel mid-stream,
    cancel before first byte, cancel during tool execution, and double cancel
    producing one outcome and no `ObjectDisposedException`.
32. Schema round-trip: assert the new runner's `report.json` drives
    `RelayCostEstimator.EstimateReport` and `RelayRunHistory.ReadStageMetric` to
    the same values as a golden Swival report, and that `RelayTraceParser`
    produces an identical `TraceEntry` sequence from the new `.jsonl` as from a
    recorded one.
33. Offline differential, free and deterministic: replay every recorded trace's
    model turns through the new loop via `ReplayTransport` and assert the same
    tool calls in the same order and matching `stats` and `result.outcome`. This
    is `ParityHarness.cs` — which already does old-versus-new for GitSim against
    real git — applied to the agent. **Nothing goes to a paid benchmark until it
    replays clean.**
34. Target-repository matrix, because Visual Relay is general-purpose and is
    pointed at arbitrary projects. Tier A is detection-only, free, and belongs in
    the fast suite: materialize marker files in temp dirs and assert
    `DetectCandidates` order and the resulting config bytes. Tier B runs the full
    pipeline, replayed through cassettes where the row exercises Visual Relay
    logic rather than model capability, and live only at release cadence. Rows:
    C#/.NET; TypeScript/Node (exercises the verbatim `scripts.test` copy and the
    shell mismatch where init validates with a direct exec but the pipeline runs
    under `/bin/sh`, so a `&&`-chained command is rejected at init yet would run
    fine); Python (the generated sample repo, as control); Go; Rust (no test file
    to classify, so `{files}` expansion degrades to the full suite); **Java and
    Kotlin, where detection does not exist at all** — no `pom.xml`, `build.gradle`
    or `settings.gradle` marker, so a JVM repo falls through to the weakest rule
    and lands on the placeholder while `TestPathClassifier` happily classifies
    `.java`/`.kt`, meaning classification and detection disagree for the entire
    ecosystem; a monorepo with no root manifest (every detector is
    `TopDirectoryOnly`, so zero candidates); a repo with no detectable toolchain;
    a very large repo (the 100-entry prompt truncation and nono's 2 GiB rollback
    budget, which is what killed `speed-up-headless-ui-tests`); **a repo whose
    tests take six minutes**, which is the headline row for the tool-timeout fix;
    non-ASCII paths including CJK, emoji, RTL, spaces and NFD-versus-NFC; a repo
    with no git history; a repo with a hostile pre-commit hook; and one with
    nested `.gitignore` files, which GitSim structurally cannot model. Add the
    missing JVM detectors as part of this step, and add the absent
    `GuardCommandDetectorTests`.
35. Fix `TestCommandValidator.Classify` accepting non-zero-exit-with-any-output,
    which currently persists `npm test` as a repo's test command on a repo that
    has no test script. Fix the generated `scripts/reset-sample.sh`, which still
    shells out to the deliberately-removed `sample-reset` verb and is broken today.

**Phase 6 — prove it is as good, then cut over.**

36. Benchmark twelve tasks drawn from `llm-tasks/completed/`, each frozen at its
    `run-base.txt` SHA: three mechanical, three medium, three needing real
    diagnosis, one UI/visual exercising stage 8 and the vision tier, one that
    legitimately escalated a tier, one that legitimately needed the fix-verify
    loop. N=5 per task per arm, arms alternated within the same hour, with
    `tierModelOverrides`, `maxTurns` and `baselineVerify` pinned identically —
    the sample repo defaults differ from this one and would make the arms
    incomparable. Alternating also controls for DeepSeek's weekday peak windows,
    which would otherwise show a phantom 2× cost regression.
37. Accept on per-stage pass rate with Wilson 95% intervals: no stage's lower
    bound more than five points below the old arm's point estimate. Report
    secondary metrics against the Phase 0 corpus baselines — turns, tool calls,
    failed tool calls, retry rate (2.3% today, the sharpest single signal),
    resilience counters, LLM time and cost, stage-7 Review verdict distribution
    on the produced diff, and manifest-violation counts as an off-task measure via
    the `EarlyImplementationDetector` and `WorktreeFilter` machinery that already
    computes them.
38. Run shadow mode for a few weeks of real drains before deleting the old path:
    execute the new runner alongside Swival, take Swival's result, record the
    new one. Cheapest route to a large N, at real production distribution.
39. Cut over, delete the Swival runner and its 16 partials, and update
    `AGENTS.md`, `README.md`, `docs/OPERATIONS.md`, `docs/DESIGN.md` (whose
    live-tailing claim becomes true for the first time) and `docs/relay-artifacts.md`.

### Guardrails

- **Do not port.** No transcribed Swival or LiteLLM code, no Python-shaped
  abstractions, no reimplementation of machinery the corpus shows never firing.
  Read them to learn the failure modes; design for this consumer.
- **Do not build a local HTTP listener.** A C# proxy on `127.0.0.1:4000` rebuilds
  most of what this change deletes and keeps the boundary that causes the
  blindness. Call providers directly from the loop.
- **Do not replace one dependency without the other.** All twelve profiles point
  at the proxy; removing it alone forces you to rebuild it.
- **Do not add new guards as `check` steps.** `check` is red at baseline on 171
  InspectCode findings and short-circuits at step 7, never reaching the tests. New
  guards go in as Tier-2 guard-as-tests, which `./visual-relay test` actually runs.
  Follow the house shape: constructor-inject `CachedSyntaxTreesFixture`, N−1 tests
  over inline fixture sources proving the guard bites and does not false-positive,
  plus exactly one live-tree test that is the gate.
- **Never let a cassette miss reach the network,** and never let the fast suite
  open a socket. Both are guarded; do not weaken either to make a test pass.
- **The sandbox stays always-on with no opt-out,** through the shared
  `BuildNonoPrefix`. Preserve the `--block-net` invariant test — network is
  deliberately never blocked, and two tests assert it.
- **Do not parallelize the UI tests or split the test assembly.** A prior attempt
  never landed, dying at stage 5 after nine attempts on a nono rollback budget
  overflow. Scope tests down instead; the 60 s full-suite ceiling stands.
- **Do not silently change what the cost numbers mean.** Emit measured and
  estimated side by side until the benchmark completes, then switch deliberately
  and say so in the commit.
- **Do not weaken the commit-authority gate** while reworking the command guard,
  even though the process boundary never protected it. Fail-closed on `git commit`
  stays fail-closed.
- **Do not keep the 240 s tool ceiling, and do not replace it with a different
  hidden one.** If any budget is ever reduced, the tool result must say so.
- **`view_image` and `list_files` keep their names.** Stage prompts reference
  them as strings.
- Leave `nono` in place. It is the sandbox and is not part of this change.

## Done when

`./visual-relay test` is green with the fast suite hermetic — a socket-throwing
handler installed, zero network, zero provider spend — and still inside the 60 s
ceiling. No Python remains: `backend-venv` and the Swival install are gone, `uv`
is no longer a prerequisite or a Homebrew dependency, and the app reaches
control-API-ready without a proxy boot. The offline differential replays all 967
recorded traces through the new loop with identical tool-call sequences. The
twelve-task benchmark meets the Phase 6 acceptance bar. Tier A of the target
matrix runs in the fast suite and Tier B passes replayed, with the JVM detectors
added and the Java/Kotlin row no longer landing on a placeholder. A stage's model
output appears in the Activity column **while the stage is running**. A tool
invocation may exceed 240 seconds and a six-minute test suite completes under the
agent's own self-verification. An image sent on the cheap tier reaches the model.
Every LLM interaction carries measured usage and a cost attributed to the concrete
model that served it. A stage killed by the watchdog still writes its report.

## Commit-message evidence

Measure at implementation time and put in the commit bodies (≤ 3 hyphen bullets,
≤ 20 words each, no file names or paths): build and test wall-clock before versus
after; bytes of dependency removed; cold start before versus after; the fraction
of stage wall time with live model output, before versus after; the twelve-task
benchmark's per-stage pass-rate table with intervals; measured versus previously
estimated cost on the same replayed task; and the longest tool invocation that
now completes.
