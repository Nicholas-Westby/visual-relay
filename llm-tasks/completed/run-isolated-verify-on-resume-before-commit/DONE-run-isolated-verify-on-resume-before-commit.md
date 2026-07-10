# Run the isolated verify gate on the resume path before committing

A resumed flagged task committed WITHOUT the authoritative isolated verify ever running. Evidence
from a real run (express, run id 20260710013317): the resume entered stage 11 Fix-verify, the agent
declared success from its own in-workspace test run, and the driver went straight to stage 12
Commit — run.log shows stage11 stage_start → stage_done → stage12 Commit with **no verify_result
event in between** and no verify-output artifact for that attempt. The normal pipeline path always
gates stage 12 behind `RunIsolatedVerifyAsync` green; the resume path skipped it. The committed
change happened to be genuinely green, but a resumed task whose fix "looks done" to the agent while
the isolated gate would still fail commits unverified — and resume is used precisely on tasks whose
verify was already failing.

## What to build

1. Locate the resume flow (the path taken by the `resume` command for a task with an existing
   NEEDS-REVIEW / prior flagged run) and make it run the SAME isolated verify gate as the normal
   pipeline before stage 12: green → commit proceeds; red → flag with the standard (enriched)
   reason and do NOT commit. Reuse `RunIsolatedVerifyAsync` — no parallel implementation.
2. Emit the same `verify_result` event + verify-output artifact on this path so a resumed run's
   log is indistinguishable in shape from a normal run's.
3. Guard against the class, not just the instance: audit for any OTHER path that can reach the
   stage-12 commit without a green isolated verify in the same run (e.g. skip-tests flows are
   allowed only where explicitly configured via skipTestsTaskIds — that exception stays; anything
   else must gate).

## Tests (red first)

- Driver test: a resumed flagged task whose testCmd fails cannot reach Commit — it re-flags, with
  a verify_result event recorded.
- Driver test: a resumed flagged task whose testCmd passes commits, and the run log contains
  verify_result (green) before the commit event.
- Regression: normal pipeline behavior unchanged (existing verify/commit tests stay green).

## Verification

- `./test.sh` fully green including the new tests.
