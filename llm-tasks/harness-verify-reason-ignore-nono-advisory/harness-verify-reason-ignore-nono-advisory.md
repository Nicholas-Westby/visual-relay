# Harness: verify failure-reason must ignore nono advisory output (keychain/mach-lookup) and reflect the test command's own failure

## Problem

When a sandboxed verify (stage 9 / fix-verify stage 10) fails, VR distills a short
`reason` from the run output and shows it in the UI and feeds it to the fix-verify
agent. For a recent .NET task the reason was:

```
…system services: mach-lookup (com.apple.SecurityServer) — Keychain / Security framework
Keychain access requires granting the login keychain path: --read-file ~/Library/Keychains/login.keychain-db
…
```

That text is **nono's own standing advisory**: the `vr-guard` profile deny-lists
keychains/1Password (`deny_keychains_macos`) and prints this on **every** run.
(Verified: a plain `git status`/`git commit` under the profile prints it and still
exits 0 — nothing actually requested the keychain.) It is **not** the test failure.
Surfacing it sent the fix-verify agent chasing a non-existent keychain problem for ~30
minutes while the real failures (the 165 sandbox/profile-write test failures) sat lower
in the output and were truncated away.

## Current state (researched 2026-06-26 — re-grep anchors)

- The distiller is `SwivalSubagentRunner.ExtractFailureReason` → `DistillFailure`, in
  `src/VisualRelay.Core/Execution/ProcessRunners.Diagnostics.cs`. It **already** does the
  right *kind* of thing (codebase-agnostic): it strips some nono advisory lines (those
  containing both `is blocked by '` **and** `use --bypass-protection`), drops
  `Verified N pack(s)`, drops bare `deny_*` tokens, then anchors on framework-neutral
  failure markers (lines starting with `Failed `, a `\bFAIL\b` token, `command not
  found`, etc.) and finally `TrimForTail(…, 600)` keeps the **last ~600 chars**.
- Existing coverage:
  `RelayDriverVerifyFixTests.RunVerifyFixLoop_FailureOutputShownToAgent_HasNonoNoiseStripped`.
- **Two gaps let the keychain text through:**
  1. The nono **`system services: … mach-lookup (com.apple.SecurityServer) … Keychain
     access requires granting the login keychain path: --read-file ~/Library/Keychains/…`**
     block (and its `Next steps:` / `--allow`/`--read`/`--write`/`nono learn`/`nono why`
     hint lines) does **not** contain `use --bypass-protection`, so the existing filter
     misses it.
  2. nono prints that advisory **after** the test command's own summary
     (`Failed!  - Failed: 165, Passed: 1860, …`), so even when real `Failed …` markers
     exist earlier, the 600-char **tail** lands on nono's trailing advisory.
- Capture: `SandboxedTestRunner` runs `nono run … -- <test cmd>` and merges nono's
  stderr with the inner command's output into one `TestRunResult.Output` stream; there
  is no current separation between nono's chatter and the inner command's output.

## What to build (keep it codebase-agnostic — VR drives jest/pytest/go/cargo/xunit; do NOT parse any test framework)

- **Complete the nono-advisory filter** in `DistillFailure`: also drop nono's own
  `system services:` / `mach-lookup (com.apple.SecurityServer)` / `Keychain access
  requires granting the login keychain path` / `--read-file ~/Library/Keychains/…`
  advisory block and the trailing `Next steps:` / `Discover paths:` / `Query policy:` /
  bare `--allow`/`--read`/`--write` hint lines nono emits. These are VR's **own sandbox
  layer's** output — filtering them is provider-agnostic.
- **Bias the kept reason to the test command's own failure**: once the sandbox epilogue
  is filtered, the existing strong-marker anchor (`Failed!`, `Failed `, `FAIL`) should
  win; ensure the `TrimForTail` budget keeps the runner's failure summary rather than
  whatever sandbox lines trail it. A modest budget bump is acceptable if needed so a
  multi-failure summary survives — but the real fix is filtering the epilogue, not just
  enlarging the window.
- **Preferred durable option (only if not a large change):** capture nono's diagnostics
  separately from the inner command so they never merge into the reason in the first
  place. If that's a big change, the filter + tail-bias above is the accepted minimal fix.

## Tests

- Extend `…HasNonoNoiseStripped`: given combined output that contains the
  `system services: mach-lookup … Keychain access requires … login.keychain-db` block
  **followed by** a real `Failed!  - Failed: N …` summary, the distilled reason
  - contains the test runner's summary / a real `Failed <Test>` line, and
  - does **not** contain `mach-lookup`, `Keychain access requires`, or `login.keychain-db`.
- A green run still distills to empty (no regression).

## Done when

- The distilled verify reason for a sandboxed .NET run shows the real failing-test
  summary, not nono's keychain advisory.
- The existing nono-noise-stripping test stays green and gains the keychain-block
  assertions above.
- `./visual-relay check` green; changed file < 300 lines; Conventional Commit subject
  (e.g. `fix(verify): strip nono keychain advisory from distilled failure reason`).

## Notes / coordination

Independent of `harness-isolate-profile-write-in-driver-tests` (land in any order): that
task removes the **actual** test failures; this task fixes the **misleading report** of
them. This one does not change which tests pass — only what the agent/UI is told when a
verify is red.
