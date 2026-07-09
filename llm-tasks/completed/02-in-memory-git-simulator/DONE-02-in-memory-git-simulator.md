# Build GitSim — an In-Memory Git Behind IGitInvoker, With a Differential Parity Harness

Part of the test-suite speed push (hard ceiling: full suite under 60 s; 45 s is the
aspirational target). Measured 2026-07-08:
the driver/git integration families are 365+ tests carrying ~71 % of all summed test
time, and every one of them talks to git by spawning the real binary. The seam is
already perfect for substitution: ALL production git access goes through
`IGitInvoker.RunAsync(rootPath, arguments, ct, timeout, environment, killToken, onActivity)`
→ `(int ExitCode, string Output, bool TimedOut)` (`src/VisualRelay.Core/Execution/IGitInvoker.cs`);
even FS-only driver tests spawn real git today because
`RelayDriverDependencies.ForTests` defaults `gitInvoker ?? new GitInvoker()`
(`src/VisualRelay.Core/Execution/RelayDriverDependencies.cs`).

Research conclusion (do not relitigate): there is **no existing .NET in-memory git**
suitable here. LibGit2Sharp is in-process but is rejected — it cannot execute hooks
(GitCommitter's hook-rejection path is load-bearing), it drags a native binary into the
nix build, and it would still need argv→API translation. We build our own simulator —
**this task builds the simulator and proves it; a later queued task migrates the test
suite onto it.** Do not migrate existing tests here beyond the pilot below.

## Shape

New project `tests/VisualRelay.GitSim/VisualRelay.GitSim.csproj` (net10.0), referencing
`VisualRelay.Core` only; referenced by `VisualRelay.Tests`; added to `VisualRelay.slnx`.
Split across small files (command router + state + one file per command group) — the
repo's file-size guard applies. Its tests live in `tests/VisualRelay.Tests/` like all
tests (`GitSimTests*.cs`).

`public sealed class GitSim : IGitInvoker` semantics:

- **Per-root state registry.** `rootPath` identifies the repo; a process-wide
  thread-safe registry maps root → repo state (xUnit runs collections in parallel).
  Linked worktrees registered by `worktree add` share the object store of their
  source root but have their own HEAD/index.
- **Arguments arrive WITHOUT `-C`** — the real `GitInvoker.RunAsync` prepends
  `-C <rootPath>` itself; GitSim replaces GitInvoker entirely, so it must treat
  `rootPath` as the working directory and never expect `-C`.
- **`Output` is combined stdout+stderr** in one string (matches `GitInvoker`).
  `TimedOut` is always `false`. Everything completes synchronously
  (`Task.FromResult`) — zero `Task.Delay`, zero real time.
- **Unknown or unsupported argv → throw** `InvalidOperationException` naming the full
  argv. Never silently succeed: a test hitting an unsupported command must fail loudly
  so the simulator gets extended (with a parity test), not worked around.

State model: in-memory object store (blobs, trees, commits with author/committer
name/email/date, message, parents), refs (branches, tags, arbitrary refs like
`refs/relay-snapshot/*`), symbolic/detached HEAD, an index — plus a per-path index
override honoring the `GIT_INDEX_FILE` entry of the `environment` argument (production
`FlaggedWorkStore` builds snapshots through a temp index). The **working tree is the
real filesystem** under `rootPath`: `status`/`add`/`check-ignore` read real files;
`checkout`/`stash apply`/`cherry-pick -n`/`worktree add` write real files. Honor the
repo-root `.gitignore` for `--exclude-standard`/`check-ignore` (literal paths, `dir/`,
`*.ext`, and `**` globs are sufficient for the suite's fixtures).

## Command surface (complete — this is the production inventory, verified 2026-07-08)

Emulate exactly these shapes, including the output each consumer parses. Global: a
leading `-c key=value` (seen: `core.quotePath=false`, `core.fileMode=true`) is accepted
and may be ignored except that quotePath=false means paths are always emitted literally.

- `rev-parse`: `--is-inside-work-tree` (stdout `true`); `HEAD`; `--verify --quiet HEAD`
  (unborn-HEAD detection by exit code); `--verify --quiet <sha>^{commit}`;
  `--verify --quiet HEAD~<N>`; `<ref>`; `--show-toplevel`.
- `ls-files`: `--others --exclude-standard` (± `-z`, ± trailing `-- <files…>`);
  `--others --ignored --exclude-standard --directory` (± `-z`; directories carry a
  trailing `/`); `--deleted -z`; `-u` (`<mode> <sha> <stage>\t<path>` lines);
  `-- <rel>`; bare and pattern forms (`*.cs`).
- `diff`: `HEAD --name-only -z`; `--name-only -z`; `--cached --name-status -M -C -z`;
  `HEAD --name-status --no-renames -z`; `--name-only --diff-filter=AM -z <a> <b>`;
  `--quiet HEAD -- <files…>` (exit 1 = differs); `--name-only <sha>` /
  `--name-only <sha> HEAD`; `--cached --name-only`.
- `status --porcelain` (± `-- <paths…>`) — empty output = clean is the only contract.
- `add`: `-A -- <files…>`; `-u`; `-f -- <files…>`; `-- <files…>`; `-A`.
- `commit -m <msg>` — the ONLY hook-consulting command (see Hooks); reads
  `RELAY_COMMIT_TOKEN`/`RELAY_NONCE` from `environment`. `commit --allow-empty -m`.
- `commit-tree <tree> [-p <parent>] (-F <file> | -m <msg>)` — honors `GIT_AUTHOR_*`/
  `GIT_COMMITTER_*` env; **never** runs hooks; stdout = new sha.
- `reset -q` / `reset -q HEAD` / `reset --soft <sha>`;
  `restore --staged --source=<sha> -- <rel>`; `checkout HEAD -- <rel>`; `checkout -- .`.
- `stash push -u -m <tag>` (± `-- <paths…>`), `stash list` (tag substring →
  `stash@{n}`), `stash apply <ref>` (non-zero exit models conflict), `stash drop <ref>`.
- Plumbing: `read-tree <sha>`, `write-tree`, `update-ref <ref> <sha>`,
  `update-ref -d <ref>`, `update-ref <ref> <new> <old>` (compare-and-swap),
  `rm --cached -q -- <p>`, `rm -f --cached --ignore-unmatch -- <p>`,
  `cat-file -e HEAD:<rel>`, `merge-base --is-ancestor <a> <b>`, `rev-list <a>..<b>`,
  `ls-tree HEAD -- <path>`, `diff-tree --no-commit-id --name-only -r <sha>`.
- `bundle create <path> <ref> ^<base>` (serialize the reachable-commit closure to the
  file — an opaque JSON payload is fine), `bundle verify <path>`,
  `fetch <path> +<src>:<dst>` (import from a bundle file), `cherry-pick -n <sha>`
  (conflicts surface via `ls-files -u`), `cherry-pick --quit`.
- `log`: `-1 --format=%B <sha>`; `--reverse --format=<…\x1e/\x1f-delimited %H %T %P %an
  %ae %aI %cn %ce %cI %B…> <range>`; `--follow -1 --format=%cI -- <path>`.
- `worktree add --detach --quiet <path> HEAD` (materialize HEAD's tree at `<path>`,
  register a linked worktree), `worktree remove --force <path>`, `worktree prune`.
- `config --default .git/hooks core.hooksPath`; `config core.hooksPath <dir>`; `init`
  (also `init -b <branch>`); `symbolic-ref --short --quiet HEAD`;
  `var GIT_AUTHOR_IDENT` (`Name <email> <ts> <tz>`); `tag -f <name> <sha>`;
  `check-ignore -- <paths…>` (exit 0 + the ignored subset on stdout).

## Hooks

GitSim executes no scripts. Instead: `public Func<GitSimCommitRequest, GitSimHookVerdict>? PreCommitHook`
consulted only by `commit -m` (request carries the staged set, message, and the env
dict). A rejecting verdict yields a non-zero exit with the verdict message in `Output` —
exactly the failure shape `GitCommitter.CommitAsync` handles. `commit-tree` bypasses it,
as real git does. (Tests that need the REAL installed `.git/hooks/pre-commit` bash hook
stay on real git — out of scope here.)

## Seeding / inspection API (what replaces shelling `git log` in test asserts)

A small closed surface on `GitSim`, designed for the later migration:
`InitRepo(root, branch = "main")`, `Seed(root, relPath, content)` +
`Commit(root, message, author?, dates?)`, `Head(root)`, `BranchTip(root, name)`,
`CommitsBetween(root, a, b)`, `CommitInfo(root, sha)` (message/author/committer/dates),
`FilesInCommit(root, sha)`, `StagedPaths(root)`, `IsIgnored(root, rel)`,
`RefExists(root, name)`. Nothing speculative beyond this list.

## Proof — two layers

1. **Fast unit tests (always on)** in `tests/VisualRelay.Tests/GitSimTests*.cs`: one
   or more facts per command group above, asserting the exact output shapes listed.
2. **Differential parity harness (opt-in)**: for each command group, run an identical
   scripted sequence against GitSim and against real git in a throwaway temp repo and
   compare exit codes plus the consumer-relevant output. Gate every parity fact with
   the house opt-in idiom — a `SlowIntegration` static class mirroring
   `NonoIntegration.SkipIfNotOptedIn()` (`tests/VisualRelay.Tests/NonoIntegration.cs`)
   but reading `VR_RUN_SLOW_INTEGRATION=1`. Keep the method name `SkipIfNotOptedIn`
   — the `RealBuildSubprocessGuardTests` AST scan recognizes the opt-out by that name.
   Real-git parity processes must set `GIT_CONFIG_GLOBAL=/dev/null`,
   `GIT_CONFIG_SYSTEM=/dev/null`, and explicit author/committer env so results are
   host-independent. Run the parity suite once opted-in during this task and record
   the result in the summary.

## Pilot migration (bounded)

Convert exactly one real-git family as proof: `GitCommitterTests` +
`GitCommitterTestHelpers` (they exercise commit, hook rejection via
`PreCommitHook`, squash, retry-after-transient — `TransientGitShim` shows the
current interception pattern). Same assertions, now against GitSim state. The
class must drop under 2 s solo
(`dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --no-build --filter "FullyQualifiedName~VisualRelay.Tests.GitCommitterTests."`).
No other existing test file changes in this task.

## Done when

- GitSim supports the full inventory above; unsupported argv throws with the argv text.
- GitSim unit tests pass; opted-in parity suite passes on the host; pilot family passes
  with identical assertions and runs <2 s solo.
- Zero `Task.Delay`/`Thread.Sleep`/process spawns anywhere in `VisualRelay.GitSim`.
- Full suite green; `./visual-relay check` passes; summary records parity results and
  pilot before/after class timings.

## Guardrails

- No production behavior changes. Do not modify `GitInvoker`, `RelayDriver`,
  `GitCommitter`, or `RelayDriverDependencies` in this task.
- Do not migrate any tests beyond the named pilot; the sweep is a separate queued task.
- No NuGet additions (no LibGit2Sharp). GitSim references only `VisualRelay.Core` and
  the BCL.
- Conventional Commits; all new files under the file-size guard.
