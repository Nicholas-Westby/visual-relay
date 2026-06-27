# Harness: tighten the vr-guard allowlist + resolve guard-probe symlinks

A security review of `harness-sandbox-package-manager-writes` (commit `c829c65`) confirmed **no sandbox
escape** — the destructive/sensitive surface (documents, photos, credentials, keychains, browser data,
shell configs, system dirs) stays write-denied because the inherited `swival` deny-groups and the
explicit `deny` list **override every allow**, and verification is nono-wrapped with no silent host
fallback. But it surfaced three **hardening** gaps: two allowlist grants are broader than the
toolchain caches they exist for (violating the profile's own "writes and deletes confined to the
workspace" promise), and the new guard-probe containment is lexical-only (a symlink defeats the
guards-dir boundary, though the script still runs *inside* the sandbox). These are accident-containment
regressions / defense-in-depth, not escapes — fix them to keep the blast radius tight.

General harness change (sandbox profile + guard probe) — keep it platform-agnostic; VR drives any repo.

## Current state (researched)

> **Freshness contract.** Locate anchors by searching for the quoted code, not line numbers; re-read
> and adapt if a snippet has drifted. Re-verify each `nono why` verdict against the installed nono.

### 1. `packaging/nono/vr-guard.json` — two over-broad `filesystem.allow` entries

- **`$HOME/.local`** (read+write+delete) is the XDG data/state/bin home, far broader than the pip/uv
  cache it was added for, and **no deny-group covers anything under `~/.local`**. It makes writable +
  deletable: `~/.local/bin` (executables the user runs **outside** the sandbox → PATH-hijack /
  persistence), `~/.local/state/{gh,nix,...}` (tool identity/state). It is also **redundant** —
  `$HOME/.local/share/NuGet` and `$HOME/.local/share/swival` are already granted separately.
- **`$HOME/Library/Developer`** (`when: macos`, read+write+delete) covers `Xcode`, `CoreSimulator`,
  and `Provisioning Profiles` — none of which is a SwiftPM *cache* (the real caches are already granted
  via `~/Library/Caches/org.swift.swiftpm`, `~/Library/org.swift.swiftpm`, `~/.swiftpm`).

(For contrast — these are correctly narrow and should stay: `$HOME/.cargo/registry` + `$HOME/.cargo/git`
deliberately exclude `~/.cargo/credentials.toml`; the per-ecosystem `~/Library/Caches/*` entries.)

### 2. `src/VisualRelay.Core/Execution/RelayDriver.GuardContainment.cs` — lexical-only containment

The containment check resolves `Path.GetFullPath(Path.Combine(rootPath, entry))` and requires it to
start with `<root>/tools/guards/` (trailing-separator check is **correct** — `..` traversal and
sibling-prefix `tools/guards-evil` are both properly rejected). But it does **not** resolve symlinks:
a committed `tools/guards/x.sh` that is a symlink to `/etc/evil` or `../../elsewhere.sh` passes the
lexical check, then `File.Exists` follows the link and the target is executed (inside the sandbox, but
outside the intended guards-dir boundary). The agent can write `tools/guards/` (workspace is granted),
so this is reachable in a run.

### 3. (Minor / defense-in-depth) `SandboxExtraAllowPaths` "under $HOME" admits sensitive subtrees

`RelayConfigLoader` accepts any entry resolving under `$HOME` — which includes `~/.ssh`,
`~/Library/Keychains`, `~/.aws`, `~/.config/gh`, shell rc files. These are **backstopped** by the
deny-groups (an `-a` allow cannot override a deny-group, verified), so this is **not** an escape — but
a belt-and-suspenders rejection of known-sensitive subtrees would make the validation self-evidently
safe rather than relying on nono's deny-precedence. (Also note: this field is operator-trust — it lives
in the repo's own `.relay/config.json`.)

## What to build

### A. Narrow the two broad allowlist grants (`packaging/nono/vr-guard.json`)
- Remove `"$HOME/.local"`. Keep the specific children that are actually needed: `$HOME/.local/share/NuGet`
  (already present), `$HOME/.local/share/uv` (Linux uv data), and `$HOME/.local/share/swival` (already
  present); add `$HOME/.local/lib` **only** if a Linux pip/uv build demonstrably needs it. **Never**
  grant `~/.local/bin` or `~/.local/state`.
- Remove `"$HOME/Library/Developer"`. If a real Swift/Xcode build need exists, narrow to the specific
  build-output subdir (e.g. `$HOME/Library/Developer/Xcode/DerivedData`), never the whole `Developer`
  tree and never `Provisioning Profiles`.
- Re-verify with `nono why -p vr-guard --op write --path <p>` that the **removed** paths
  (`~/.local/bin/x`, `~/Library/Developer/Xcode/UserData/Provisioning Profiles/x`) are now **DENIED**,
  and the **kept** toolchain caches (`~/.nuget/packages`, `~/.swiftpm`, `~/Library/Caches/org.swift.swiftpm`,
  `~/.local/share/NuGet`) remain **ALLOWED**.

### B. Resolve symlinks in guard-probe containment (`RelayDriver.GuardContainment.cs`)
- After computing the candidate path and confirming it exists, resolve the **final** link target
  (`File.ResolveLinkTarget(path, returnFinalTarget: true)`, or equivalent realpath) and re-apply the
  same `<root>/tools/guards/` + trailing-separator containment check to the **resolved** target. Drop
  (and `warn`-log, as today) any candidate whose real target escapes the guards dir. Keep the existing
  lexical check too (defense in depth).

### C. (Optional, do if cheap) `SandboxExtraAllowPaths` sensitive-subtree rejection
- In `RelayConfigLoader`, after the `under $HOME / workspace` check, also reject entries that resolve
  into a small denylist of sensitive subtrees (`~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.config/gh`,
  `~/Library/Keychains`, shell rc files) with a config load error — so the field cannot even *appear*
  to grant them, independent of nono's deny-precedence.

## Tests

Write failing tests first.
- **Profile (NonoWhyOracle tier).** `nono why -p vr-guard --op write` reports **DENIED** for
  `~/.local/bin/x` and `~/Library/Developer/Xcode/UserData/Provisioning Profiles/x`, and **ALLOWED**
  for the kept caches above. (Keep these in the cheap oracle tier — NOT the opt-in real-build tier.)
- **Guard-probe symlink.** A manifest entry `tools/guards/x.sh` that is a symlink whose final target
  is outside `tools/guards/` is **dropped** (not executed); a real file `tools/guards/check.sh` is
  still selected. Assert via the candidate-selection seam without executing.
- **Config (if C done).** A `sandboxExtraAllowPaths` entry of `~/.ssh` (or `~/Library/Keychains`)
  produces a config **load error**; a legitimate `~/.cache/exotic-tool` is still accepted.

## Done when
- `vr-guard.json` no longer grants all of `~/.local` or all of `~/Library/Developer`; `nono why`
  confirms the PATH-hijack/provisioning paths are write-DENIED while the toolchain caches stay allowed.
- The guard probe rejects a symlinked entry whose real target escapes `tools/guards/`.
- The default `dotnet test` stays fast (these are cheap oracle/unit assertions — no real builds added
  to the default suite; respect the `VR_RUN_NONO_INTEGRATION` gate).
- `./visual-relay check` green; changed files < 300 lines; Conventional Commit subjects.

## Notes
- This is hardening, not an escape fix — the sandbox does not currently leak the sensitive surface.
  Scope strictly to A + B (+ optional C); do not re-architect the sandbox.
