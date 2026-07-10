# Make init finish its own job: commit the config it writes

A real-world verification run (zod, husky) proved a gap in the onboarding flow. Bootstrap/init
correctly writes `.relay/config.json` and `.relay/.gitignore` (RelayGitignoreWriter: `*`,
`!.gitignore`, `!config.json`) and installs the commit-authority hook — and then leaves its own
two files untracked. On repos whose pre-commit hooks reject untracked files (zod's husky runs
`git ls-files --others --exclude-standard` and hard-fails on any hit), every stage-12 commit is
then rejected with "untracked files present" even though the pipeline was perfect: the residue at
hook time is exactly `.relay/.gitignore` and `.relay/config.json`. Those two files are DESIGNED to
be committed — the un-ignore entries exist for no other reason — so the init flow should close the
loop itself instead of leaving the last step to the operator.

## What to build

1. **A setup commit at the end of initialization.** After a successful bootstrap
   (`ProjectBootstrapper.BootstrapAsync` — the CLI `init` and control-API `bootstrap` path) and
   after the GUI Create-config path (`MainWindowViewModel.Execution.cs` `CreateConfigAsync`),
   create a commit with message `chore(relay): initialize project config` containing EXACTLY the
   files initialization wrote: `.relay/config.json` and `.relay/.gitignore`. Nothing else, ever.
   - Factor the logic into one shared helper in `src/VisualRelay.Core/Init/` (both paths call it);
     no duplicated git plumbing in the view-model.
   - Stage/commit by explicit pathspec (`git commit -m <msg> -- .relay/config.json
     .relay/.gitignore` semantics) so a user's independently staged changes are NEVER swept into
     the setup commit.
   - Idempotent: when both files are already tracked and unchanged (re-running init on an
     initialized repo), make no commit and report nothing noisy. When only one file changed
     (e.g. config rewritten with a new testCmd), commit just the changed file with the same
     message.
   - Ordering: this runs after hook installation and after `GitBootstrapper` has ensured a
     resolvable HEAD (fresh `git init` repos get their empty root commit first — existing flow).
     Note: the commit-authority hook VR installs only blocks commits while `.relay/ACTIVE` exists;
     init time has no ACTIVE dir, so no token is needed — do not add one.
2. **Hook rejection is an init-time setup-check failure, not an exception.** If the target repo's
   own pre-commit rejects the setup commit, initialization must still leave the written files in
   place, and the rejection must surface through the existing setup-check plumbing
   (`.relay/setup-check.log` artifact + the `setupCheck` field in `/state` + status text), with
   the hook's output captured. This converts the failure the user saw 11 minutes into a pipeline
   into an immediate, diagnosable init-time signal.
3. **Belt-and-suspenders for non-init entry paths:** at run start, ensure `.relay/.gitignore`
   exists (call `RelayGitignoreWriter.EnsureWritten` from the driver's run preparation) so
   hand-written `.relay/config.json` setups still get the diagnostics-ignoring behavior. Write
   only — never commit outside the init flow.
4. Out of scope (tracked separately): the 5-second validation timeout in `CreateConfigAsync`
   (issue 016) and pending task files of OTHER queued tasks (user-authored content).

## Tests (red first)

- Bootstrap on a fresh repo: after init, `git status --porcelain` contains no `.relay/` entries;
  `git log -1` is `chore(relay): initialize project config` touching exactly the two files.
- Idempotence: a second bootstrap creates no new commit.
- Staged-isolation: with an unrelated file staged by the user before init, the setup commit
  contains only the two `.relay` files and the user's staged file remains staged.
- Strict-hook repo (the zod scenario): install a pre-commit hook that fails when
  `git ls-files --others --exclude-standard` is non-empty; after init (setup commit succeeds —
  the two files are being committed, so the tree is clean at hook time), a subsequent
  driver-style commit in that repo is NOT rejected for `.relay` residue.
- Hook-rejection path: a pre-commit hook that always fails → init completes with the config
  written, no commit, and the setup-check artifact + state field carry the hook output.
- Run-start test: driver preparation on a repo with a hand-written `.relay/config.json` and no
  `.relay/.gitignore` writes the gitignore (and does not commit it).

## Verification

- `./test.sh` fully green including the new tests.
