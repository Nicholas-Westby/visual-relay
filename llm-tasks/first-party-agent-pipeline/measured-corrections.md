# Corrections to the spec, measured rather than assumed

Everything below was checked against the real code or the live provider APIs on
2026-08-31 while implementing phases 0 to 2. Where a measurement disagrees with
the spec, the measurement wins and the reason is given. Where it agrees, that is
noted too, because several of these claims are load-bearing and worth knowing
they still hold.

## Confirmed, and stronger than stated

| Claim | Spec | Measured |
| --- | --- | --- |
| Stage time with no live model output | 99.7% over 15 stages | **99.96%** over 957 stages: 4536 minutes of agent wall time against 1.71 minutes of trace-record spread |
| Per-stage-instance retry rate | 2.3% | 2.33% (25 of 1074 distinct task-stage pairs) |
| `turn_drops` and `scavenged_calls` | 0 across the corpus | 0 across all 1109 reports |
| Corpus outcomes | 1092 / 10 / 7 | identical |
| Premium providers unused | never used here | **zero** stages on any premium tier, and neither key is set on this machine |

## Confirmed on the wire

`usage` really does land in three different places, and Moonshot really does
report it twice, so a reader that accumulates would double every Moonshot bill.
Reasoning tokens really are inside `completion_tokens`: one Z.AI call spent all
40 of its 40 completion tokens on reasoning and returned no content at all.
Hugging Face really does lower-case the model id in its response.

The image claim holds decisively. The same PNG, prompt and model, minutes apart:
direct to the provider gives 207 prompt tokens and a correct description; through
the proxy it gives 91, which is exactly the no-image control. The proxy drops it.

## Corrections

**The `claude` tier had seven branches, not six.** Four of them are shared
`tier is "claude" or "vision"` guards. Since `vision` keeps its behaviour those
had to be rewritten rather than deleted; they are now one named rule.

**`tool_choice: "required"` is a Moonshot constraint, not a DeepSeek one.** The
spec says `reasoning_effort: "none"` unlocks it and that it 400s while thinking
is on. DeepSeek accepted `tool_choice: "required"` with reasoning left on and
returned a well-formed call. Moonshot is the provider that refuses it, with
"tool_choice 'required' is incompatible with thinking enabled". It is recorded
per provider now, and DeepSeek needs no workaround.

**The in-box SSE parser cannot be used.** Step 8 recommends
`System.Net.ServerSentEvents`. Its `SseItem<T>` exposes only data, event type, id
and retry interval, so there is no way to represent a comment: it drops
keepalives, exactly as the SSE specification requires. Step 27 needs keepalives
visible but distinct, feeding the idle budget without resetting the
output-silence clock. It also consumes a `Stream` rather than chunks, which
hides the chunk-boundary cases step 14 asks to be tested. A first-party
incremental parser replaces it. No SDK dependency is added either way.

**Hugging Face returned 400, not 410,** for a model that is no longer served. The
scenario may differ from the one originally measured, so this is not a
refutation. The classifier treats 400, 404 and 410 alike, which is right either
way.

**GLM's rejection names its accepted values.** The spec records only that
reasoning cannot be disabled. The 400 body says "please use low, high, or max",
so the accepted set is known rather than guessed.

## A pre-existing bug found on the way

`MainWindowViewModel.AllProviderKeys` held six providers while the settings panel
bound only five rows, so the last key had no editor at all and every row comment
from index 1 down named the wrong provider. Z.AI had been inserted at index 1
without a matching row. The test called `ProviderKeyNames_MatchTheSettingsPanelRows`
compares two C# lists and never reads the markup, which is why it stayed green
throughout. Removing the two premium providers resolves the immediate breakage;
a guard that actually parses the markup now keeps the two in step.

## A defect the real captures caught that synthetic tests missed

Hugging Face sends `prompt_tokens_details` and `completion_tokens_details` as
explicit JSON nulls rather than omitting them. Reading through one threw. It was
found only because the four captured streams are replayed through the production
reader; hand-written fixtures had all omitted the fields instead.

## Confirmed for a phase-3 step not yet implemented

`GET https://router.huggingface.co/v1/models` answers 200 unauthenticated with
136 models, carrying per-provider `status`, `context_length`, `pricing` and
`supports_tools` exactly as step 21 describes. The two floating vision routes
match the spec's figures to the cent:

| Model | Host | Context | Input | Output |
| --- | --- | --- | --- | --- |
| Qwen3-VL-235B-A22B-Instruct | novita | 131072 | 0.30 | 1.50 |
| Qwen3-VL-235B-A22B-Instruct | deepinfra | 262144 | 0.20 | 0.88 |
| Qwen3-VL-30B-A3B-Instruct | novita | 131072 | 0.20 | 0.70 |
| Qwen3-VL-30B-A3B-Instruct | deepinfra | 262144 | 0.15 | 0.60 |

The catalog currently records the dearer host for each, which is the correct
half of step 21's "pin the suffix or price the worst case". Deriving the numbers
from this endpoint rather than hand-copying them is still outstanding.

## End-to-end validation after phase 1

A twelve-stage run was driven through the control API against a small throwaway
repo, to prove that retiring the two premium providers did not disturb the live
pipeline. It asked for a `subtract` function beside an existing `add`.

| Measure | Value |
| --- | --- |
| Wall clock | 5 min 34 s |
| Stages executed | 9 (Visual-review and Fix correctly skipped) |
| Outcomes | 9 success, 0 error, 0 exhausted |
| Retries | 0 |
| Turns | 41 |
| LLM time | 300 s |
| Cost | $0.039 |
| Tiers exercised | cheap, balanced, frontier |

The agent added `subtract`, registered both prescribed cases, took the suite from
two passing to four, and committed. Tier resolution, the fallback chains and cost
attribution all still work with four providers instead of six.

The run also re-confirmed the blindness this whole change exists to fix: mid-run,
the Commands tab showed 22 entries, every one of them stamped `s01` from the
stage that had already finished, while the running stage showed nothing. Every
assistant record in the fresh trace carries `"usage": {}`.

## The offline differential, and what it found

All **880** recorded traces that carry a tool call now replay through the new
loop with identical tool calls in identical order. That is the spec's stated
precondition for spending anything on a benchmark.

Getting there turned up three things worth recording.

### The spec's tool inventory is incomplete

The spec names fourteen tools. Counted across all 967 traces, the recorded runs
used twenty-five names:

| Tool | Calls | Ported |
| --- | --- | --- |
| read_file | 14052 | yes |
| grep | 5071 | yes |
| edit_file | 2639 | yes |
| run_shell_command | 2420 | yes |
| run_command | 2337 | yes |
| list_files | 1112 | yes |
| todo | 949 | yes |
| read_multiple_files | 450 | yes |
| write_file | 378 | yes |
| think | 236 | yes |
| outline | 205 | yes |
| snapshot | 146 | yes, but see below |
| python | 89 | no, covered by the command tools |
| use_skill | 55 | no |
| delete_file | 61 | yes |
| view_image | 40 | yes |
| check_subagents | 25 | no |
| fetch_url | 24 | no |
| spawn_subagent | 16 | no |
| bash, glob, find, tail_command, look_for_files_tests, bash_command | 13 total | no |

### "Skills and subagents are unused" is not accurate

The spec lists skills and subagents among the machinery that "fired 0 times".
Skills fired **55 times**, always requesting the `nono-sandbox` skill, in 5.7% of
traces; subagents fired **41 times** across two tool names. They are rare, not
unused. The differential excludes them explicitly and counts them, and a test
fails if dropped tools ever exceed 2% of recorded calls.

The claim that IS exact is the python one: the spec says 89 calls bypassed the
command guard, and the corpus contains exactly 89.

### `snapshot` means something other than what was built — OPEN

The spec names `snapshot` in the tool list without saying what it does. The
recorded corpus says: 146 calls, every one shaped
`{action: "save"|"restore", label?, summary?, force?}` — a conversation
checkpoint the model writes a long summary into and later restores.

The tool that was built instead reports the working tree (branch, porcelain
status, diffstat) and takes no arguments. The differential does not catch this,
because it compares which calls are made rather than whether the arguments would
be accepted.

This is the largest single piece of unfinished business in the tool set. Either
the checkpoint tool should be implemented under this name and the working-tree
report renamed, or the name should be retired and the 146 calls accounted for.

## The first-party loop, driven end to end

A full twelve-stage task was run through the new loop against real providers via
the control API, on a throwaway repo, asking for a `multiply` function beside an
existing `add`.

| Measure | Value |
| --- | --- |
| Wall clock | 4 min 28 s |
| Stages executed | 9 (visual review and fix correctly skipped) |
| Outcomes | 9 success, 0 error, 0 exhausted, 0 retries |
| Turns / tool calls | 38 / 51 |
| LLM time / tool time | 256 s / 8.4 s |
| Models actually served | deepseek-v4-flash-vision-exp, deepseek-v4-pro, glm-5.3-flash |
| Compactions, storms, guardrail firings | 0, 0, 0 |
| turn_drops, scavenged_calls | 0, 0 |

The agent added `multiply`, registered both prescribed cases, took the suite from
one passing test to three, and committed.

### The headline: output is visible while a stage runs

Captured mid-run, with stage 2 still executing, the Commands tab held **130
entries, every one stamped `s02`** — the running stage — showing live
`run_command` calls and streaming reasoning. The same capture under the previous
runner showed 22 entries, all stamped `s01`, from the stage that had already
finished, while the running stage showed nothing at all.

### Measured cost against the old estimate, same run

The previous estimator computed output as `ceil(answer.Length / 4) + calls * 50`.
Against the provider's own numbers on this run:

| Stage | Measured output | Old estimate | Under-count |
| --- | --- | --- | --- |
| 1 | 2364 | 1014 | 2.3x |
| 2 | 5558 | 1949 | 2.9x |
| 3 | 2431 | 565 | 4.3x |
| 4 | 1253 | 443 | 2.8x |
| 5 | 2198 | 356 | 6.2x |
| **Total** | **13804** | **4327** | **3.2x** |

Reasoning was 10,065 of 17,942 output tokens across the whole run — 56% — which
is why a length-derived estimate cannot work on these models. Every figure is
attributed to the concrete model that served the call, so a fallback hop is no
longer invisible.

### Two bugs this run found that no fixture could

**Provider keys were resolved from the process environment only.** They live in
the user-level `.env` the key panel writes, so every tier resolved to "no model
is available" on a machine with all four keys set. Fixed by resolving through
both, in the documented precedence.

**The contract reader accepted balanced prose.** It took the last brace-balanced
span without checking it parsed, so a sentence containing `{file, line}` written
after the contract block won, and the stage was flagged with a parse error
against prose while the real contract sat just above it. The extractor it
replaced checked that candidates parse; this one now does too.

## What the target-repo matrix found

Adding Tier A of the matrix (step 35) turned up defects beyond the JVM gap the
spec named. The JVM gap was real: `TestPathClassifier` has always classified
`.java` and `.kt` files as tests while no detector recognised `pom.xml` or
`build.gradle`, so every Maven or Gradle repository fell through all eight rules
onto the empty placeholder.

**Init could never validate a repo-relative program.** .NET resolves a relative
`ProcessStartInfo.FileName` against the calling process's current directory, not
against `WorkingDirectory`, so `./gradlew test` raised ENOENT and was rejected at
init even though the pipeline runs the same command under `/bin/sh` with the repo
as cwd, where it resolves perfectly. This is the same init-versus-pipeline family
as the `&&` mismatch the spec names, and it would have made wrapper preference
actively harmful. It also blocked the common `./scripts/test.sh` shape outright.

**The `&&` shell mismatch is real in BOTH directions.** The spec describes a
chained command being rejected at init but running fine later. The reverse also
holds: `true && <absent binary>` is *accepted* at init, because a direct exec
hands `&&` and the binary name to `true` as plain arguments and it exits 0, while
`/bin/sh` would fail it.

**The persisted config HTML-escapes the command.** `vitest run && tsc --noEmit`
is written to `.relay/config.json` as `vitest run && tsc --noEmit`. It
round-trips losslessly, but a file users are invited to hand-edit does not read
back as written.

**`cargo test {files}` expands to a shape cargo cannot use.** A
`tests/integration_test.rs` does classify, so the token expands to
`cargo test tests/integration_test.rs` — but cargo reads a bare positional as a
test-NAME filter, not a path. It matches nothing and reports green. The same
shape would be wrong for Maven and Gradle, which need `-Dtest=` and `--tests`.

Smaller ones, all now pinned rather than fixed: a Python repo with both
`pyproject.toml` and `tests/` yields `pytest` twice; a conventional .NET repo
gets `pytest` as its second candidate purely from a root `tests/` directory;
`GuardCommandDetector` still enumerates a `tools/guards/*.sh` directory that was
ported to C# and no longer exists here; and GitSim reads only the root
`.gitignore`, so a nested one has no effect.

## The A/B benchmark, and the bug it found

Spec steps 37-39 ask for a paid A/B against the old path. At full scale that is
120 runs and weeks of drains, which was not on the table. What ran instead was
the same design at 1/10 scale: 3 seeded tasks against a small Python repo, 2
repetitions, both arms, 12 paid runs. Each run started from the same commit with
the working tree reset, and counted as a pass only if the repo's own test script
came back green AND a commit had actually been made.

| arm | runs | passed | 95% interval | median | mean | turns/run | tool calls/run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| swival | 6 | 6 (100%) | 54-100% | 394s | 420s | 42.8 | 78.2 |
| firstparty | 6 | 5 (83%) | 36-100% | 238s | 258s | 34.0 | 48.8 |

The intervals overlap almost completely. Six runs an arm cannot separate a 100%
pass rate from an 83% one, so **the honest reading is that the benchmark did not
detect a quality difference**, not that the old path is more reliable. The
timing gap is the part worth trusting: the first-party arm finished in roughly
60% of the time, using about 80% of the turns and 60% of the tool calls, and
that ordering held on every one of the three tasks independently.

### Step 38's acceptance rule cannot be met as written

Step 38 asks for per-stage pass rate with Wilson 95% intervals, accepting when
no stage's lower bound sits more than five points below the old arm's point
estimate. Computed that way on this run, every stage fails — including the six
stages where BOTH arms scored a clean 6/6:

| stage | swival | firstparty | gate | verdict |
| --- | --- | --- | --- | --- |
| 1-3 | 100% (lower 61.0) | 100% (lower 61.0) | 95% | fail |
| 4 | 100% (lower 61.0) | 83% (lower 43.6) | 95% | fail |
| 5-9 | 100% (lower 61.0) | 100% (lower 56.6) | 95% | fail |

A rule that rejects two arms with identical perfect scores is measuring sample
size, not quality. That is expected at n=6 and is not itself the problem.

The problem is that **the rule barely passes at the spec's own scale either**.
Step 37 specifies N=5 per task per arm across 12 tasks, so n=60 per stage. A
FLAWLESS new arm at 60/60 has a Wilson lower bound of 94.0%. Five points below a
perfect old arm is 95%. So if Swival scores 100% on a stage, no possible result
lets the new arm clear that stage — not even a spotless 60 for 60. The rule only
admits a pass when the old arm's own point estimate is at or below 99.0%.

Phase 0 measured a 2.3% retry rate, so Swival is probably not perfect per stage
and the gate is probably reachable in practice. But it is reachable by accident
rather than by design, and one unusually good Swival sample would block a cutover
on a new arm that made no mistakes at all. Before Phase 6 runs for real, the rule
wants restating as a difference between the arms with an interval on THAT
difference (a two-proportion test), not as one arm's interval against the other
arm's point estimate.

### The one failure was real, and worth the whole exercise

The single failure was not noise. Its plan stage wrote a correct contract and was
told `the contract is missing the required key "plan"`.

`StageContractReader` had been searching BACKWARD from the last `{` in the
answer. The last brace in a plan that describes code is very often inside a
string value: this one said

    "plan": "Add apply_all(a, b) returning {\"add\": add(a,b)} and register a case."

A backward search starts in the middle of that string, and the scan from there
has no idea it is inside one, so it lifted a fragment that parsed as valid JSON
and was not the contract. The reader then reported a missing key against text
that plainly had it. The run stopped after 4 of 9 stages.

It now scans FORWARD once, tracking string state, and prefers the last candidate
that both parses and satisfies the contract. Three further paid runs of the exact
task that failed all passed, in 337s, 316s and 351s.

This is the kind of defect no fixture would have produced, because writing the
fixture requires already knowing that models put braces inside prose about code.

## Chasing a 2x cost gap in the benchmark, and the three bugs behind it

The benchmark reported the first-party arm costing about twice as much per
cheap-tier stage while the balanced tier agreed to within 1%. Both arms resolve
the SAME model for both tiers, so that was not a routing difference. Three real
defects were behind it, all on step 29's ground.

**The report timeline was fabricated.** `AgentReportWriter.BuildTimeline` split
the stage's total input evenly across calls and accumulated it, so its last
entry came out equal to the SUM of every call's input. The cost estimator reads
that last entry as the final context and bills it as fresh input. A real stage
recorded `[5011, 10022, ... 35077]` against a measured total of 35,081 — a
perfectly straight line, which no real conversation produces.

The loop already knew the true numbers and threw them away. It now records each
call's measured input. On a verification run the same stage went from a
fabricated `[3441, 6882, 10323, 13764, 17205]` to a measured
`[2898, 3266, 3533, 3631, 3880]`, whose sum is 17,208 and matches the reported
usage exactly. The uncached input the estimator bills fell from 17,205 to 3,880.
End to end on one task, the reported cost went from $0.13 to $0.04.

**Five of the nine routes reported every stage as free.** Pricing is keyed on the
catalog alias, but a provider's response names the UPSTREAM id, and the two
differ for `kimi-k2` (answers to `kimi-k2.7-code`), `hf-glm-5.3-flash`,
`hf-qwen3-coder-next` and both `hf-qwen3-vl-*`. The lookup tried the served id,
missed, and returned "not priced, $0" without ever falling back to the tier.
DeepSeek happened to echo its own alias, which is the only reason the benchmark
showed any cost at all. There is now a reverse lookup, case-insensitive and
tolerant of a missing provider pin because Hugging Face lower-cases the id on the
way out.

The test that should have caught this was named
`AnUnpricedServedModel_StillPricesThroughTheTier`, documented the fallback in
prose, and then asserted `False(Priced)` and `0`. A test whose name and body
disagree is worse than no test: it reads as coverage.

**No surface ever named the concrete model.** The loop published a usage event
carrying the served model and the measured tokens; `RelayEventBridge` mapped six
event kinds and let that one fall through to null. So the trace, the run log and
the run history all showed `cheap` and `balanced` and nothing else, and a
fallback hop to a different model was invisible. The trace now carries a line per
call: `model deepseek-v4-flash-vision-exp  in 2898  out 146  cached 2816`.

**One related defect is deliberately NOT fixed.** Cached tokens are a subset of
prompt tokens, and the estimate branch bills the full context AND the cached
count on top, so cache hits are charged twice. This is pre-existing and applies
to BOTH arms equally, so it does not distort the comparison. Step 29 says to keep
the estimate authoritative until Phase 6 completes and then switch; changing it
now would silently rewrite what every historical dollar figure means.

## The cutover, and four behaviours that would have gone quiet

Steps 19, 20, 30 and 40 read as deletions. They are not. Six static helpers on
`SwivalSubagentRunner` were used by code that SURVIVES it, including
`BuildPrompt`, which the first-party loop itself calls. They moved to a new
`SandboxedStage` type first; only then could the runner go.

Four things would have disappeared silently with it. None is mentioned in the
spec, and each was found by looking rather than by a failing test.

**Manifest validation.** Stages 4 and 10 name the files they intend to change,
and the old runner rejected a manifest naming a gitignored or absent path,
returning a corrective message. The first-party loop had no such check. It does
now, at the same point in the contract path.

**The stage-input artifact.** The runner wrote `stage{n}-attempt{m}.input.json`
beside each report and announced it, which is the only way the GUI's stage-input
pane can show the prompt a stage was given. The loop wrote neither. Deleting the
runner would have left that pane permanently empty.

**Six timeout knobs, and the watchdog they drive.** `firstOutputTimeoutMs`,
`inactivityTimeoutMs` and `outputSilenceTimeoutMs`, each with a per-tier map, had
exactly one consumer: the subprocess watchdog. Deleting it made all six dead
config — a repo that set them would have been silently ignored. Worse,
`AgentWatchdog` had been built earlier in this work and never wired in: it was
referenced only by its own tests, so the loop had no stall detection at all
beyond a total budget. Both halves are now connected: the config drives the
watchdog, and a timer evaluates it, because a stall is the ABSENCE of events and
an event sink alone can never notice one.

**A preflight for a binary that no longer exists.** `MissingRequiredTools` still
required `swival` on PATH. Left alone it would have reported a missing tool on
every machine, forever, for a process nothing spawns.

### What the cutover was verified against

A sample repo generated fresh, opened through the Control API, and driven to
completion with no proxy running and no `swival` binary involved:

| check | result |
| --- | --- |
| Task outcome | committed |
| The repo's own tests | 9 passed |
| Stages completed | 9 of 9 |
| Stage-input artifacts written | 9 |
| Served model in every report | yes, and in the run log per call |
| Timeline sum vs measured prompt tokens | equal on every stage |

The run log now carries a line per model call naming the concrete model and its
real token counts. Before this work no surface named anything but the tier alias.

## An independent audit of all forty steps, and what it caught

After the cutover I had the spec audited step by step against the tree by an
agent that had not done the work. It was right and I was over-claiming: I had
called the implementation complete when several steps were partial and two were
untouched. What follows is the corrected record.

### Two regressions the cutover introduced, now fixed

**A killed stage lost its report.** `WriteReportAsync` was handed the stage's own
cancellation token, so cancelling a stage cancelled the write of the report
describing it — and the catch did not cover `TaskCanceledException` either. The
write is now uncancellable and happens in a `finally`, so it survives the
cancellation that used to throw straight past it. Verified by reverting the fix
and watching the new test fail.

**A watchdog kill reached the driver stripped of its signature.** `SuperviseAsync`
did `var (outcome, _) = watchdog.Evaluate()`, discarding the `KillSignature` and
never setting `HardAbort`. The driver's `result.Kill is not null` branches were
therefore permanently false, so an absolute-ceiling kill, an output-silence kill
and a socket wedge all arrived as ordinary escalatable errors — the exact
classification step 27 says to preserve. Both now flow through, with the
watchdog's own `IsHardAbort` deciding which escalate.

A third defect on the same ground: a reset connection escaped every catch in the
client and the loop, taking the stage down with no report at all. It is now an
ordinary route fault the model chain hops past.

### The autopsy artifact, restored

Step 27 also asks for the autopsy file. The subprocess runner had the child's
captured stdout to write; a cancelled in-process loop returns nothing, so there
was no output to write and `AutopsyPath` was always null. A bounded transcript
buffer now keeps the tail of the model's output off the event stream, and a kill
writes `stage{n}-attempt{m}.killed-output.txt` with the same name and header the
old version used, so anything reading the archived corpus still reads these.

### A stream that failed was being read as a success

Z.AI signals a mid-stream failure only through `finish_reason`, whose enum
carries `sensitive` and `network_error` beyond the usual set. Neither was
handled, so a filtered or upstream-failed response was classified `Completed` and
its truncated content handed to the loop as though the model meant it. They are
now failures, and they differ in retryability: a filtered response will be
filtered again, so it is a bad request; a mid-stream upstream failure is
transient and stays retryable.

### Still not done, and why

**Step 16 — request goldens.** Not started, and until this audit not recorded as
skipped either. There is no `Goldens/` directory, no `VR_UPDATE_GOLDENS`, and no
live suite. This is the largest genuine hole: the per-provider request shapes are
asserted only by unit tests over `ChatRequestBuilder`, never against a live
endpoint's acceptance of them.

**Step 39 — shadow mode.** Foreclosed rather than deferred. It requires both
runners to exist and the old one was deleted. The cutover rests instead on the
880-trace offline differential, fifteen paid live runs, and a full end-to-end run
through the Control API.

**Steps 17 and 18 — the LiteLLM template survives.** `tools/backend/litellm-config.yaml`
is still tracked, still read by the settings panel's tier summary, and still
shipped. Ten test files assert against it, including per-model timeout ceilings
that no longer correspond to anything the code uses. The proxy that consumed it
is gone; the file remains the model catalog's source of truth, and moving that
into C# is a separate piece of work.

**Step 15 — one shared handler, not one per provider.** Recorded as a deviation
rather than a gap: `SocketsHttpHandler` already pools per origin, and
`PooledConnectionLifetime` bounds each connection's age, so the stated goal —
forcing new connections and refreshing DNS against these load balancers — is met
without a handler per provider.

**Step 22 — `FencedJsonExtractor` was not deleted.** Two live callers outside the
agent path still use it. The agent path no longer does, which is what the step
was for.

**Steps 31 to 35 — the fault and repository matrices are incomplete.** Missing by
name: a truncated fenced answer, an empty response, a 429 carrying `Retry-After`,
the exact attempt count on an exhausted retry budget, and three of four
cancellation cases. Tier B of the repository matrix does not exist, which follows
from step 5's cassette directory being empty. Three Tier A rows are absent,
including the spec's own headline row, the six-minute test suite.

**Step 38 — five secondary metrics were not reported:** failed tool calls, retry
rate, resilience counters, the stage-7 verdict distribution, and
manifest-violation counts.

One portability defect found alongside: the differential's corpus check was a
hard assertion against a gitignored directory, so the suite went red on any clean
checkout. Absent now skips; present-but-thin still fails, which is the case it
was actually written to catch.

## Step 38's secondary metrics, reported

Step 38 names secondary metrics to report against the Phase 0 corpus baselines.
The earlier write-up gave three of them and skipped five. Here are the rest,
computed from the same twelve benchmark runs.

| Metric | Phase 0 corpus (1109 reports) | swival, 6 runs | firstparty, 6 runs |
| --- | --- | --- | --- |
| Tool calls per stage | 31.56 mean | 6.51 | 4.65 |
| Failed tool calls per stage | 0.95 mean | 0.58 | 0.11 |
| Failed share of all tool calls | 3.0% | 9.0% | 2.4% |
| Resilience interventions | 0.37 compactions/stage | 1 | 0 |
| Contract retries | 2.33% of attempts | 0 | 0 |
| Stage-7 Review verdicts | — | all completed | all completed |
| Manifest violations | — | 0 | 0 |

The benchmark tasks are far smaller than a corpus task, which is why both arms
sit well under the corpus means for tool calls. Within the comparison the
interesting number is the failure share: the first-party arm failed 2.4% of its
tool calls against the subprocess arm's 9.0%, on the same three tasks. The
plausible reason is the guard — every command now runs through the same hardened
prefix with no opt-out, so a call that would have been rejected downstream is
shaped correctly before it is made — but six runs cannot establish that, and this
is reported as an observation rather than a cause.

Zero counts over six runs bound a rate loosely, not tightly: no resilience
intervention, contract retry or manifest violation fired on the new arm, which is
consistent with the corpus rates (0.37 compactions and 2.33% retries per stage
would predict roughly two and one across 63 stages) but does not distinguish
"rarer" from "unlucky".

**One metric is not reportable from the surviving artifacts.** Retry rate per
arm needed the per-attempt report files, and the benchmark repositories were
deleted after the runs to keep the VM's disk free — which the session's own
instructions asked for. The logs carry attempt markers only on the subprocess
arm, so the two are not comparable. Measuring it properly needs a fresh paid run
with the reports retained.
