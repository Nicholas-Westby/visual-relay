# Auto-grant the workspace volume's .TemporaryItems in the nono sandbox

On macOS, Foundation atomic file writes stage their temp file in the
DESTINATION volume's temporary directory. For a workspace on an external
volume (e.g. /Volumes/Tera/dev/patternsmith) that staging area is
`/Volumes/Tera/.TemporaryItems/folders.<uid>/TemporaryItems/NSIRD_<proc>_<rand>`
at the volume root — outside the `--allow-cwd` grant, so nono denies
`file-write-create` (PolicyBlocked). Verified 2026-07-18 with
`nono run --diagnostics-json`: `swift build` on such a workspace dies with the
misleading "encountered an I/O error (code: 1) while reading
…/output-file-map.json" (errno 1 = EPERM; the file was never written —
non-atomic writes like module.modulemap succeed), and `swiftformat .` errors
"Failed to write file …" on every file it reformats, which VR silently
ignores. This flagged BOTH patternsmith tasks in the 2026-07-19 drain
("setup check failure" after 3 fix-verify attempts: guard `swift build` red
every attempt, unfixable by agents — the guard command is harness-owned).
Re-running the identical commands with `-a /Volumes/Tera/.TemporaryItems`
added: build green in ~9s, swiftformat formats. System-volume workspaces are
unaffected (atomic writes stage under $TMPDIR, which the vr-guard profile
already allows). The failure mode applies to ANY tool doing Foundation atomic
writes in ANY external-volume workspace, for both verification commands and
swival agent shells (agents hit the same wall probing `swift test` in-repo).

## Prescribed approach

When the workspace root lives on a non-system volume, automatically append
`-a <volumeRoot>/.TemporaryItems` to the nono prefix for BOTH the
verification runner and the swival agent runner. This is an internal grant,
not user config: `sandboxExtraAllowPaths` validation (RelayConfigLoader)
deliberately rejects paths outside $HOME/workspace root and must stay that
way — do not relax it.

### Steps

1. Pure helper (new, unit-testable): `WorkspaceVolumeTempDir(rootPath)`
   returns `/Volumes/<vol>/.TemporaryItems` for any rootPath under
   `/Volumes/<vol>/…`, and null otherwise (system volume, Linux, Windows).
   Path-string logic only — no filesystem probing — so it stays testable.
2. Thread it into `BuildNonoPrefix` (ProcessRunners.cs) — the builder today
   takes no rootPath; add the computed grant (or the rootPath) as a
   parameter so both callers pass it: the swival prefix
   (ProcessRunners.Helpers.cs) and the verification prefix
   (SandboxedTestRunner.ResolveLaunch). Emit `-a <dir>` before `--`,
   alongside SandboxExtraAllowPaths.
3. Confirm empirically (implementer, once): nono accepts a `-a` grant for a
   path that does not yet exist. If it refuses, best-effort
   `Directory.CreateDirectory` first and fall back to no grant on failure —
   never fail the run because the grant could not be built.
4. Windows/MXC path untouched (ResolveWindowsLaunch).

## Tests (red first)

- `WorkspaceVolumeTempDir`: `/Volumes/Tera/dev/x` → `/Volumes/Tera/.TemporaryItems`;
  `/Volumes/Tera` → same; `/Users/nick/dev/x` → null; `/private/tmp/wt` →
  null; trailing-slash and bare `/Volumes` inputs don't crash.
- Extend the existing SandboxedTestRunner arg-shape tests: external-volume
  rootPath produces a prefix containing `-a /Volumes/<vol>/.TemporaryItems`;
  home-rooted rootPath produces a prefix without it; the grant appears in the
  swival prefix as well.

## Verification

`./visual-relay check` green. Manual proof on this machine: from an
external-volume Swift workspace, `nono run --profile <vr-guard> --allow-cwd
<new grant> -- /bin/sh -c "swift build --disable-sandbox"` exits 0 where the
grantless invocation fails with the output-file-map EPERM error.
