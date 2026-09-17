# Refuse a distro mismatch before a worktree is asked for

On Windows the planning worktree and the verify snapshot belong inside the WSL
distro, and git runs there too. Two pieces of code disagree about which distro
that is. `WorktreeNamespace` places a worktree in the distro only when the
resolved distro and the one named in the UNC workspace path are the same;
`GitRouting` routes every git call into whatever distro the path names, without
asking. When the two diverge the worktree path falls back to a Windows temp
directory and is handed to the distro's git — and the distro's git does not
refuse it. On ext4 a backslash is an ordinary character and a colon is legal, so
`C:\Users\...\Temp\visual-relay\wt\...` is one long directory NAME. git creates
it inside the operator's repository, registers it as a real worktree and exits
0. Nothing throws, so nothing is retried, nothing is logged, and the catch that
was written for this case never fires: verify then runs the project's suite
against the real checkout while the Windows half of Visual Relay looks at an
empty temp directory. This task refuses the mismatch before a worktree is asked
for.

## Evidence

Windows PC, 2026-09-17, measured directly against a throwaway repository in the
distro (no eval repository was touched).

- The fallback command, run as the code would produce it:

      git -C /home/enjay/vr-unc-probe worktree add --detach --quiet \
        "C:\Users\Enjay\AppData\Local\Temp\visual-relay\wt\a1b2c3d4e5f6\1122334455aa\99887766ddcc" HEAD

  exit 0, 45 ms, no output. `git worktree list` then reports
  `/home/enjay/vr-unc-probe/C:\Users\...\99887766ddcc` as a genuine detached
  worktree, and `ls -1b` shows one directory component beside `.git` and
  `README.md`. The operator's repository is left holding an untracked directory
  with a name no ordinary `rm -rf` invocation guesses.
- The index path behaves the same way:
  `GIT_INDEX_FILE='C:\Users\...\git-index-0123456789abcdef' git add -A` exits 0
  in 12 ms, writes the index file relative to the current directory (inside the
  repository) and stages it along with the junk worktree, with git's own
  "adding embedded git repository" warning.
- The path corrupts itself in any log line rendered through a shell's
  `echo`/`printf`: `\v`, `\a` and `\1` are interpreted, so the same directory
  reads as
  `C:\Users\Enjay\AppData\Local\Tempisual-relay\wt1b2c3d4e5f6J2334455aa\99887766ddcc`
  in git's own advice output while `ls -1b` shows it intact. Anyone diagnosing
  this from a shell-rendered log is chasing a path that never existed.

## Current state (researched at 53a5c8ab)

- `Execution/Wsl/WorktreeNamespace.cs:102-108` places a worktree inside the
  distro only when BOTH `host.Wsl is { } wsl` and the UNC root's distro equals
  `wsl.Distro`; otherwise it returns the Windows temp path
  `C:\Users\<u>\AppData\Local\Temp\visual-relay\wt\…`.
- `Execution/Wsl/GitRouting.cs:33-34` requires neither: a UNC root routes git
  into the distro the path names.
- Two states diverge: the probe is unusable while the workspace is a UNC path,
  and the resolved distro differs from the one in the UNC path (a renamed
  distro, `VR_WSL_DISTRO` pointing elsewhere, a repository opened in a second
  distro).
- `Execution/FlaggedWorkStore.cs:76` sets `GIT_INDEX_FILE` to a Windows temp
  path the same way.
- Nothing classifies the outcome because nothing fails:
  `GitFailureClassifier.cs:24-25` knows only "not a git repository" and
  "invalid reference", the three retries at `PlanningWorktree.cs:264-271` never
  run, and `RelayDriver.VerifyWorktree.cs:37-40` (`catch { worktreePath =
  null; }`) never fires. `:44` then runs the suite against the real repository,
  which the comment at `:28-30` names as the outcome to avoid.
- The pre-run gate does not catch the second state:
  `SandboxedStage.ToolPresence.cs:56` tests only `host.Wsl is null`, and
  `WslSandboxLauncher.ResolveWorkspace` (`:39-45`) refuses later, after the
  worktree has already been requested.
- `WorktreeNamespaceTests` covers the matching-distro case, the temp index, the
  drive root and the local host. Neither divergent state is covered.

## Prescribed approach

1. The mismatch is a refusal, not a fallback. `WorktreeNamespace` stops
   returning a Windows temp path on a Windows host: when the workspace is a UNC
   path whose distro is not the resolved one, or when there is no resolved
   distro at all, it fails with a reason naming both distros. The Windows temp
   path remains only for a host that is not Windows-with-a-distro at all, where
   nothing routes git into a distro either.
2. The refusal happens in the pre-run gate, before any worktree is asked for.
   `SandboxedStage.MissingRequiredTools` (or a sibling the run gate calls) takes
   the workspace root as well as the host and reports the mismatch, so the
   operator is told before a run starts rather than after a worktree has been
   created in their repository. The message names the resolved distro, the
   distro in the path, and the two ways out: open the workspace through the
   resolved distro's share, or point `VR_WSL_DISTRO` at the one the path names.
3. The same rule covers `GIT_INDEX_FILE`. `FlaggedWorkStore` asks the same
   helper for its temp index path and refuses rather than handing a Windows path
   to the distro's git.
4. Whatever still cannot be refused is at least visible. When verify falls back
   to running without a snapshot for any reason, it publishes a warn event
   `verify_isolation_lost` with the reason, and the flag reason says so. A suite
   that ran against the operator's real checkout must never be a silent success.
5. A note in `TROUBLESHOOTING.md` under Windows: a path holding `\` and `:` is
   one legal filename on ext4, so a stray directory of that shape inside a
   repository is this bug's signature; it will not survive being echoed through
   a shell, so read it with `ls -1b`.

## Tests

- `WorktreeNamespaceTests`: a UNC root whose distro differs from the resolved
  one is refused with both names in the reason; a UNC root with no resolved
  distro is refused; the matching case is unchanged; the local host is
  unchanged.
- `FlaggedWorkStoreTests`: the temp index path on a mismatched host is refused
  rather than built from a Windows path.
- The run gate's tests: a mismatched host reports the refusal in `StatusText`
  and no worktree call is recorded (a recording git invoker must see none).
- `RelayDriverVerifyWorktreeTests`: a verify that cannot make a snapshot
  publishes `verify_isolation_lost` and the reason reaches the flag.
- Watch each fail first; mutate the comparison to always-equal and confirm the
  mismatch facts catch it.

## Verification (through the control API)

Windows PC. Open a workspace through a UNC path naming a distro other than the
resolved one (`VR_WSL_DISTRO` pointing at a second distro is the cheapest way),
then `run-selected` one small task. Expected: the run is refused before stage 1
with a message naming both distros, `git -C <clone> status --short` is empty
afterwards, `git worktree list` shows one worktree, and no directory whose name
contains `\` or `:` exists in the repository. Then unset the variable and
confirm the same task runs to a commit. Put the refusal text and the two
`git worktree list` outputs in the commit body.

## Out of scope

Supporting two distros at once; whether the Windows temp path is the right
fallback for a non-Windows host; `RewriteUndoStore`'s copy into the Windows temp
directory (by design, nothing has gone wrong from it).

## Rejected alternatives

- Classify the failure and retry: there is no failure. git accepts the path and
  succeeds, so every mechanism built to catch an error is bypassed. Only a
  refusal before the call can help.
- Sanitize the path into something the distro can use: it would still be a
  worktree the Windows half of the app cannot see, which is the actual defect.
- Let `GitRouting` follow the resolved distro instead of the path: the path is
  the workspace the operator chose, and silently running git somewhere else is
  the same class of bug pointing the other way.
