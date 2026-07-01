# Make the Settings Sandbox Lists Faithful to the Enforced Policy on Each OS

The Settings → Sandbox lists don't match what the sandbox actually enforces, and the mismatch differs by
platform:

- **macOS / Linux (nono):** the "Blocked" list shows only five paths (Documents, Desktop, Pictures, Movies,
  Music). But the sandbox also **blocks reads of the user's credentials** — `~/.ssh`, `~/.aws`, `~/.gnupg`,
  cloud tokens, Keychains, shell history, browser data — via inherited deny groups the panel never
  resolves. Credentials are enforced-but-invisible, and in fact most of the inherited read/write policy is
  under-shown too.
- **Windows (MXC):** VR uses a **different** sandbox here — Microsoft Execution Containers (`wxc-exec`) —
  whose model is **write-confinement with broad reads**, not nono's read-deny model. The panel must show
  *that* picture honestly, and must not imply a macOS-shaped "these paths are read-blocked" when on Windows
  nothing is read-blocked.

Make each OS's panel faithful to its own enforcement. (`Screenshot-settings.png` in this folder is the
macOS view — the long Writable list, and a Blocked list with no `~/.ssh`.)

## Current state

`src/VisualRelay.Core/Execution/SandboxPathInspector.cs`, `InspectAsync`, already branches by OS:
`if (OperatingSystem.IsWindows()) return BuildWindowsResult(...)`, else it runs the nono path. The three
lists it returns feed `MainWindowViewModel.Sandbox.cs` → `SandboxPaths.axaml`.

### macOS / Linux — nono, and the un-resolved `extends` chain

Tasks run under `nono run --profile <vr-guard.json> --allow-cwd -- swival …`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs`, `BuildNonoPrefix`). `packaging/nono/vr-guard.json`
starts with `"extends": "swival"`, and the swival pack profile has `"extends": "default"` — so the
effective policy is the resolved chain **vr-guard → swival → default**.

The credential denials are **nono policy groups** included by the parent (swival) profile — not concrete
`filesystem.deny` entries. Verified against the installed nono (0.61.1): `nono profile show vr-guard --json`
returns a fully-resolved profile whose `groups.include` has **25 groups**, including `deny_credentials`,
`deny_keychains_macos`, `deny_keychains_linux`, `deny_shell_history`, `deny_shell_configs`,
`deny_browser_data_macos`, `deny_browser_data_linux`, `deny_macos_private` (plus allow/read groups like
`system_read_*`, `user_tools`, `homebrew_*`, `python_runtime`, `node_runtime`, `user_caches_*`,
`git_config`, …). Expanding the credentials group — `nono profile groups deny_credentials --json` — returns
the same `{raw, expanded}` / `deny.access` shape the inspector already parses:

```json
{ "name": "deny_credentials", "platform": "cross-platform",
  "deny": { "access": [ {"raw":"~/.ssh","expanded":"…/.ssh"}, {"raw":"~/.aws",…}, {"raw":"~/.gnupg",…}, … ] } }
```

(full set: `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.config/gcloud`, `~/.gcloud`, `~/.kube`,
`~/.docker`, `~/.git-credentials`, `~/.netrc`, `~/.npmrc`, `~/.vault-token`, `~/.credentials`, `~/.secrets`,
`~/.keys`, `~/.pki`, `~/.terraform.d`, `~/.config/op`.)

**The gap:** `ParseOwnDirectives` reads vr-guard's own `filesystem.allow/read/deny`, and `ParseGroupJson`
expands only the groups in **vr-guard's own** `groups.include` — just `go_runtime` and `rust_runtime`. The
inspector **never resolves `extends`**, so it misses every inherited group, including all `deny_*`. Result:
enforcement expands 25 groups; the display expands 2.

### Windows — MXC is write-confinement, with broad reads (no read-deny)

`src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs` hand-authors the Windows policy (the analogue of
vr-guard). `Generate(...)` emits **only** `{ filesystem.readwritePaths, network.defaultPolicy:"allow" }`
(schema `PinnedMxcVersion = "0.7.0-alpha"`). Its docstring states the model outright: *"writes are confined
to the workspace plus … toolchain caches …, **reads are broad (the agent must read the system)** …"* — no
`readonlyPaths`, no `deny`, no credential block. The readwrite set is `DefaultWindowsCacheDirs()`
(`%LOCALAPPDATA%`, `%APPDATA%`, `~/.nuget/packages`, `~/.dotnet`, `~/.cargo`, `~/.config/swival`,
CommonAppData\Unity, TEMP). `src/VisualRelay.Core/Execution/WindowsSandbox.cs` `BuildMxcLaunch` runs
`wxc-exec.exe <policy> -- <program>`; nothing adds a read-deny.

`SandboxPathInspector.BuildWindowsResult` today returns: Writable = the MXC cache dirs (source
`"MXC cache dir"`) + workspace; Readable = one synthetic row `"<entire filesystem except blocked paths>"`;
Blocked = one synthetic row `"<writes outside listed paths are blocked>"`. Two issues: the Readable row's
**"except blocked paths"** implies read-exceptions that **don't exist** on Windows, and nothing conveys
that reads (including credentials) are unrestricted here — so a user could wrongly assume macOS-style
credential read-protection.

## What to build

**macOS / Linux** — resolve the `extends` chain so the inspector expands **all** inherited groups, reusing
the existing group expansion:

1. Get the fully-resolved `groups.include` via `nono profile show <profile> --json` (verified: contains all
   25 groups incl. `deny_credentials`), rather than only vr-guard's own `groups.include`.
   - **Staleness caveat:** `nono profile show <name>` reads the *registered* profiles dir, which can be
     stale vs the embedded/enforced vr-guard.json (the registered copy currently reports
     `filesystem.deny: []`, missing the five media-folder denies). So keep sourcing vr-guard's **own**
     directives from `NonoProfileEnsurer.EmbeddedContent` (as today), and use `show` only for the inherited
     group list. (`default` is built into the binary, so `show` — not file-walking — is the reliable way to
     get the whole chain.)
2. Expand every included group through the existing `nono profile groups <name> --json` path
   (`ParseGroupJson`): `deny_*` groups → Blocked (`deny_credentials` → `~/.ssh` …); allow/read groups → the
   (currently under-shown) Readable/Writable lists. Tag `Source` with the group name, honoring the per-group
   OS `platform` filter (`_macos` vs `_linux`).

**Windows** — keep deriving from MXC (`DefaultWindowsCacheDirs()` + workspace), but make the model honest:

- Readable should read as broad **with no exceptions** — e.g. `"<entire filesystem — reads are not
  restricted>"` — not "except blocked paths" (there are none on Windows).
- The Blocked group represents **write** confinement only (`"<writes outside the listed paths are
  blocked>"`), which is correct — keep it, but ensure the panel doesn't imply credentials are read-blocked
  on Windows. Do **not** invent credential / read-deny rows that aren't in the generated MXC policy — today
  it declares none (only `readwritePaths`). (Whether VR should *add* credential denies to the Windows policy
  is a separate, security-level question — not this display task.)

Both branches must stay **derived** from each OS's real mechanism (nono resolved profile / the MXC
generator), never a hardcoded snapshot.

## Constraints & done criteria

- **macOS / Linux:** the Blocked list includes the enforced credential denials — at minimum `~/.ssh` (from
  `deny_credentials`) — each attributed to its group `Source`; the Readable/Writable lists reflect the full
  inherited policy; all derived (adding a path to a group shows up with no code change). **Test:** feed a
  resolved `groups.include` containing `deny_credentials` + a sample `nono profile groups deny_credentials
  --json` payload and assert `~/.ssh` lands in Blocked with `Source = "deny_credentials"`; include a
  `_macos`-vs-`_linux` platform-filter case. Do **not** shell out to real nono in tests.
- **Windows:** the lists reflect MXC — Writable = the MXC cache dirs + workspace; Readable = broad **with no
  claimed read-exceptions**; Blocked = the write-confinement note. The panel must **not** imply credential
  read-protection on Windows. Test `BuildWindowsResult` accordingly (the synthetic Readable row no longer
  says "except blocked paths").
- **Graceful degradation preserved:** nono absent / call fails → the existing "unavailable" state, no throw
  (nono is a `flake.nix` dev-shell binary resolved by bare name `"nono"`; absence must stay non-fatal).
- No weakening of the sandbox — display / derivation only. Keep every edited file within the **≤300-line**
  gate. Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.Core/Execution/SandboxPathInspector.cs` — **nono branch:** resolve the `extends` chain
  (via `nono profile show --json`) then expand every group through the existing `ParseGroupJson`;
  **Windows branch (`BuildWindowsResult`):** fix the Readable wording and keep the honest write-confinement
  model.
- `tests/VisualRelay.Tests/` — resolved-chain / `deny_credentials` tests (nono) **and** a
  `BuildWindowsResult` test.
- (reference, no change) `packaging/nono/vr-guard.json` (`"extends": "swival"`),
  `src/VisualRelay.Core/Execution/NonoProfileEnsurer.cs` (embedded profile),
  `src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs` (Windows model),
  `src/VisualRelay.Core/Execution/WindowsSandbox.cs`,
  `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (`sensitiveSubtrees`), `docs/OPERATIONS.md`.
