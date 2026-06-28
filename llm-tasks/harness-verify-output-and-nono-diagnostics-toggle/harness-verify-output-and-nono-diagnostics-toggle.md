# Harness/UI: quiet sandbox diagnostics by default (Settings toggle) + richer verify output to the stage

Two related changes to the verify path. (Background: nono prints a verbose diagnostic
banner/footer on sandboxed runs — e.g. "Sandbox blocked system services … com.apple.SecurityServer
… Next steps" — which is a known red herring: it appears on ANY sandboxed failure and is unrelated
to the real test/build error. It pollutes the captured output and, because the footer is ~590
chars, can fill the agent's ~600-char tail window so the real failure never reaches the agent.)

## Part A — Settings toggle for sandbox-diagnostics verbosity (default: quiet)
- Add a toggle in the **Settings screen**, e.g. "Verbose sandbox diagnostics".
- **Default = OFF / quiet:** suppress nono's diagnostic output by invoking `nono run` with the full
  silent flag **`-s` / `--silent`** (suppresses banner, summary, status, the WARN preflight list,
  AND the failure footer — leaving only the child command's own output). Confirmed available in the
  pinned nono v0.61.1; it is output-only and does **not** weaken the sandbox (enforcement and the
  child exit code are unchanged).
- **Toggle ON = verbose:** do NOT pass `--silent`; show nono's full diagnostics (banner + footer)
  for diagnosing sandbox/VR-itself issues.
- Frame the setting **generically** (a "verbose diagnostics" preference). For now its only effect is
  selecting the nono `--silent` flag, but the name/intent should generalize.
- **Wiring:** the nono prefix is built in `BuildNonoPrefix`
  (`src/VisualRelay.Core/Execution/ProcessRunners.cs:135`, currently
  `["run", "--profile", <profile>, "--allow-cwd"]`). Add `--silent` there when the toggle is OFF.
  The setting is a global/app preference (Settings screen), not per-repo `.relay/config.json`, so
  thread it from the App settings store into the engine's nono-prefix builder; pick the cleanest
  plumbing. Applies to both the verify (`SandboxedTestRunner`) and swival-stage nono invocations.
- **Tradeoff (accepted by this default):** `--silent` also hides nono's capabilities banner, which
  is useful when a *real* denial breaks a run — that's exactly what the verbose toggle restores.

## Part B — richer verify output handed to the stage agent (independent of Part A)
In the Verify/Fix-verify prompt assembly — `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs:106-114`,
the `## Verify output` section built from `TrimForTail(invocation.LastTestOutput)`:
- **At least TRIPLE the tail:** raise `TrimForTail`'s window from ~600 to **≥1800 chars** (e.g. 2000)
  so the pass/fail summary and real errors survive any trailing noise.
- **Also pass the full-output file PATH:** the harness already persists the complete captured output
  to `stage{N}-attempt{M}.verify-output.txt` (`TryPersistVerifyOutput`,
  `src/VisualRelay.Core/Execution/RelayDriver.VerifyObservability.cs:52-69`), but that path is NOT
  given to the agent. Thread it into the invocation/prompt and add a line such as
  "Full output: <abs path> — read it for the complete log." so the agent can scan the whole thing
  (restores the persist-and-scan principle). The file is under the repo cwd, so it's readable under
  `--allow-cwd`; confirm the stage's command whitelist permits reading it.

## Done when
- Settings screen has the diagnostics toggle; default quiet (nono `--silent`), toggling on gives
  verbose nono output; the sandbox stays strict either way.
- The Verify/Fix-verify agent input contains a ≥3× tail AND the full-output file path.
- General-purpose: no test-framework/VR-repo specifics baked into the engine; the toggle keys on a
  generic preference, not a VR symbol.
- Tests cover: the `--silent` flag is present/absent per the setting; `TrimForTail` window ≥1800; the
  path appears in the prompt. `./visual-relay check` green; suite green under nono; Conventional Commits.
- Keep the existing `IsNonoSystemServiceAdvisory` reason-stripper as defense-in-depth.
