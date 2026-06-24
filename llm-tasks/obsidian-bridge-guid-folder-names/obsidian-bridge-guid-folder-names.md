# Obsidian bridge mints GUID-named top-level vault folders instead of one stable project folder

The Obsidian bridge is supposed to mirror a project into **one** stable, project-named
top-level folder inside the vault (the user's vault has the correct `visual-relay/`
folder, containing `Completed/<date>/…`, `New Tasks/`, and `INFO.md`). Instead, the
vault has accumulated several **spurious top-level folders named like 32-hex GUIDs with
no dashes** (C# `Guid.ToString("N")` format), e.g. `ada5411c8ffa48d8bcf41340ad6f48af`,
`fd4a0b884eea4c0a807c065396520…`. Each GUID folder is a *full duplicate* of the same
layout (one even has `Completed/2026-06-24/first`, `New Tasks/`, `INFO.md`) — i.e. real
scaffold/export runs wrote into a freshly-invented folder rather than reusing the
project's stable one. The vault should contain exactly **one** folder per project, named
stably after that project — never a GUID.

## Current state (researched)

The vault's top-level folder name is computed in exactly one place and flows straight
through to the on-disk path with no stability guarantee:

- The bridge builds the layout from the *current* root path's leaf folder name.
  `MainWindowViewModel.RunObsidianBridgeScanAsync` and `ExportSummaryOnCompletion`
  (both in `src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs`) do:

  ```csharp
  var layout = new ObsidianVaultLayout(ObsidianVaultRoot, RootFolderDisplay.Name(RootPath));
  ```

- `RootFolderDisplay.Name` (`src/VisualRelay.App/ViewModels/RootFolderDisplay.cs`) is
  just the path leaf:

  ```csharp
  return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
  ```

- `ObsidianVaultLayout` (`src/VisualRelay.Core/ObsidianBridge/ObsidianVaultLayout.cs`)
  then uses that name verbatim as the top-level folder:

  ```csharp
  public string RepoDir => Path.Combine(_vaultRoot, _repoName);
  ```

  Its `SanitizeRepoName` only strips path separators and rejects `"."`/`".."` — a
  32-hex GUID string passes through unchanged and becomes a top-level vault folder.
  `EnsureScaffold` then creates the whole `New Tasks/` + `Completed/` + `INFO.md` tree
  under it.

**Root cause:** the vault folder name is derived from the *volatile leaf of whatever
`RootPath` currently points at*, with nothing that ties it to a stable project identity.
When `RootPath` is (or has been) pointed at a directory whose leaf name is a GUID, the
bridge fabricates a brand-new GUID-named vault folder each scan/export. The repo has
several GUID-`N`-named directories that are repo-shaped copies a user can end up opening
or that the app itself creates as working copies — e.g. rewrite snapshots at
`Path.GetTempPath()/visual-relay/rewrite-undo/<Guid.ToString("N")>`
(`src/VisualRelay.Core/Execution/RewriteUndoStore.cs`, `Capture`) and worktree/temp
checkouts under `Path.GetTempPath()/visual-relay/wt/…`
(`src/VisualRelay.Core/Execution/PlanningWorktree.cs`). Whenever the bridge runs with
such a root, `Path.GetFileName(RootPath)` is a GUID and a new vault folder is born. The
legitimate `visual-relay/` folder is simply the leaf name from a normal checkout
(`…/Dev/visual-relay`); the GUID folders are the same mechanism fed a GUID leaf.

This is not project-specific — VR is a general-purpose tool, so the fix must derive the
folder name generically from the project, not hard-code anything.

## What to build

Derive the top-level vault folder name from a **stable identity of the project**, and
make that derivation robust so a temp/snapshot/worktree copy can never mint a new folder.

1. **Compute the folder name from the project's stable identity, not the raw `RootPath`
   leaf.** Prefer the project's git top-level folder name: resolve the repo root via
   `git rev-parse --show-toplevel` for `RootPath` and use *its* leaf. This collapses a
   worktree/temp checkout back to the real project name (so a checkout of `visual-relay`
   under a GUID path still maps to `visual-relay`), and is the most natural stable id for
   the general case. Fall back to the existing `RootFolderDisplay.Name(RootPath)` leaf
   only when `RootPath` is not inside a git work tree. Reuse the existing git seam used
   elsewhere (e.g. `IGitInvoker` / the `rev-parse` pattern in
   `src/VisualRelay.Core/Init/GitBootstrapper.cs`) rather than shelling out ad hoc.

2. **Reject a GUID-shaped folder name as the identity (defense in depth).** Even with the
   git-root step, guard the final name: if the derived name matches a bare
   `Guid.ToString("N")` shape (32 hex chars, no separators) — or otherwise can't be
   trusted as a project name — do **not** create a per-run folder under it. Add this guard
   in `ObsidianVaultLayout` (alongside `SanitizeRepoName`) so it holds no matter who calls
   the constructor, and surface a clear status message instead of silently scaffolding a
   GUID folder.

3. **Keep the derivation in one testable place.** Put the project→folder-name logic in a
   single function (e.g. a static helper in Core, or extend `ObsidianVaultLayout` /
   `RootFolderDisplay`) and have **both** bridge call sites in
   `MainWindowViewModel.ObsidianBridge.cs` use it. Do not duplicate the logic.

### What NOT to break

- The existing **`visual-relay/`** folder (the correct, project-named folder) must keep
  being the target for a normal checkout — no rename, no second folder. A normal repo at
  `…/Dev/visual-relay` must continue to resolve to exactly `visual-relay`.
- The full layout under that folder is unchanged: `New Tasks/` (+ `Recognized/`),
  `Completed/<YYYY-MM-DD>/<task>.md`, and the four `INFO.md` guide files. Do not touch
  `EnsureScaffold`'s structure, the `INFO.md` templates, the importer, or
  `ObsidianSummaryWriter`.
- Keep the existing path-traversal hardening in `SanitizeRepoName` (separator stripping,
  `"."`/`".."` rejection, the `project` fallback) — add to it, don't replace it.
- Do **not** bake in any project-specific name. The folder name must derive generically
  from the project (git-root leaf, with the directory-leaf fallback).

## How to verify

- Running the bridge (scan and on-completion export) **twice** against the same project
  reuses the **same single** top-level folder; no GUID-named folder is ever created.
- Opening the app against a worktree/temp checkout whose leaf is a GUID (e.g. a path under
  `…/visual-relay/wt/<hash>/<id>` or a `rewrite-undo/<guid>` copy) maps to the project's
  real name (or is refused), **not** a new GUID folder.
- The legitimate `visual-relay/` folder and its `Completed/<date>` / `New Tasks` / `INFO`
  contents are untouched.
- Tests (write the failing tests first):
  - Folder-name derivation: a git checkout resolves to the repo-root leaf, including when
    the working directory leaf differs from the repo-root leaf; a non-git directory falls
    back to the directory leaf.
  - The GUID guard: a 32-hex `Guid.ToString("N")` name is rejected / not scaffolded as a
    top-level folder (assert no such directory is created).
  - **Update the existing bridge tests that currently assume the GUID leaf is the folder
    name** — `ObsidianBridgeVmScaffoldExportTests` and `ObsidianBridgeVmTests`
    (`tests/VisualRelay.Tests/`) build their expected path from
    `RootFolderDisplay.Name(repoRoot)` where `repoRoot`'s leaf is `Guid.ToString("N")`.
    Point these at the new derivation symbol (or give their temp `repoRoot` a real
    project-name leaf inside a git repo); do **not** re-pin them to assert a GUID folder.
- `./visual-relay check` green; C#/XAML files under 300 lines; Conventional Commit subjects.
