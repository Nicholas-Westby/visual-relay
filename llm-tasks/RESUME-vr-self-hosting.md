# RESUME — Visual Relay self-hosting (handoff 2026-06-09)

Status snapshot for picking this up with fresh context. Goal (standing): complete the
4 remaining VR LLM tasks by driving VR against itself. **JobFinder is OUT OF SCOPE**
(directive 2026-06-09). When all 4 are committed, the goal is met.

## The 4 tasks
| task | state |
|---|---|
| `installer-4-require-hugging-face-and-add-key-setup-panel` | ✅ **DONE** — committed `d38278c` |
| `sandbox-2-make-nono-a-required-dependency-of-the-installer-and-launcher` | reached stage 10/11; **Verify (stage 9) passed**, then Fix-verify timed out on the 20-min cap. Impl was sound — needs a re-drive with a larger cap (see below). |
| `sandbox-3-add-a-bypass-nono-checkbox-to-the-ui` | not yet completed; was blocked only by the `tree` crash (now fixed). Just needs a drive. |
| `parallelize-planning-across-tasks` | heaviest task (Author-tests 19m29s/97 turns); Implement exceeded the 20-min cap. Needs the larger cap. |

## Pipeline state / fixes applied this session (all committed)
- **`5bbcf15`** litellm `Connection: close` per model — kept (harmless; was aimed at a mis-diagnosed stall).
- **`41f81e8`** watchdog effectively DISABLED: `.relay/config.json` `firstOutputTimeoutMsByTier` = 1,800,000ms (30min) for all tiers, which is > the 20-min subagent cap, so the first-output watchdog never fires. WHY: its liveness signal (a trace FILE appearing in `--trace-dir`) false-killed healthy stages — swival writes that file only after its first turn, and heavy reasoning/Implement turns out-run any budget. The 20-min `SubagentTimeoutMilliseconds` is the sole stall backstop now. Proper fix = [[watchdog-real-liveness-signal]].
- **`tree` installed** (`brew install tree`) — swival fatally exits when a *whitelisted* command is missing. Real fix = [[swival-command-whitelist-degrade]].

## DO THIS FIRST on resume (the one config tweak that unblocks the remaining 3)
The heavy tasks legitimately exceed the **20-min per-stage cap** (not deadlocks — confirmed:
sandbox-2 Fix-verify was running `dotnet test` at 74% CPU at the 20-min mark; parallelize
Author-tests took 19m29s). Raise it in `.relay/config.json`:
- add `"subagentTimeoutMs": 2400000` (40 min)
- bump `firstOutputTimeoutMsByTier` to `3600000` (60 min) so the watchdog stays > the new cap (disabled)
Then re-drive sandbox-3, sandbox-2, parallelize (pattern: `.relay-scratch/drive-rest.sh`, or `./visual-relay run-task <root> <taskId>` each). Backend must be up: `tools/backend/backend.sh start` (readiness http://127.0.0.1:4000/health/readiness → 200). Deeper fix (optional): make the Fix-verify/Implement fix-loop use targeted tests (TestFileCommand) instead of the full suite each iteration, so heavy stages don't need 40 min.

## Open VR self-fix tasks (drive these through VR after the 4 land, or as desired)
- [[watchdog-real-liveness-signal]] (#19) — replace the trace-file signal so the watchdog can be re-enabled.
- [[swival-command-whitelist-degrade]] (#20) — stop shipping optional tools in whitelists / preflight required bins; + upstream swival report.
- [[nono-grant-swival-workspace-writes]] (#18) — make the nono sandbox actually usable (prereq before sandbox-2 makes nono "required" for real).
- gen-backend-config (#17): appeared to WORK this session (`backend.sh start` logged "config generated"); verify it's truly fixed, then close.

See also memory: `vr-stall-often-watchdog-false-kill`, `pipeline-mocks-process-layer-blindspot`.
