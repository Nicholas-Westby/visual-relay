# Task: Strip repo-local git identity in the pre-commit hook

Something keeps writing `[user] name/email` into this repo's `.git/config`.
Every commit made on any machine — including automated commits from guest
VMs and driver runs — is then stamped `Nicholas-Westby
<nicholas-westby@users.noreply.github.com>`, because commit authorship is
plain config metadata: no key, no signature, nothing cryptographic is
involved. The repo must not carry a pinned identity. Make the existing
`.githooks/pre-commit` self-heal: unset repo-local `user.name` and
`user.email` on every commit attempt so each machine's own default
identity is what lands. This is for the Visual Relay repository ONLY —
the hooks Visual Relay provisions into target repositories are untouched.

### Evidence (2026-07-19 guest-VM identity investigation)

- `git config --show-origin --get-all user.name` → `file:.git/config`
  is the ONLY source; the guest's global `~/.gitconfig` has no `user.*`
  section at all. Every recent commit is unsigned (`git log
  --format='%G?'` → `N` across the board).
- `.git/config` mtime was 14:27 the same day — the values get re-written
  by something (SmartGit runs in-guest; the setter was not identified).
  Hunting the setter is OUT of scope: a self-healing hook makes the
  setter irrelevant.
- Delivery mechanism already exists: `core.hooksPath` points at
  `$repo/.githooks` (`pre-commit`, `commit-msg`, `command-guard`). The
  current `pre-commit` does commit-authority enforcement plus
  `bump-version`, and sits at **19 logic lines against the 24-line
  ceiling** (`ShellSizeGuard.DefaultLimit`,
  `tools/VisualRelay.Guards/ShellSizeGuard.cs:10`; counting rules in
  `ShellScriptLineCounter.cs` — blanks, full-line comments, and here-doc
  bodies are free). The addition has ~5 logic lines of headroom.
- git reads config once at process start: an unset performed by
  pre-commit CANNOT change the identity of the in-flight commit. The
  contract is therefore explicitly **one-commit lag** — the commit that
  triggers the strip still carries the stale identity; every commit
  after it uses the machine default. This is accepted (see decision 2).
- The machine default is real even with NO `user.*` config anywhere:
  verified on the guest that a fresh-repo commit succeeds with git's
  auto-detected ident `Managed via Tart
  <admin@Manageds-Virtual-Machine.local>` (GECOS full name +
  `username@hostname`, plus git's one-time advice message). git refuses
  to commit only where the hostname yields no domain part
  (`…@host.(none)`), so post-strip commits keep working on these
  machines — just distinguishably.

### What to build

1. **Strip at the top of `.githooks/pre-commit`, unconditionally.**
   Immediately after `repo_root` resolution and BEFORE the active-run
   branch and `bump-version`, unset repo-local identity so every path —
   developer commit, driver sealed commit, even a commit the authority
   check is about to reject — heals the config. Under `set -euo
   pipefail`, `git config --local --unset` exits 5 when the key is
   absent; use the tolerant idiom:

   ```bash
   stripped=0
   git config --local --unset user.name 2>/dev/null && stripped=1 || true
   git config --local --unset user.email 2>/dev/null && stripped=1 || true
   [[ "$stripped" = 0 ]] || echo "Visual Relay: removed repo-local user.name/user.email — this commit kept the old identity; the machine default applies from the next commit." >&2
   ```

   Four logic lines, 23/24 total. Keep the warning on ONE physical line:
   the counter counts backslash-continued lines individually, and shfmt
   does not enforce line length. If later work needs the headroom back,
   extract to a tracked `.githooks/identity-guard` helper with its own
   24-line budget — do NOT raise the ceiling.
2. **Strip-and-continue, uniformly. No blocking, no path-splitting.**
   Rejecting the commit (exit 1, "re-run") would make the current commit
   clean too, but it converts a hygiene event into a failed driver run —
   `GitCommitter` fails fast on hook rejection by design — and the house
   precedent in this exact hook (`bump-version`) is self-heal-and-allow.
   One lagging commit per re-infection is the accepted cost; the stderr
   warning above is the audit trail. Do not make this configurable.
3. **Warn only when something was stripped.** The no-op path stays
   silent — this hook runs on every commit and its output is read by
   humans and by driver logs.
4. **Nothing else.** No handling of `user.signingkey`, `author.*`,
   `committer.*`, or `GIT_AUTHOR_*`/`GIT_COMMITTER_*` env overrides; no
   identity chooser; no `--no-verify` countermeasure (bypass is accepted,
   same as for the authority check).

### Constraints

- `ShellScriptSizeGuardTests.AllTrackedShellScripts_AreWithinTheLimit`
  enforces the 24-logic-line ceiling and `ShellFormatGuard` enforces
  shfmt formatting (tabs) — both must pass with zero exemptions added.
- The existing commit-authority behavior (nonce/`RELAY_COMMIT_TOKEN`)
  and `bump-version` staging are byte-for-byte preserved apart from the
  inserted block; their tests keep passing.
- Visual Relay's target-repo hook provisioning is NOT touched. The strip
  lives only in this repo's own `.githooks/pre-commit`.
- Scratch/test repos deliberately set local identities
  (`tests/VisualRelay.Tests/ScratchRepo.cs:27-28` and friends) — those
  repos do not use this repo's hooksPath and must remain unaffected.

### Tests (red first)

Real-git integration tests in the style of
`RealGitIntegrationDriverTests.cs`: temp repo, `core.hooksPath` pointed
at the real `.githooks`, and — critical for determinism on machines with
and without a personal identity — `GIT_CONFIG_GLOBAL` pinned to a
test-owned file and `GIT_CONFIG_SYSTEM=/dev/null` on every git call.

- Infected repo: set local `user.name`/`user.email`, commit. Assert the
  commit succeeds, stderr carries the warning, and the local config no
  longer contains either key.
- Lag contract pinned: with a distinct identity in the test's global
  config, the first (triggering) commit's author is the OLD local
  identity and the second commit's author is the global one.
- Clean repo: no local identity → commit succeeds, no warning emitted.
- No-default machine: empty test global config → assert only the
  contract that matters and is machine-independent: the post-strip
  commit NEVER carries the stripped identity. Whether git auto-detects
  `username@hostname` (macOS `.local` hosts) or refuses (`…@host.(none)`
  hosts) is hostname-dependent — accept either outcome, assert the
  stripped name/email appear in neither the commit nor the error path.

### Verification

- `./visual-relay check` fully green (size guard, shfmt guard, and the
  authority-hook tests all count as regressions here).

### Operator note (not part of the implementation)

No machine needs identity setup for this to land: the guest has no
global `user.*` and still commits fine via git's auto-detected
`Managed via Tart <admin@Manageds-Virtual-Machine.local>` — which is
exactly the desired outcome, guest commits become visibly guest-authored.
Optionally set an explicit global identity in the guest image to pin the
wording and silence git's one-time auto-ident advice message; that is
polish, not a prerequisite.
