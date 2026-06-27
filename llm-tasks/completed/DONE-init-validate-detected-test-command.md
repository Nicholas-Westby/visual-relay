# init writes an unvalidated test-command guess (pytest for a bun repo)

`visual-relay init` against ~/Dev/JobFinder (bun.lock, bunfig.toml, package.json,
test.sh wrapping `bun test`) wrote `"testCmd": "pytest"` (2026-06-10). pytest is not
the repo's test runner and is not even installed; the first pipeline Verify would have
died on a command-not-found against a freshly onboarded repo — the worst possible
first-run experience, on any repo whose detection misfires.

## Goal

`init` never persists a test command it hasn't proven: the detected command is executed
once (time-boxed) before writing; a command that can't even start (not found / usage
error) is rejected and detection falls back/escalates. The written config is always
runnable on that machine, or init says plainly that it couldn't determine one
(`"testCmd": null` + a visible message) rather than guessing.

## Approach (suggested)

- After detection (`LlmTestCommandFinder` / heuristics in `tools/VisualRelay.Init` +
  `RelayConfigWriter`), smoke-run the candidate with a short timeout in the target
  root. Accept exit 0; accept nonzero-with-test-output (failing tests still prove the
  runner exists — distinguish via output shape); reject command-not-found/127, usage
  errors, and timeouts at startup.
- On rejection: try the next candidate (lockfile/manifest heuristics outrank LLM
  guesses: bun.lock→`bun test`, package.json scripts.test, pytest.ini/pyproject→pytest,
  *.csproj→dotnet test, Cargo.toml→cargo test); exhaust → write null + message, never a
  guess.
- Surface the validation result in init's stdout (`testCmd validated: bun test
  (322 tests, 0.2s)` / `could not validate a test command`).
- Tests: fake process runner in the Init tool's suite — (a) detected command 127 →
  falls through to lockfile heuristic; (b) failing-but-real runner accepted;
  (c) exhaustion writes null with message; (d) validated command written verbatim.
