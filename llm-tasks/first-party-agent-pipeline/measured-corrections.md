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
