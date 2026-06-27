# Make the Fix-verify loop converge-or-bail (one authoritative verify, fed to the agent; stop burning all attempts on a non-converging verdict)

The stage-10 **Fix-verify** loop decides pass/fail from a **second, separate
execution** of the test command run by the orchestrator — independent of what the
agent itself verified. When the two disagree, the loop cannot converge: the agent
(seeing green) makes no change, the orchestrator (seeing red) re-enters, and the
loop burns every `MaxVerifyLoops` attempt before **flagging a task whose tests
actually pass**.

Captured live (run `20260617211929-task-pending-and-complete`, JobFinder) and
analyzed to source — not a hypothesis.

## Confirmed incident

`bun test --timeout 15000`:

- `stage_start name=Fix-verify` fired **5 times**, `stage_done` 5 times. Every
  pass ended `verify_retry reason=transient-fault` → `stage_done` → immediate
  `stage_start` (full stage re-entry).
- **0** `verify_retry_pass` / **5** `verify_retry` — the retry never once flipped
  red→green.
- The loop ran **~42 min** (21:37:19 → 22:19:23) and cost **~$0.13** (sessionCost
  $0.52 → $0.65), then `error s10 flagged reason=verify failed after 5 fix-verify
  attempts`.
- **Smoking gun** — final attempt, seconds apart:
  - `22:19:19  agent run "cd … && bun test --timeout 15000" → 6591 pass  0 fail` (exit 0)
  - `22:19:22  verify_retry reason=transient-fault`  ← orchestrator's own run: **nonzero**
  - `22:19:23  flagged: verify failed after 5 fix-verify attempts`

So at the same minute, **agent = green, orchestrator = red**, and the task was
failed anyway.

## What was RULED OUT (do not chase these)

A read-only investigation resolved both launches with `nono --dry-run` and
compared them to source. **The sandbox configuration is byte-for-byte identical**
between the agent's launch and the orchestrator's verify launch:

- Same `vr-guard` profile, same resolved capability set, same `--allow-cwd`, same
  working directory (`rootPath` == the agent's `TargetRoot`,
  `RelayDriver.VerifyFix.cs:93-95`).
- Same environment overlay: `ProcessCapture` **augments** (never replaces) the
  parent env and both paths apply the same `BuildSandboxEnvironment(config)`
  overlay (`ProcessCapture.cs:74-80`, `ProcessRunners.Helpers.cs:63-78`). No
  PATH/HOME/DOTNET_/BUN_/credential-var divergence exists in the code.
- The only nono-flag difference is the agent's `--rollback --no-rollback-prompt`
  pair (`ProcessRunners.Helpers.cs:89`).

**Confirmed by running the real `nono 0.61.1` binary on the host** (it lives in
the nix store / this repo's devshell, `flake.nix:28` — not a VM-only tool):

- Both launches emit **byte-identical** credential-block WARNs (`.ssh`, `.aws`,
  `.gnupg`, `.gcloud`, `.kube`, …) and `Verified 1 pack(s)`. So that noise is a
  **red herring for the exit code** — identical on both sides, unrelated to the
  divergence.
- `--rollback` does **not** discard writes: a file written under
  `--rollback --no-rollback-prompt` survived the process exit. So rollback is not
  silently undoing the agent's fixes.
- `rootPath` is the **main repo itself** (`RelayDriver.cs:28`,
  `Path.Combine(rootPath, ".relay", taskId)`); tasks serialize via
  `ActiveTaskLock`, so the agent (`cd BASEDIR`) and the orchestrator verify run in
  the **same directory** — no per-task worktree, no cwd divergence (which is also
  why a flagged task's edits are reverted in place and are gone afterward).
- The base suite is **deterministic** on a clean tree: two orchestrator-style runs
  returned identical `6574 pass / 0 fail`, exit 0.

The cause is therefore *what is executed and whether re-executing it is stable* —
NOT the sandbox, rollback, cwd, or environment.

## Root cause

### R0a (primary, general) — the agent and the gate verify DIFFERENT commands
By design the agent is pointed at a **targeted subset** while the orchestrator
gates on the **full suite**:

- Orchestrator: `RunTestCommandWithRetryAsync` runs `config.TestCommand` — the
  whole suite (`RelayDriver.Bootstrap.cs:73`, fed into the verdict at
  `VerifyFix.cs:155,164`).
- Agent: the prompt's verify section and stage system prompts tell the LLM to run
  `invocation.TestCommand` = `BuildTargetedTestCommand` = `TestFileCommand` with
  `{files}` substituted from the manifest's test files
  (`RelayDriver.Artifacts.cs:159-168`, `ProcessRunners.Helpers.cs:126-129`,
  `RelayStages.cs:51-60`).

A change that breaks a test **outside** the manifest's targeted files → the agent
sees green on its subset, the orchestrator sees red on the full suite,
*deterministically*. This is the textbook "agent green / authoritative red"
divergence and it will bite **any** repo whose `testFileCmd` contains `{files}`.

> Note for *this* incident specifically: the agent went off-script and ran the
> **full** `bun test` itself (its logs show "6591 pass … across 168 files"), so
> scope was not the *active* trigger here — but it is a real latent bug, and it is
> the divergence that a general fix must close.

### R0b (active trigger here) — the gate re-executes a NON-IDEMPOTENT verify after the agent's, and the loop can't tell flaky from real
The verdict comes from the orchestrator running the test command **a second
time**, independently, after the agent's own run. That is only safe if the verify
is idempotent and the tree is stable between the two runs — and neither holds here:

- **The suite self-mutates.** Running `bun test` once writes tracked files it also
  asserts on (observed on the host: `TEST-TIMING.md`,
  `tests/results/ratchet-status.json` — "ratchet"/timing baselines), and one host
  run flaked outright (`exit 2`, no output, then clean on retry). So the agent's
  run and the orchestrator's re-run can see different state from the *first* run's
  side effects alone.
- **The tree is shared and unisolated.** `rootPath` is the **live main repo**
  (`RelayDriver.cs:28`) — there is no per-task worktree. So anything else writing
  to that repo while a task runs (the user's own app, another process, IDE
  tooling) perturbs the verify tree between the agent's run and the gate's. This
  was demonstrated live (a concurrently-running JobFinder app modified
  `data/companies.json` during investigation). **Caveat: the app was NOT running
  during the actual 3-task incident**, so this is a **latent fragility, not a
  cause of the observed failures** — but it is a real divergence vector the design
  should close.

So the agent's run and the orchestrator's re-run operate on **different on-disk
state**, and either can land on a different result. The loop has **no mechanism to
distinguish a flaky/order-dependent failure from a real one** (e.g. re-run the
failing test in isolation and compare), so it treats every re-run red as a hard
gate failure.

**This is systemic, not a one-off:** in the same batch, **three** tasks flagged
with the identical signature — `task-pending-and-complete`,
`autofill-fix-ashby-other-4`, and `fix-job-sb-energy-…` — each with the agent's
final summary asserting "all tests pass / 0 fail" while the orchestrator flagged
`verify failed after 5 fix-verify attempts`, on three unrelated code areas. A
large fraction of a real run's tasks are being failed this way.

### R0c (the generalizable insight) — the gate reduces the whole run to ONE bit (exit code), conflating "a test failed" with "the process exited nonzero for another reason"
The verdict is `testResult.ExitCode == 0 ? green : red` (`VerifyFix.cs:164`). The
exit code is the **only** signal universal across test runners, but it is lossy: a
suite can exit nonzero for reasons that are **not** a failed test and that the fix
agent cannot address by editing application code — a perf/wall-clock ceiling, a
lint/coverage ratchet, or a setup/teardown hook that throws *after* every test has
already reported pass. (Confirmed real and by-design in JobFinder: `tests/setup.ts`
prints `0 fail`, then an `afterAll` throws a wall-clock-ceiling error → nonzero
exit.) The orchestrator has **no general way** to tell these apart from a real
failure — per-runner output parsing is not general — so it loops the fix agent
against something the agent can neither see (it ran the targeted subset) nor fix,
and a summary that says "pass" while the exit code says "fail" is agent-green /
gate-red **by construction**. The fix is therefore **not** to make the orchestrator
parse results; it is to make the **agent** the interpreter (Approach 1b) and the
orchestrator **bail gracefully** when the agent can't move the verdict
(Approach 2–3).

### R1 — the failure shown to the fix agent is raw, noise-buried
`BuildFailureOutput` (`RelayDriver.RepoGuards.cs:271-292`) appends
`testResult.Output` **verbatim** (line 280) into the next attempt's prompt. The
nono-WARN distiller `ExtractFailureReason` (`ProcessRunners.Diagnostics.cs:72`) is
applied **only** to the subagent-crash path (`ProcessRunners.RunAsync.cs:226-229`),
never to the verify output. So even though the WARN noise is benign, it dominates
what the agent sees, burying the real failing line — the agent reasonably calls it
"benign / no changes needed." (Independent of R0; fixing it makes the agent
effective regardless of the divergence cause.)

### R2 — the retry is mislabeled "transient-fault" and is an identical immediate re-run
`RunTestCommandWithRetryAsync` (`RelayDriver.Bootstrap.cs:69-94`) emits
`verify_retry reason=transient-fault` for **any** nonzero exit and re-runs the
**identical** command immediately. For a deterministic divergence (or a flake
whose window exceeds the retry gap) it never flips — 0/5 here — pure wasted cost,
and the hardcoded "transient-fault" label misleads diagnosis.

### R3 — no convergence/divergence guard; the loop burns the full cap then fails
`RunVerifyFixLoopAsync` (`VerifyFix.cs:82-202`) loops `attempt = 1..maxLoops`
(`MaxVerifyLoops` default **5**, `RelayConfigLoader.cs:22`) and only exits early on
`check == "green"` (line 190). It never detects the non-convergence signal:
*the agent reported success and produced **no working-tree change**, yet verify is
still red for the same reason.* That cannot improve by looping; it should bail at
once with an actionable flag, not exhaust 5 attempts then emit the misleading
`verify failed after {maxLoops} fix-verify attempts` (`VerifyFix.cs:199-200`).

### R4 — the inactivity watchdog cannot catch this (context)
Each attempt is genuinely CPU-active (`watchdog_heartbeat … lastPulseSource=cpu`),
so `ActivityWatchdog` (`ProcessRunners.Watchdog.cs`) never trips. An
*active-but-not-converging* loop is invisible to an inactivity watchdog;
`MaxVerifyLoops` is the only bound, and it's expensive. (Complements
`DONE-swival-first-output-watchdog.md`, which covers the *silent* stall.)

### R5 — the verify failure is not observable after the fact (this blocked root-causing)
The orchestrator's authoritative verify is the thing that decided every red, yet
its result is **not recorded as first-class evidence**. The only signals in
`run.log` are `check: red` (in the seals/status) and the **hardcoded**
`verify_retry reason=transient-fault` — which carries neither the exit code nor
what failed. The actual failing output is only spliced into the *next* agent
prompt (`## Failing verify output`, trimmed) and is not cleanly extractable
afterward. Net effect: **the exact cause of each incident red cannot be proven
from the logs** (and the trees are reverted, so it can't be reproduced either).
An under-observable gate is both a root problem and the reason this investigation
could narrow but not finish the diagnosis.

## Goal

The Fix-verify loop must **converge or bail**, and must **tolerate suites that are
working as designed** — including suites that legitimately **exit nonzero while
reporting zero failed tests** (perf/wall-clock ceilings, lint/coverage ratchets,
throwing setup/teardown hooks) and suites that **write files during a run**. It
must never burn every attempt on a verdict the agent cannot move, and never fail a
task whose gate the agent has actually satisfied:

1. The orchestrator gates on the **process exit code** — the only signal universal
   across test runners — and stays the mechanical authority; the agent's
   self-report never decides the verdict.
2. The **agent** is the universal interpreter (no per-runner parsing in the
   orchestrator): on the gate stage it runs the **full** gate command and reads
   **both** the pass/fail summary **and** the exit code, then resolves *every*
   reason the process exited nonzero — a real test failure, a perf ceiling, a lint
   ratchet, a throwing hook — **legitimately** (never by deleting tests or
   weakening assertions to beat a gate). This is what makes "agent-green /
   gate-red" structurally impossible.
3. A non-deterministic (flaky) or non-convergent verify — one the agent provably
   cannot move (tree unchanged, same failure) — is detected and **bailed at once**
   with an actionable reason, not looped into a max-attempts failure.
4. The verify tree is **isolated** so a suite's file-writes can't perturb the gate
   or pollute the repo; incidental mutations are surfaced as advice, not failures.
5. The failure shown to the agent must be the **real** one, not advisory noise.

## Approach (suggested)

0. **Make the gate observable FIRST (R5 — prerequisite for everything else).**
   Emit a structured `verify_result` event for every authoritative verify with:
   the exact command run, its **exit code**, a **distilled failure reason**
   (via `ExtractFailureReason`), the working-tree hash, whether it came from test
   vs guard vs bootstrap, and a **pointer** to a full-output file. Specifically:
   - **Persist the FULL, untrimmed output to a per-attempt artifact file**
     (e.g. `stageN-attemptM.verify-output.txt`, mirroring the existing
     `killed-output.txt` precedent at `ProcessRunners.Helpers.cs:221-267`).
     `SandboxedTestRunner` persists **nothing** today (`SandboxedTestRunner.cs`
     returns the output in memory only) — this is the gap that made the incident
     unprovable. Put the bytes in the file and only a path in the event; dumping
     full test output inline is what bloated `run.log` and buried the real failure.
   - **Capture stdout and stderr separately (or tag each line by stream).**
     `ProcessCapture` merges them into one buffer today (`ProcessCapture.cs:86-87`)
     but already knows the source (it tags activity `"stdout"`/`"stderr"`). Keeping
     them apart matters because the nono credential WARN noise is on **stderr**
     while the test runner's real pass/fail is on **stdout** — so stream
     separation is a cleaner fix for the noise problem (R1 / step 4) than
     after-the-fact regex stripping.
   - **Detect and surface incidental file mutations by the verify.** Diff the
     tracked tree immediately before and after the gate run (pure git — no
     per-runner knowledge). If the test command wrote tracked files, emit a
     `verify_mutated_tree` advisory naming them with guidance ("the test command
     wrote `TEST-TIMING.md`, `ratchet-status.json`; verification is
     non-idempotent — gitignore these or use a non-writing test command; VR ran
     the gate in an isolated tree so the repo is unaffected"). This is the
     general, hand-delivered version of the JobFinder hygiene task — it turns a
     silent fragility into a one-time-fixable signal.

   This is also the data the convergence guard (2) and flaky-detection (3) consume
   anyway — without it the *next* incident is just as unprovable as this one. Do it
   before the behavioral changes so they can be validated against real evidence.

1. **Make the agent fix against the full gate AND reconcile exit-code-vs-summary
   (closes R0a + R0c, the core fix).** Two linked changes:

   **(a) Escalate to the full gate command on Fix-verify.** The contradiction
   today is explicit: the Fix-verify prompt hands the agent the **full-suite**
   failing output (`failingTestOutput`, `VerifyFix.cs:94`) but sets `## Verify
   command` to the **targeted subset** (`targetedTestCommand`) and instructs it to
   "run ONLY [that] … and do NOT broaden the command to a fuller gate"
   (`RelayStages.cs:78-86`). So the agent passes the subset and is structurally
   blind to the failures it was shown. Fix it where the contradiction lives:
   - Pass `config.TestCommand` (the full gate) as the agent's `## Verify command`
     for the **Fix-verify** stage instead of `targetedTestCommand`
     (`VerifyFix.cs:94`). Keep the fast targeted command for the earlier stages
     (Author-tests / Implement / Review / Fix) — only Fix-verify escalates.
   - Flip the Fix-verify instruction (`RelayStages.cs:78-86`) from "run ONLY the
     targeted command / do NOT broaden" to "the full suite is what's judged — run
     it (shown in ## Verify command) and resolve its **new** failures."
   - Point the agent at the **new** failures via the existing baseline diff
     (`GetNewFailuresAsync` / `config.BaselineVerify`) so it doesn't chase
     unrelated pre-existing breakage.

   **(b) Teach the agent to read the EXIT CODE, not just the summary (the general
   answer to "tolerate suites that exit nonzero despite passing").** The
   orchestrator gates on the exit code and, per R0c, cannot generally tell a failed
   test from a non-test gate. The agent is the only general interpreter that can.
   So the gate-stage prompt must instruct it to:
   - treat **a nonzero exit as a real, unfinished failure even when the summary
     says `0 failed`**, and inspect the output tail for the non-test gate that
     caused it (perf/wall-clock ceiling, lint/coverage ratchet, a throwing
     setup/teardown hook);
   - **resolve it legitimately** — genuinely speed up slow work, fix the ratcheted
     lint, etc. — and **NOT** by deleting tests, weakening assertions, or otherwise
     gaming the exit code;
   - if a nonzero exit is **not safely fixable** within the task's scope, **report
     it as a non-test gate** (a clear, distinct signal) rather than hack around it,
     so the orchestrator's bail (Approach 2–3) surfaces an actionable reason.

   Add the same "passing summary + nonzero exit = not done" framing to the earlier
   test-running stages (Implement / Fix) so it is usually resolved **before**
   Fix-verify is even reached (shift-left). Note this only durably clears
   *consistent* gates — a ceiling the suite *varies around* still needs the
   flaky/convergence bail below.

   The orchestrator stays the mechanical authority (the agent's self-report never
   decides the verdict); these changes only make the agent *fix against the same
   command and the same success criterion (exit 0) the gate judges*. Fully general
   — **no per-runner parsing, no per-repo config.**

2. **Add a convergence guard (R3).** Track the working-tree hash across attempts
   (`WorkingTreeHash` already exists, `VerifyFix.cs:175`). If an attempt leaves the
   tree unchanged **and** verify is still red **and** the distilled failure
   matches the prior attempt's, stop immediately and flag with a specific reason,
   e.g. `verify non-convergent: working tree unchanged, same failure persists
   (<distilled reason>)`. Bails on attempt 2 (~$0.10 / ~30 min saved here).

3. **Distinguish flaky from real (R0b, R2).** When the gate verify is red, re-run
   just the failing test(s) once; if the result flips, label it flaky (emit a
   `verify_flaky` signal, surface it), don't treat one flaky red as a hard gate
   failure, and don't blindly re-run the whole suite as a "transient-fault." Only
   call a retry "transient" when it actually flips red→green. Track flip rate so a
   chronically-flaky suite is visible rather than silently looped.

4. **Distill the failure shown to the agent (R1).** Run `testResult.Output`
   through the same `ExtractFailureReason`/nono-WARN stripper before it enters
   `BuildFailureOutput`, so the agent sees the real failing test/lint lines, not
   credential-block noise. Persist the full raw output as a breadcrumb (as the
   subagent-crash path already does).

5. **Drift-guard the shared sandbox launch (keep R-ruled-out true).** The two
   launches are identical *today*; add a unit test asserting the agent and verify
   launches resolve to the same `(nono profile, capability set, cwd, env overlay)`
   for a given config (allowing only the documented `--rollback` difference) so
   they can't silently drift apart later.

6. **Isolate the verify tree so the suite's file-writes are harmless (now
   central, not optional).** Because suites legitimately **write files during a
   run** — JobFinder rewrites `TEST-TIMING.md` and ratchet/results JSON every run
   *by design*, not by bug — the orchestrator must stop assuming verify is
   side-effect-free. Run the authoritative verify against an isolated copy of
   `committed tree + the agent's diff` (a throwaway git worktree / snapshot)
   instead of the live `rootPath` (`RelayDriver.cs:28`):
   - The suite can scribble freely in the copy; the real repo is never polluted,
     and the agent's actual fix (in the real tree) is untouched — so a later commit
     keeps the fix and drops the test's incidental writes. (This is also how VR
     "discards" the mutations without having to know *which* files are incidental
     vs. the real fix.)
   - Each verify starts from the same known state, so non-idempotent writes don't
     compound across attempts and a concurrent external writer can't perturb the
     gate.
   Pair it with the mutation advisory (Approach 0) so the human is also told how to
   stop the writes durably. (Previously sequenced last as a latent-risk item; the
   reframe — file-writing is *intended* and will keep happening — makes this the
   primary, general way VR tolerates it. Still the most invasive change, so build
   it after observability (0), but it is no longer optional.)

## Acceptance

- A regression test where the gate verify returns red while the agent reports
  green and the working tree is unchanged across attempts → the loop **bails on
  attempt 2** with the non-convergence flag, not all `MaxVerifyLoops`.
- A test where a failing test passes on re-run → it is labeled flaky and does not
  by itself hard-fail the gate.
- A test asserting that for the **Fix-verify** stage the agent's `## Verify
  command` equals the full `config.TestCommand` (not the targeted subset) for a
  config whose `testFileCmd` contains `{files}`, while the earlier stages
  (Implement / Fix) keep the targeted command — so the agent fixes against the
  gate's scope without slowing the fast inner loop.
- A test asserting the failure fed into the fix-verify prompt has nono advisory
  WARN spam stripped (`deny_*` / "Verified N pack(s)" gone; the real reason
  present).
- The launch drift-guard test (Approach 5).
- A test asserting the **Fix-verify** (and Implement / Fix) prompt instructs the
  agent to treat a nonzero exit with `0 failed` as a real failure to resolve, with
  the no-reward-hacking caveat (don't delete tests/weaken assertions to beat the
  gate) and the "report as a non-test gate if not safely fixable" fallback.
- A test asserting the authoritative verify runs in an isolated tree: a suite that
  writes a tracked file during the run leaves the real `rootPath` tree unmodified
  after verify, and a `verify_mutated_tree` advisory naming that file is emitted.
- No behavior change on the normal path: a genuinely red suite the agent *can* fix
  still loops and goes green; a genuinely green suite passes on attempt 1.

## Out of scope / notes

- The JobFinder-side cause is a **JobFinder** bug, tracked separately in that repo
  as `llm-tasks/bun-test-idempotent`. A read-only investigation
  found the real mechanism: `tests/setup.ts` registers a global `afterAll` that
  **throws a wall-clock ceiling** (`setup.ts:186-188`), so a full `bun test` can
  report `0 fail` in its summary yet **exit nonzero** because the hook threw —
  i.e. agent-green / gate-red by construction — and a normal run also rewrites
  git-tracked files (`TEST-TIMING.md`, `tests/results/ratchet-status.json`,
  `tests/results/console-ratchet.json`), making it non-idempotent. That is exactly
  the non-determinism Approach 2–3 must tolerate, but do **not** depend on fixing
  it to land this. (Correction: an earlier note here claimed a `relay-status` test
  reads the live `.relay/` directory — that is **false**; no JobFinder test reads
  `.relay/`.)
- Do **not** "fix" this by raising `MaxVerifyLoops` — that multiplies the waste.
  Detect non-convergence; don't hand the loop more rope.
- The exact agent-vs-gate exit-code flip could not be reproduced end-to-end
  (the `nono` binary + toolchain live on the VM, not the host); the sandbox-is-
  identical conclusion is from `nono --dry-run` + source, which is conclusive that
  the differ is command scope / flakiness, not sandbox config.
