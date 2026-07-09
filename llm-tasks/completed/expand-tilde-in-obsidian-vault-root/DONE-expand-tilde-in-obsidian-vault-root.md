# Expand `~` in the Obsidian Vault Root at Point of Use (Stray `<repo>/~/…` Tree)

Diagnosed 2026-07-08: a literal `~/Library/Mobile Documents/iCloud~md~obsidian/…` directory
tree keeps appearing under the repo root (the user deleted it; the running app recreated it
the same day). The audit log (`~/.config/visual-relay/settings-audit.log`) shows the trigger:

```
2026-07-08T04:55:12Z VR_OBSIDIAN_VAULT_ROOT "/Users/…/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/" -> "~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/" source=settings-ui pid=15435 proc=VisualRelay.App
```

The tilde form is a deliberate, desirable way to store the vault root (the `.env` is
per-machine but home paths differ between host and VM) — the bug is that the live app treats
the raw string as a filesystem path. .NET never expands `~`, so `~/…` is a RELATIVE path,
resolved against the app's working directory (the repo root).

## Root cause (verified in source)

`ObsidianBridgeSettings.Load` (`src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs`)
already expands the stored value at startup:

```csharp
var vaultRoot = !string.IsNullOrWhiteSpace(vaultRootStr)
    ? ExpandTilde(vaultRootStr, home)
    : defaultVaultRoot;
```

```csharp
private static string ExpandTilde(string path, string home)
{
    if (string.IsNullOrWhiteSpace(home))
        return path;
    if (path.StartsWith("~/", StringComparison.Ordinal))
        return Path.Combine(home, path[2..]);
    return path;
}
```

…and hydration is guarded (`LoadObsidianBridgeSettings` sets `_isHydrating` so the expanded
value is not persisted back — `.env` keeps the tilde). So restarted sessions are fine.

The gap is every path that puts a RAW value into the live `ObsidianVaultRoot` VM property
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs`):

1. the Settings textbox (the 2026-07-07 edit above), and
2. the control API — `src/VisualRelay.App/Services/ControlApi.cs`:
   `viewModel.ObsidianVaultRoot = path;` — so headless/loopback drivers hit the same bug.

Every consumer then uses the property verbatim. The two production
`ObsidianVaultLayout` constructions (both in `MainWindowViewModel.ObsidianBridge.cs` — one in
`RunObsidianBridgeScanAsync`, one in `ExportSummaryOnCompletion`):

```csharp
var layout = new ObsidianVaultLayout(ObsidianVaultRoot, repoName);
layout.EnsureScaffold();
```

`ObsidianVaultLayout` (`src/VisualRelay.Core/ObsidianBridge/ObsidianVaultLayout.cs`) does
`Path.Combine(_vaultRoot, …)` and `Directory.CreateDirectory` on the results — with
`_vaultRoot = "~/Library/…"` that scaffolds `<cwd>/~/Library/…`. `RevealVaultRoot` (same VM
file) has the same defect: `FileReveal.Reveal(ObsidianVaultRoot)` reveals a relative path.

Fix at the point of use, not at the write sites: the ctor is the single choke point through
which BOTH production layout uses (and any future one) flow, and it covers textbox and
control-API writes alike. The VM property deliberately keeps showing the tilde form the user
typed, and `.env` keeps storing it.

## What to build (TDD-first)

1. **Shared helper.** New file `src/VisualRelay.Core/Configuration/TildePath.cs`
   (namespace `VisualRelay.Core.Configuration`, next to `KeyEnvFile`/`XdgConfig`):

   ```csharp
   /// <summary>Expands a leading "~/" to the user's home directory. All other
   /// forms (absolute, relative, bare "~", "~user/…") pass through verbatim.</summary>
   public static class TildePath
   {
       public static string Expand(string path) =>
           Expand(path, Environment.GetEnvironmentVariable("HOME")
               is { Length: > 0 } home
               ? home
               : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

       public static string Expand(string path, string? home)
       {
           if (string.IsNullOrWhiteSpace(home))
               return path;
           return path.StartsWith("~/", StringComparison.Ordinal)
               ? Path.Combine(home, path[2..])
               : path;
       }
   }
   ```

   Semantics are exactly today's `ExpandTilde` — do not extend to `~user` or bare `~`.

2. **Single implementation.** Replace the private `ExpandTilde` in
   `ObsidianBridgeSettings.cs` with calls to `TildePath.Expand(vaultRootStr, home)` (it keeps
   resolving `home` through its `IEnvironmentAccessor` seam; delete the private method).

3. **Expand in the layout ctor** — the fix itself. In `ObsidianVaultLayout`'s constructor:

   ```csharp
   _vaultRoot = TildePath.Expand(vaultRoot);
   ```

   Every existing call site passes an absolute or non-`~` path, for which `Expand` is a
   verbatim no-op — zero behavior change outside the bug case.

4. **Fix the reveal.** In `RevealVaultRoot`, reveal `TildePath.Expand(ObsidianVaultRoot)`.
   (No new test — `FileReveal` shells out to the OS; the expansion is covered by unit tests
   of `TildePath` itself.)

5. **Tests** (red-first where marked):
   - New `TildePathTests.cs`: `"~/x"` → `Path.Combine(<resolved home>, "x")`; absolute path
     unchanged; plain relative unchanged; bare `"~"` unchanged; `"~user/x"` unchanged; the
     two-arg overload with `home: null` returns input verbatim. Pure string assertions, no
     filesystem I/O.
   - **RED regression pin** in `ObsidianVaultLayoutTests.cs`: constructing
     `new ObsidianVaultLayout("~/vault-tilde-test", "repo")` yields a `RepoDir` equal to
     `Path.Combine(<resolved home>, "vault-tilde-test", "repo")` — in particular NOT starting
     with `"~"`. Assert on the computed string only; do NOT call `EnsureScaffold` in this
     test (never create directories under the real home).
   - Regression pins that stay green unmodified: the existing tilde-expansion Load tests in
     `ObsidianBridgeSettingsTests.cs` (they already feed
     `"~/Library/Mobile Documents/iCloud~md~obsidian/…"` through `Load` with a fake
     accessor), all other `Obsidian*Tests` (they construct layouts with absolute temp
     roots — the ctor change is a no-op for them), and `ObsidianBridgeHermeticityTests.cs`.

## Done when

- Typing or control-API-setting a `~/…` vault root in a live session can no longer create
  `<cwd>/~/…`: both production layout constructions and the reveal path expand it to the
  real home directory, while the Settings textbox and `.env` continue to display/store the
  tilde form as entered.
- `TildePath` is the only tilde-expansion implementation (the private
  `ObsidianBridgeSettings.ExpandTilde` is gone).
- All tests above pass; `./visual-relay check` passes.

## Guardrails

- Do NOT normalize at the write sites (textbox change handler, control-API setter,
  `PersistBridgeSettings`) — the raw tilde in the VM property and `.env` is intended,
  VM-portable behavior; only consumption expands. Do not rewrite the user's input.
- Do NOT add rooted-path validation or reject other relative vault roots — out of scope.
- Do NOT delete the existing stray `<repo>/~` tree — user cleanup, not this task.
- Touched files stay under the 300-line guard: `ObsidianVaultLayout.cs` (216 now, +1),
  `ObsidianBridgeSettings.cs` (260 now, net negative), `MainWindowViewModel.ObsidianBridge.cs`
  (222 now, +1), new `TildePath.cs` (~30).
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs.
