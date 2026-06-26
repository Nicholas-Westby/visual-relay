# Add Cloak Browser to Allowed Paths

Grant `~/.cloakbrowser` read+write in the `vr-guard` nono sandbox profile so a
task whose toolchain writes there isn't blocked. `~/.cloakbrowser` is a `$HOME`
toolchain dir exactly like the existing `"$HOME/.bun"`, `"$HOME/.deno"`,
`"$HOME/.npm"` grants — it just was never added.

## Current state (researched)

- The sandbox is nono with the `vr-guard` profile. Its single source of truth is
  `packaging/nono/vr-guard.json`; the `filesystem.allow` array lists `$HOME`
  toolchain cache dirs granted read+write (e.g. `"$HOME/.bun"`, `"$HOME/.deno"`,
  `"$HOME/.npm"`, `"$HOME/.pnpm-store"`, `"$HOME/.yarn"`). `~/.cloakbrowser` is
  absent, so writes there are denied.
- That JSON is the only copy to edit. It is embedded into `VisualRelay.Core` at
  build time via `<EmbeddedResource Include="..\..\packaging\nono\vr-guard.json"
  LogicalName="VisualRelay.Core.vr-guard.json" />` in
  `src/VisualRelay.Core/VisualRelay.Core.csproj`; `NonoProfileEnsurer.EmbeddedContent`
  reads the embedded resource (not a hardcoded string), so a rebuild re-embeds
  the edited file automatically. `NonoProfileEnsurerTests`
  `EmbeddedContent_EqualsRepoPackagingFile_ByteForByte` enforces on-disk ==
  embedded and will pass after build.
- Validation convention: every `allow` entry has a matching assertion in
  `tests/VisualRelay.Tests/NonoWhyOracleTests.cs` — e.g.
  `NonoWhy_Npm_AllowedReadWrite` calls `AssertAllowed(Path.Combine(Home, ".npm"))`.
  These tests skip when `nono` is off PATH and the helper creates the dir if
  missing, so no pre-existing `~/.cloakbrowser` is required.
- `NonoProfileStructureTests.VrGuardProfile_HasFilesystemAllowEntries` rejects
  entries starting with `/Users/` — use `$HOME`, never a hardcoded user path.
- A per-repo escape hatch exists (`RelayConfig.SandboxExtraAllowPaths`), but its
  docstring scopes it to "exotic toolchain cache paths the vr-guard profile
  baseline does not cover." `.cloakbrowser` is a standard `$HOME` toolchain dir,
  so the baseline profile — not the per-repo hatch — is the correct home.

## What to build

1. **Test first.** In `tests/VisualRelay.Tests/NonoWhyOracleTests.cs`, add a test
   mirroring `NonoWhy_Npm_AllowedReadWrite`:
   ```csharp
   [Fact]
   public void NonoWhy_CloakBrowser_AllowedReadWrite()
   {
       if (!NonoAvailable) Assert.Skip("nono is not on PATH");
       AssertAllowed(Path.Combine(Home, ".cloakbrowser"));
   }
   ```
   Confirm it fails (path denied) before the profile change.
2. **Grant the path.** In `packaging/nono/vr-guard.json`, add `"$HOME/.cloakbrowser"`
   to the `filesystem.allow` array, beside the sibling `"$HOME/.bun"` / `"$HOME/.deno"`
   / `"$HOME/.npm"` entries. Plain string entry (not OS-specific, so no `when`
   predicate).
3. Re-run the new oracle test → green. Rebuild so the embedded resource refreshes.

## Out of scope

Do **not** add a `rollback.exclude_patterns` entry. Every writable cache in the
profile currently has one (the `VrGuardProfileRollbackTests` docstring warns that
omitting it for a large cache can exhaust nono's rollback budget), but the
rollback budget is a separate concern from "the sandbox blocks it," `.cloakbrowser`'s
size is unknown, and no test enforces it. This task is strictly the `allow` grant.

## Done when

- `"$HOME/.cloakbrowser"` is present in `packaging/nono/vr-guard.json`
  `filesystem.allow`, using `$HOME` (not a hardcoded `/Users/` path).
- `NonoWhy_CloakBrowser_AllowedReadWrite` passes (and skips cleanly where `nono`
  is absent).
- `NonoProfileEnsurerTests.EmbeddedContent_EqualsRepoPackagingFile_ByteForByte`
  still passes after a rebuild.
- `./visual-relay check` passes.
- Conventional Commit subject (final): `feat: allow cloakbrowser in vr-guard sandbox profile`.

## Guardrails

- `./visual-relay check` must pass (file-size guard, format, build, tests, screenshot).
- The only changed file is JSON + one test file; the 300-line C#/XAML limit and
  `[AvaloniaFact]` headless-UI rule don't apply (these are non-UI `[Fact]` tests).
- Minimal diff: add the one `allow` entry and the one test; do not reformat the
  profile or reorder existing entries.
