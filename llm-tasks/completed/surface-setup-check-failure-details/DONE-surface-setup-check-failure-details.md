# Surface full diagnostics when a setup/validation test run fails

A real user onboarding his own repo (bun+vitest suite, green in ~23s in his terminal) hit
"test command timed out (timeout, exit code -1)" from the init/Create-config validation run.
The ONLY surfaces were a truncated status badge and a bottom-left toast. Neither the UI, nor
`GET /state` on the control API, nor any artifact he could find told him: what command actually
ran, from which directory, under what timeout, or what output it produced before being killed.
He could not distinguish "my test command is wrong" from "VR's execution environment differs from
my terminal" — and for automation driving VR through the control API, the failure is entirely
opaque: a screenshot of the window after such a failure contains no diagnostic content at all.

Operating principle for this task: **every failure must be diagnosable from the control API alone**
(`/state` + `/screenshot`). Artifacts on disk are good corroboration but cannot be the only record.

## Root-cause specifics from the reproduced case (verified in source, do not re-derive)

- `ProjectBootstrapper.BootstrapAsync` validates candidate test commands with a **hardcoded
  `TimeSpan.FromSeconds(5)`** (`src/VisualRelay.Core/Init/ProjectBootstrapper.cs:54`). Any real
  suite slower than 5s can never validate; the user's takes ~30s.
- On timeout `ProcessCapture.RunAsync` returns `(-1, output, timedOut: true)`;
  `TestCommandValidator.Classify` produces `Reject("test command timed out (timeout, exit code -1)", …)`
  — and `ResolveTestCommandAsync` **discards the RejectionReason** when falling through to the
  placeholder. It reaches no log, no file, no stdout; `Program.cs` prints only a fixed
  placeholder-used string. The GUI path surfaces the reason once as a truncated badge.
- Candidate order compounds it: for a Bun-lockfile repo whose tests are vitest via
  `scripts.test`, the detector tries Bun's native `bun test` before the package script.

This task must therefore ALSO: raise the validation timeout to something realistic and
configurable (e.g. default 60s, or reuse testTimeoutMs when a config exists; keep it bounded),
persist every candidate's rejection reason + output into the setup-check artifact (one section per
candidate), and prefer a `package.json` `scripts.test`-derived command over the bun-native
heuristic when both exist (general rule: an explicit project script beats an inferred runner).

## What to build

1. **Persist a setup-check artifact.** Wherever a validation/bootstrap/baseline test-command run is
   executed outside a task pipeline (the init/Create-config validation, `EnsureRunnableAsync`-style
   pre-run gates, baseline verify), on ANY failure (nonzero exit, timeout, spawn error) write
   `<root>/.relay/setup-check.log` containing: ISO timestamp, the exact command line, cwd, the
   effective timeout ms, exit code / timed-out flag, and the captured stdout+stderr (tail-truncated
   to a sane cap, e.g. last 64 KB, with a truncation marker). Overwrite per attempt; it is a
   diagnostic scratch file, not history.
2. **Expose the details in the view-model and `/state`.** Add a nullable structured field to the
   control API state (e.g. `setupCheck: { command, cwd, timeoutMs, exitCode, timedOut, outputTail,
   artifactPath, capturedUtc }`, null when the last setup check passed). `outputTail` capped
   (e.g. 4 KB) so `/state` stays lightweight; the full text lives in the artifact.
3. **Render the details in the UI** where the current badge/toast appears: keep the one-line status,
   add an affordance (expander or "Details" link) showing the command, cwd, timeout, exit summary,
   and scrollable output tail — so a window screenshot after a failure actually shows WHY. Follow
   the existing panel/expander idioms in the app; no new visual language.
4. **Actionable hint line.** Derive one short hint from the failure shape, generically (no
   language-specific logic): timed out → "the command exceeded <N>s; raise testTimeoutMs in
   .relay/config.json or verify the command finishes non-interactively in this environment";
   spawn/not-found → "the command's binary was not found on VR's PATH — VR runs commands through
   its own shell environment, which can differ from your terminal".

## Constraints

- General-purpose: nothing bun/npm/vitest-specific.
- Do not change when/whether validation runs — only how its failure is recorded and shown.
- `/state` additions must be additive (existing consumers unaffected).

## Tests (red first)

- Unit: a failing validation run (nonzero, and timeout separately) produces the artifact with all
  fields; passing run clears/nulls the state field.
- Control-API state test: after a simulated failed setup check, `/state` JSON contains the
  structured `setupCheck` with the output tail; after a passing one it is null.
- UI/view-model test in the existing style: failure populates the details view-model (command,
  timeout, tail); the one-line status remains.

## Verification

- `./test.sh` fully green including new tests.
- Manual/e2e proof recorded in the task ledger: with a deliberately failing testCmd (e.g. `sleep 999`
  under a small testTimeoutMs) in a scratch repo, `GET /state` shows the structured details and the
  window (via `/screenshot`) visibly renders them.
