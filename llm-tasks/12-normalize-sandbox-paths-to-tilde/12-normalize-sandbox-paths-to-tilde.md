# Normalize Sandbox Path Display to `~` (Fix the `$HOME` vs `~` Inconsistency)

In the Settings → Sandbox lists, some rows show `$HOME/…` and others show `~/…` for the same home
directory, side by side (e.g. the Writable list shows `$HOME/.swiftpm`, `$HOME/.npm`, `$HOME/.cargo`,
while the Readable list shows `~/go`, `~/.cargo`, `~/.rustup`). It's an unnormalized display artifact —
not a Windows requirement and not intentional. Show a **single convention — `~` — everywhere**, matching
what a macOS/Unix user expects and the convention the panel was originally meant to use.

See `Screenshot-settings.png` in this folder — the mixed `$HOME/…` and `~/…` rows.

## Current state — two producers, two conventions, no normalization

**Rows render `Raw` verbatim.** `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml` binds each row's
visible path to `Raw` (`Text="{Binding Raw}"`), with `Expanded` as the tooltip and `Source` as the
right-side tag. The entry type is
`record SandboxPathEntry(string Raw, string Expanded, SandboxAccess Access, string Source)` in
`src/VisualRelay.Core/Execution/SandboxPathInspector.cs`.

**The two conventions come from two different producers:**

- **vr-guard rows** (`source = "vr-guard"`): the literal `"$HOME/…"` strings come straight out of
  `packaging/nono/vr-guard.json` (e.g. `"read": ["/", "$HOME/.gitconfig"]`, `"allow": ["$HOME/.swiftpm",
  …]`). `SandboxPathInspector.ParsePathArray` stores that literal into `Raw` **unchanged**.
- **`go_runtime` / `rust_runtime` rows**: the group JSON from the external `nono` binary carries a `"raw"`
  field that uses the `~/…` convention; `SandboxPathInspector.ParseGroupAllowEntries` copies that `"raw"`
  into `Raw` **verbatim**.

**Nothing normalizes the displayed value.** `SandboxPathInspector.ExpandPath` *does* understand both
prefixes (it strips a leading `$HOME` **or** `~` to build the concrete home path) — but it only feeds the
`Expanded` field (the tooltip), **never** the displayed `Raw`. So `$HOME/…` and `~/…` sit next to each
other purely because of which producer emitted them. The task that built this panel explicitly intended the
`~` form ("prefer showing the raw form `~/Library/Caches`"), so `~` is the target convention.

## What to build

- Normalize the **displayed** home-relative path to a single `~` convention, regardless of source. The
  cleanest spot is where entries are constructed in `SandboxPathInspector`: add a small helper (e.g.
  `NormalizeRawForDisplay(string raw)`) that rewrites a leading `$HOME` (and `${HOME}` if that form ever
  appears) to `~`, leaves an existing `~` untouched, and leaves absolute / non-home paths (`/`,
  `/usr/local/go`, `$TMPDIR`, etc.) exactly as they are. Apply it to `Raw` for **every** entry — both the
  `ParsePathArray` (vr-guard) path and the `ParseGroupAllowEntries` (group) path — so all three lists are
  consistent.
- Keep `Expanded` (the concrete `/Users/you/…` tooltip) exactly as-is; only the human-facing `Raw`
  convention changes.
- (Alternative: a display-layer `IValueConverter` used by `SandboxPaths.axaml`. Normalizing at entry
  construction is preferred because it keeps the logic in one testable place; use the converter approach
  only if there's a concrete reason to.)
- **Windows note:** the Windows sandbox derivation is a separate branch (`BuildWindowsResult`). Apply the
  same normalization there **only** if it emits `$HOME`/`~` forms; otherwise leave Windows paths untouched.
  The primary goal is intra-list consistency on macOS/Linux.

## Constraints & done criteria

- In the Settings sandbox lists, home-relative paths render uniformly as `~/…` (no `$HOME/…` remains);
  non-home absolute paths (`/`, `/usr/local/go`, …) are unchanged; tooltips (`Expanded`) still show the
  concrete home path.
- The fix is a **derived transform** over whatever the producers emit — not a rewrite of `vr-guard.json`.
  (Editing `vr-guard.json` to use `~` would fix today's symptom, but normalizing at display is the durable
  fix so a future `$HOME` literal can't reintroduce the split. Tidying `vr-guard.json` too is optional; the
  display normalization is the requirement.)
- **Test:** feed the inspector/helper a `$HOME/…` vr-guard entry and a `~/…` group entry and assert both
  `Raw` values come out as `~/…`; assert `/` and other absolute paths are left untouched. (Follow the
  `SandboxPathInspectorTests.cs` pattern.)
- Cosmetic only — no behavior or policy change. Keep files within the **≤300-line** gate. Full `Verify`
  gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.Core/Execution/SandboxPathInspector.cs` — add `NormalizeRawForDisplay` and apply it to
  `Raw` in `ParsePathArray` and `ParseGroupAllowEntries`.
- `tests/VisualRelay.Tests/` — normalization test.
- (reference, no change) `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml` (binds `Raw`),
  `packaging/nono/vr-guard.json` (the source of the `$HOME` literals).
