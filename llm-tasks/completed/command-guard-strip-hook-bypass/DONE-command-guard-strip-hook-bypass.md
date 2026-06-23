# Stop agents bypassing the commit-authority hook: a Swival command-middleware that strips `--no-verify`

During a run, stage agents must never commit on the project repo — the driver produces the single
sealed commit. `.githooks/pre-commit` enforces this: while `<repo>/.relay/ACTIVE/info.json` exists,
only a commit carrying `RELAY_COMMIT_TOKEN == nonce` may land. But the hook is **bypassable** — an
agent that hits the rejection just retries with `git commit --no-verify` (or `-n`), which skips all
client-side hooks and lands a bare, unsealed commit. `GitCommitter.Squash.cs`
(`SquashInRunCommitsAsync`) already reconciles any such stray commit into the sealed commit (the
backstop — keep it), but we want to **prevent** the bypass up front and let the existing hook steer
the agent.

Swival has a first-class interception point: **`--command-middleware COMMAND`** (see
`command_middleware.py` in the swival package). Before every `run_command`/`run_shell_command`,
swival invokes the middleware with a JSON payload on stdin and reads a JSON verdict on stdout. Add a
C# `VisualRelay.CommandGuard` tool, wired as that middleware, that **strips git hook-bypass flags**
so the existing per-repo authority hook re-engages. Validated live this session: stripping
`--no-verify` makes the hook reject the agent's commit — both outside and inside the nono sandbox.

## Facts established by testing (bake in; do not re-derive)

- **Protocol** (`command_middleware.py`): payload is
  `{phase:"before", tool, cwd, mode, command, timeout, is_subagent}`. `mode` is `"shell"` (`command`
  is a **string**) or `"argv"` (`command` is a **list of strings**). Response:
  `{"action":"allow"}` (pass through), `{"action":"allow","mode":<shell|argv>,"command":<string|list>}`
  (rewrite — `mode` is required and the `command` type must match it, else swival fails open), or
  `{"action":"deny","reason":...}` (block; `reason` is shown to the agent). The middleware subprocess
  has a hard **10s** timeout.
- **Two command tools, both must be handled.** `run_command` arrives `mode:"argv"` (command is a
  token **list** → stripping is just list-element removal, no parsing/re-quoting). `run_shell_command`
  arrives `mode:"shell"` (command is a **string** → remove only the flag token(s) surgically and keep
  the rest byte-exact; do NOT shlex-rejoin the whole string). The agent picks either tool.
- **Scoping (the trap).** Strip `--no-verify` from any `git` command; strip `-n` **only** when the
  git subcommand is `commit` — because `git push -n` is `--dry-run`, `git merge -n` is `--no-stat`,
  and non-git `-n` (`sort -n`, `grep -n`, `head -n`, `echo -n`) means something else entirely. When
  finding the subcommand, skip `git`'s own option args (`-C <dir>`, `-c <k=v>`, `--git-dir=…`). Handle
  combined short flags (e.g. `-nm`). Never touch a non-git command. (Probed all of these.)
- **Scope is automatic / repo-agnostic.** The middleware does NOT resolve which repo a command
  targets. It re-arms hooks everywhere; the *per-repo* hook self-scopes — the project repo has
  `.githooks/pre-commit` + `.relay/ACTIVE` so its commits are rejected, while a scratch repo the
  agent `git init`s has no such hook so its commits proceed. Agents keep full git freedom on any repo
  **other** than the one under an active run (git-tooling tasks, temp repos), which is a requirement.
  Stripping `--no-verify` on a scratch commit is harmless (no hook there).
- **Fail-open is inherent.** Swival treats ANY middleware failure (missing exe, >10s, non-zero exit,
  bad JSON, unknown action/mode, wrong command type) as `allow` with the ORIGINAL command → the
  bypass survives. So the guard must be robust and fast, and `GitCommitter.Squash.cs` stays as the
  floor.
- **Not covered by the strip** (covered by the squash, out of scope here): hook-less plumbing
  (`git commit-tree` + `update-ref`, `fast-import`), `-c core.hooksPath=…` overrides, and deleting /
  `chmod -x`ing the hook. The strip targets the common `--no-verify`/`-n` path.
- **Under nono.** Swival (`nono run --profile <abs> -- swival …`) DOES invoke the middleware inside
  the sandbox and honor its verdict (tested with the live `vr-guard` profile). The wrapper + binary
  must sit on a path the `vr-guard` profile permits.

## Goal

An agent can no longer bypass the project repo's authority hook with `git commit --no-verify`/`-n`.
`VisualRelay.CommandGuard`, wired as swival's `--command-middleware`, strips those flags (correctly
scoped, both `argv` and `shell` modes) so the hook re-engages and rejects the agent's commit with its
existing steering message. Scratch/other repos are unaffected. `GitCommitter.Squash.cs` stays as the
backstop for everything the strip does not cover.

## Approach (suggested)

1. **New project `tools/VisualRelay.CommandGuard`** (Option 2 — dedicated, registered in
   `VisualRelay.slnx`; mirror `tools/VisualRelay.CheckCommitMessage`'s csproj: `OutputType=Exe`,
   `net10.0`, `ProjectReference` to `VisualRelay.Core`). **No NativeAOT** — a normal self-contained or
   framework-dependent publish is fine (~150ms startup is acceptable for the command volume).
2. **Pure, IO-free strip logic in `VisualRelay.Core`** (e.g. `CommandGuard/` split to stay ≤300
   lines): a `Decide(payload)`-shaped function returning allow / allow-rewrite(mode, command). The
   `argv` path filters the token list; the `shell` path tokenizes (shlex-equivalent) to LOCATE the
   flag tokens, then removes them from the original string surgically. Apply the scoped strip above.
3. **`tools/VisualRelay.CommandGuard/Program.cs`**: read stdin JSON → `Decide` → write the verdict
   JSON → exit 0. On ANY internal error, emit `{"action":"allow"}` (match swival's fail-open; never
   break the agent).
4. **A tiny `sh` wrapper** (the value passed to `--command-middleware`), **≤20 logic lines** (the
   shell-size gate), that execs the published guard binary. Precedent: `.githooks/commit-msg` execing
   `check-commit-message/`.
5. **Build tie:** the project is in `VisualRelay.slnx` (built by `./visual-relay build`/`check`/
   `launch`). Add an ensure-step mirroring `NonoProfileEnsurer.EnsureAsync` (called at run start in
   `RelayDriver`, gated the same way) that publishes the guard to a stable gitignored path **before**
   swival launches — guaranteeing it is current even on a fresh checkout (same precedent as publishing
   `check-commit-message` at install/launch). Incremental: a no-op when up to date.
6. **Wire into the swival launch:** in `ProcessRunners` (where the swival argv / nono prefix is built
   — `BuildNonoPrefix` / the launch target), append `--command-middleware <wrapper-path>`. Make sure
   the wrapper + binary path is reachable inside the `vr-guard` nono profile (grant it in
   `packaging/nono/vr-guard.json` if needed).
7. **Leave `GitCommitter.Squash.cs` as-is** (the backstop).

## Files

- new `tools/VisualRelay.CommandGuard/{Program.cs, VisualRelay.CommandGuard.csproj}`
- new `src/VisualRelay.Core/CommandGuard/*.cs` (pure strip logic; split to ≤300 lines)
- new wrapper script (the `--command-middleware` target, ≤20 logic lines)
- new `src/VisualRelay.Core/Execution/CommandGuardEnsurer.cs` (sibling of `NonoProfileEnsurer`) + the
  `RelayDriver` run-start call
- `VisualRelay.slnx` (register the project)
- `src/VisualRelay.Core/Execution/ProcessRunners*.cs` (append `--command-middleware`)
- `packaging/nono/vr-guard.json` (grant the wrapper/binary path if not already covered)
- tests (below)

## Tests (TDD — write the failing tests first)

- **Strip scoping units:** `git commit --no-verify -m x`, `git commit -n -m x`, `git -C /r commit -n
  -m x` → flag removed; `git push -n`, `git merge -n` → `-n` KEPT; `git push --no-verify` → only
  `--no-verify` stripped; `sort -n file`, `grep -n foo`, `head -n 5 f`, `echo -n hi`, `ls -la` →
  UNCHANGED; combined `-nm` / `-n -m`.
- **Both modes:** argv (list in → filtered list out, `mode:"argv"`); shell (string in → only the flag
  token(s) removed, the rest byte-identical, `mode:"shell"`).
- **Robustness:** malformed / unexpected payloads → `{"action":"allow"}` with no throw.
- **Ensure-step:** publishes the binary to the expected path; a second call is a no-op when current.
- **Integration (may be a documented manual check):** a temp repo with `.githooks/pre-commit` + a
  `.relay/ACTIVE` marker → a stripped `git commit` is rejected by the hook; a scratch repo (no hook) →
  the commit proceeds.

## Sequencing

Self-contained; complements (does not replace) the existing `GitCommitter.Squash.cs` backstop and the
`.githooks/pre-commit` authority hook, and reuses the `NonoProfileEnsurer` run-start pattern. No
dependency on other queued tasks.

## Done when

- `VisualRelay.CommandGuard` exists and is registered in `VisualRelay.slnx`; the pure strip logic is
  unit-tested across all scoping + both-mode cases above (tests fail against absent code, then pass).
- The swival launch passes `--command-middleware`; the ensure-step guarantees the binary is current
  before swival starts; the wrapper is ≤20 logic lines.
- Manually (or via the integration test): an agent's `git commit --no-verify` on the project repo
  during a run is rejected by the re-armed hook, while the same on a scratch repo still works.
- `GitCommitter.Squash.cs` is unchanged and still green.
- `./visual-relay check` is green; all files under 300 lines; shell scripts ≤20 logic lines;
  Conventional Commit subjects.

## Notes

- **Fail-open is inherent** to `--command-middleware`; the squash is the floor. Optional hardening
  (decide during implementation): make the wrapper fail-**closed** for git — emit `{"action":"deny"}`
  for a git command when the binary errors — so a broken guard blocks git but not other commands.
- **Out of scope** (covered by the squash): hook-less plumbing (`commit-tree`/`update-ref`/
  `fast-import`), `-c core.hooksPath=…`, and hook-file deletion.
- **No NativeAOT** (decided) — accept ~150ms startup.
- Steering comes free from the existing `.githooks/pre-commit` rejection message; no
  middleware-authored reason is needed for the strip path.
- Keep `VisualRelay.CommandGuard` GENERAL — it ships with VR and applies to any repo VR drives;
  per-repo scoping is the hook's job, not the middleware's.
