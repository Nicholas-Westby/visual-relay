# Enforce that only the driver's stage-11 commit can land during an active run

Visual Relay drives an LLM task through 11 stages, and every non-driver stage
shells out to the `swival` agent, which has full git shell access (the Review
stage needs the diff). Nothing stops a stage agent from running `git commit`
itself mid-run, and one did: the source landed in a rogue agent commit while the
driver's sealed commit carried only proof. The `Relay-Seal` trailer and `.seals`
chain are today **write-only provenance** — no hook or code verifies anything,
and the only existing hook (`.githooks/commit-msg`) checks Conventional Commit
subject format and nothing else. A prior workaround (commit `af6ae6a`) papered
over the symptom by folding rogue commits back in with `git reset --soft`; this
task replaces that with a real authority gate so rogue commits cannot happen in
the first place, then removes the fold.

## Current state (researched)

**The run-authority token already exists — it just isn't checked.** The driver
acquires `ActiveTaskLock` early in `RelayDriver.RunTaskAsync`
(`src/VisualRelay.Core/Execution/RelayDriver.cs:30`), before any stage runs, and
holds it for the whole run. `ActiveTaskLock.AcquireAsync`
(`src/VisualRelay.Core/Execution/ActiveTaskLock.cs:19-39`) creates
`.relay/ACTIVE/` and writes `info.json` with `{ task, pid, nonce }`, where
`nonce` is a fresh `Guid.NewGuid().ToString("N")`. The lock (and its `Nonce`
property) is in scope at the commit call site (`RelayDriver.cs:173`). Releasing
the lock deletes `.relay/ACTIVE/` (`ActiveTaskLock.cs:54-57`), so the presence of
`.relay/ACTIVE/info.json` is itself the "a run is active right now" signal.

**The driver's commit is the one sanctioned commit.** Stage 11 calls
`GitCommitter.CommitAsync` (`src/VisualRelay.Core/Execution/GitCommitter.cs:5`),
which stages the manifest, runs `git add -u`, force-adds the `.relay/<task>/`
proof triple, and writes the commit with a `Task:` / `Relay-Seal:` trailer
(`GitCommitter.cs:82-83`). All git is invoked through the private `GitAsync`
helper → `ProcessCapture.RunAsync`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs:154-211`), which builds a
`ProcessStartInfo` with `UseShellExecute = false`. `ProcessCapture` does **not**
currently touch `startInfo.Environment`, so a per-invocation env var can be added
without disturbing other callers.

**The fold workaround to remove (commit `af6ae6a`).** That commit added:
- `GitCommitter.CommitAsync`'s `string? runStartCommit` parameter and the
  `git reset --soft <runStartCommit>` block (`GitCommitter.cs:12,21-32`);
- `GitCommitter.TryGetHeadAsync` (`GitCommitter.cs:95-103`);
- the `runStartCommit` capture in `RelayDriver.RunTaskAsync`
  (`RelayDriver.cs:34-36`) and its pass-through to `CommitAsync`
  (`RelayDriver.cs:173`);
- the test `RunTaskAsync_WhenAnAgentCommitsMidRun_FoldsItIntoOneSealedCommit`
  and its `MidRunCommittingSubagentRunner` helper
  (`tests/VisualRelay.Tests/RelayDriverGitCommitTests.cs:79,187`).
The separate `git add -u` behaviour (`GitCommitter.cs:63`, commit `a269dc7`) is
**not** part of this workaround and must be kept.

**Hook installation today.** Visual Relay's own repo wires hooks via
`git config core.hooksPath .githooks` in the `install-hooks` case of the
`visual-relay` script (`visual-relay:68-71`). That only protects this repo. Repo
setup for *any* target repo goes through `RelayConfigWriter.Write`
(`src/VisualRelay.Core/Init/RelayConfigWriter.cs:11-25`), which writes
`.relay/config.json`. Both entry points call it: the `visual-relay init` CLI
(`tools/VisualRelay.Init/Program.cs:11`) and the in-app config-init command
`CreateConfigAsync` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs:106-108`).
A target repo may have no custom `core.hooksPath` (hooks live in `.git/hooks/`)
or its own; `commit-msg` and `pre-commit` are distinct files, so adding a
`pre-commit` does not disturb an existing `commit-msg`.

## What to build

A `pre-commit` git hook that enforces commit authority **only while a run is
active**, the driver-side token that satisfies it, installation of the hook into
every repo Visual Relay sets up, and removal of the `af6ae6a` fold. Write the
failing tests first.

**1. The `pre-commit` hook (`.githooks/pre-commit`).** A POSIX `sh`/`bash`
script that resolves the repo root and checks for `.relay/ACTIVE/info.json`:
- If it does **not** exist → `exit 0`. Human commits outside a run, and the
  Visual Relay self-repo when idle, are completely unaffected — do not lock the
  developer out.
- If it **does** exist → read the `nonce` value out of `info.json` and compare it
  to the `RELAY_COMMIT_TOKEN` environment variable. If they match, `exit 0`. If
  the env var is unset or differs, print a clear message to stderr (e.g. that a
  Visual Relay run is active and only the driver's stage-11 commit may commit —
  stage agents must not run `git commit`) and `exit 1`.
- Keep it dependency-light: parse the `nonce` with a small `grep`/`sed`-style
  extraction (the value is a 32-char hex guid), no `jq` requirement. Keep the
  file under 300 lines (it will be tiny) and mark it executable.

**2. The driver authorizes its own commit.** Pass the active lock's `Nonce`
(available at `RelayDriver.cs:173`) into `GitCommitter.CommitAsync`, and have
`GitCommitter` set `RELAY_COMMIT_TOKEN=<nonce>` **only on the `git commit`
`ProcessStartInfo`** for the sealed commit. Thread an optional env-var dictionary
(or single key/value) through `ProcessCapture.RunAsync` /
`GitCommitter.GitAsync` so the var is applied to that one process via
`startInfo.Environment` / `EnvironmentVariables`. It must **not** be exported to
the driver's own environment or to any other process: `SwivalSubagentRunner`
spawns `swival` through the same `ProcessCapture` (`ProcessRunners.cs:59`), and
child processes inherit the parent environment, so a globally-exported token
would be inherited by agents and defeat the check. Setting it on the single
commit `ProcessStartInfo` keeps it off agents (which never receive it and are
therefore rejected by the hook).

**3. Install the hook during repo setup.** Add a hook installer (e.g.
`HookInstaller` in `src/VisualRelay.Core/Init/`) invoked from the same place
`RelayConfigWriter.Write` is called — both `tools/VisualRelay.Init/Program.cs`
and the app's `CreateConfigAsync`
(`MainWindowViewModel.Execution.cs:106-108`) — so every repo Visual Relay is used
in gets the hook, not just this one. The installer must:
- resolve the **active** hooks directory: honour an existing
  `git config core.hooksPath` if set, otherwise default to `.git/hooks`;
- write a `pre-commit` hook there and `chmod +x` it;
- be **idempotent** (re-running install does not duplicate or error);
- **not clobber or destroy** a user's pre-existing `pre-commit` of a different
  origin — detect a foreign hook (e.g. one Visual Relay did not write, identified
  by a marker comment it stamps into its own hook) and leave it in place / surface
  a clear warning rather than overwriting it. A pre-existing `commit-msg` is
  untouched (different file).
Also add a `pre-commit` to this repo's own `.githooks/` and include it in the
`chmod +x` line of the `install-hooks` case in `visual-relay` so the self-repo is
protected too.

**4. Remove the `af6ae6a` fold workaround.** Delete the `runStartCommit`
parameter and the `git reset --soft` block from `GitCommitter.CommitAsync`,
delete `GitCommitter.TryGetHeadAsync`, delete the `runStartCommit` capture and
pass-through in `RelayDriver.RunTaskAsync`, and delete the
`RunTaskAsync_WhenAnAgentCommitsMidRun_FoldsItIntoOneSealedCommit` test plus the
`MidRunCommittingSubagentRunner` helper. **Keep** the `git add -u` behaviour
(`GitCommitter.cs:63`).

Scope: enforcement only. Do **not** add a `relay verify` command or full seal /
`.seals`-chain re-verification — the token/authority check is enough for now.

## Done when

- A `git commit` attempted **during an active run** (`.relay/ACTIVE/info.json`
  present) **without** a matching `RELAY_COMMIT_TOKEN` is **rejected** by the
  `pre-commit` hook with a clear message — this is the exact rogue-agent case.
- The driver's stage-11 commit, which sets `RELAY_COMMIT_TOKEN` equal to the
  active lock's `nonce` on its own `git commit` process only, **succeeds** and
  produces the single sealed `Task:` / `Relay-Seal:` commit.
- A commit with **no active run** (`.relay/ACTIVE/` absent) is **unaffected** —
  the hook exits 0, so ordinary human commits and the idle self-repo are not
  blocked.
- The token is set on the commit process only and is **not** inherited by
  `swival`/agents (not exported to the driver's environment).
- Hook installation runs during repo setup (both `visual-relay init` and the
  in-app config-init), is **idempotent**, **respects an existing
  `core.hooksPath`** (defaulting to `.git/hooks` otherwise), and **does not
  clobber** a pre-existing foreign `pre-commit` (and leaves `commit-msg` alone).
- The `af6ae6a` fold workaround is gone: no `runStartCommit`, no
  `git reset --soft` fold, no `TryGetHeadAsync`, and the
  `RunTaskAsync_WhenAnAgentCommitsMidRun_FoldsItIntoOneSealedCommit` test +
  `MidRunCommittingSubagentRunner` are deleted; `git add -u` is retained.
- Tests cover the hook/authority behaviour: rejection without token during an
  active run, acceptance of the driver's tokened commit, no-op when no run is
  active, idempotent install, and respect-existing-hook/`hooksPath` install.

Plus: `./visual-relay check` green; C#/shell files stay under 300 lines;
Conventional Commit subjects.
