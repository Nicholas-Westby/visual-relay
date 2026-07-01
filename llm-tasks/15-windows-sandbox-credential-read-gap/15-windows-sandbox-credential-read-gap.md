# Windows Sandbox (MXC): Emit `deniedPaths` for Credentials, and Warn (Windows-only) That They May Not Be Enforced Yet

The Settings sandbox UI must not falsely imply that credential paths are blocked on Windows. On macOS /
Linux the sandbox (nono) genuinely denies credential **reads** (`~/.ssh`, `~/.aws`, `~/.gnupg`, …) via its
`deny_credentials` group. On **Windows** VR uses Microsoft Execution Containers (`wxc-exec`), and VR's policy
today confines **writes** only — it declares no credential denials at all. MXC *does* have a `deniedPaths`
primitive, but Windows enforcement of it is new and feature-gated (research below), so VR should not assume
it's active. The fix is: **(1)** have VR emit `deniedPaths` for the credential set in its generated policy
(forward-compatible, and enforced wherever MXC honors it), and **(2)** show a **Windows-only** warning that
these denials may not yet be enforced by the underlying sandbox, linking the MXC tracking work. On macOS the
denials **are** enforced, so show them with **no** warning — a scary "not enforced" note there would
needlessly alarm macOS users.

## Current state (researched against microsoft/mxc)

- **VR emits no denials on Windows.** `src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs`, `Generate(...)`
  emits only `{ filesystem.readwritePaths, network.defaultPolicy:"allow" }` — no `deniedPaths`, no
  `readonlyPaths`. Binaries pinned to `microsoft/mxc` `MxcInstaller.PinnedRelease = "v0.7.0-rc1"`; schema
  `PinnedMxcVersion = "0.7.0-alpha"`.
- **MXC's schema has the primitive.** `filesystem.deniedPaths` = *"Paths the process cannot access at all"*
  (default empty array), shared across backends — the analogue of nono's `deny_credentials`.
- **Windows enforcement is new / feature-gated — do not assume it's on.** The MXC README (main) still says
  *"denied paths not yet supported on Windows"*, while the repo is actively adding it: PR #489 *"Enforce
  deniedPaths natively in BaseContainer via fs_deny"* (https://github.com/microsoft/mxc/pull/489) gates
  native enforcement on a `Feature_BfsPolicyDeny` flag with a fail-secure fallback (*"a denied-paths policy
  falls back to the AppContainer+BFS tier with a deny-only DACL"*). That PR is closed-unmerged and the
  `v0.7.0-rc1` release notes don't mention deny support — so VR should treat Windows enforcement as **not
  guaranteed** and message accordingly. (Link whatever is the canonical tracker at authoring time — PR #489
  is the most specific reference found; the `microsoft/mxc` issues page is the fallback.)
- **macOS / Linux already enforce credential denials** (nono `deny_credentials`), so there VR shows them as
  normal Blocked entries with no caveat.

## What to build

1. **Emit `deniedPaths` for the credential set** in the generated Windows policy. In
   `MxcPolicyGenerator.Generate`, add a `filesystem.deniedPaths` array mirroring macOS `deny_credentials`, in
   Windows path forms — e.g. `%USERPROFILE%\.ssh`, `.aws`, `.azure`, `.gnupg`, `.kube`, `.docker`,
   `.git-credentials`, `.netrc`; DPAPI master keys (`%APPDATA%\Microsoft\Protect`); Credential Manager
   (`%LOCALAPPDATA%\Microsoft\Credentials`); browser data (`%LOCALAPPDATA%\Google\Chrome\User Data`, Edge).
   Keep the list in one place (e.g. a `WindowsCredentialDenyDirs()` helper beside `DefaultWindowsCacheDirs()`).
   This is forward-compatible: enforced wherever MXC honors `deniedPaths` (native feature or the
   AppContainer+DACL fallback), harmless where it's ignored.
   - **Precedence:** MXC treats `deniedPaths` as overriding `readwrite`/`readonly` (the repo has a BFS
     precedence fix), so a denied path under `%APPDATA%` isn't re-opened by the broad `%APPDATA%` readwrite
     grant.
2. **Windows-only warning in the UI.** In the Settings sandbox section (`SandboxPaths.axaml` /
   `MainWindowViewModel.Sandbox.cs` / `SandboxPathInspector.BuildWindowsResult`), when the denied credential
   paths are shown **on Windows**, attach a clear caveat — e.g. *"⚠ Configured as denied, but the Windows
   sandbox (MXC) may not enforce denied paths yet — treat these as potentially readable"* — with a hyperlink
   to the MXC tracking (PR #489 / the `microsoft/mxc` filesystem-policy work). The caveat belongs **only** in
   the Windows code path: add it inside `BuildWindowsResult`, and render **no** such caveat on the
   macOS/Linux (nono) result, where the denials are enforced. Reuse the app's existing hyperlink-button idiom
   (as used for "Get a key" / the Obsidian "Reveal" button).
   - Because Windows enforcement isn't guaranteed, the caveat is **shown by default**. Keep it behind a
     single, clearly-named flag/constant so softening or dropping it later is a one-line change.
3. **macOS / Linux:** show the credential denials as normal Blocked entries, **no** caveat.
4. **(Secondary) Narrow the coarse readwrite grant.** `DefaultWindowsCacheDirs()` grants read+write to the
   entire `%LOCALAPPDATA%` and `%APPDATA%`; prefer the specific toolchain-cache subdirs actually needed
   (e.g. `%LOCALAPPDATA%\uv`, `%APPDATA%\npm`, `%USERPROFILE%\.nuget\packages`, `%USERPROFILE%\.dotnet`, …)
   so credential/browser subtrees under AppData aren't writable even where `deniedPaths` isn't enforced.
   Keep the "only dirs that exist" behavior; enumerate the caches so no toolchain regresses.

## Constraints & done criteria

- **The generated Windows policy includes the `deniedPaths` credential set** — assert it directly on
  `MxcPolicyGenerator.Generate(...)` output (a pure function; the test runs anywhere). The set comes from a
  single helper, not a duplicated literal list.
- **The Windows caveat is present and correctly scoped** — assert it by invoking `BuildWindowsResult`
  directly (the Windows result carries the caveat + a valid tracking URL) and asserting the macOS/Linux
  (nono) result carries **no** caveat. Make `BuildWindowsResult` reachable from tests (e.g. `internal` +
  `InternalsVisibleTo`) so this is a plain unit test over the builder output, independent of the running OS.
- Messaging is honest and conservative: the panel does not assert a protection guarantee on Windows, and the
  caveat never appears on macOS / Linux.
- If the readwrite grant is narrowed, the cache set still contains the toolchain dirs a run needs, and the
  skip-nonexistent-dirs behavior is preserved.
- Keep every edited file within the **≤300-line** gate. Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs` — add the `deniedPaths` credential set (own
  helper); optionally narrow the readwrite grant.
- `src/VisualRelay.Core/Execution/SandboxPathInspector.cs` (`BuildWindowsResult`) — surface the denied paths
  in Blocked and attach the Windows-only caveat + tracking link.
- `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml`,
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Sandbox.cs` — render the caveat row + hyperlink.
- `docs/OPERATIONS.md`, `README.md` — document the Windows read/write guarantees and the deniedPaths caveat.
- `tests/VisualRelay.Tests/` — a `Generate(...)` `deniedPaths` test, and a `BuildWindowsResult`-vs-nono
  caveat-scoping test.
- (reference, no change) `src/VisualRelay.Core/Execution/WindowsSandbox.cs`,
  `src/VisualRelay.Core/Execution/MxcProvisioner.cs`,
  `src/VisualRelay.Core/Execution/MxcInstaller.cs` (pinned `microsoft/mxc` `v0.7.0-rc1`); MXC PR #489 and the
  MXC README limitation note.
