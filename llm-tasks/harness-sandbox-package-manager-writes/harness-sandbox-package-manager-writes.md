# Harness: keep verification inside the nono sandbox by granting package-manager writes

Visual Relay confines Swival writes/deletes with the **nono** OS sandbox (Seatbelt on macOS,
Landlock on Linux) — but the **verification commands** (the test command, the repo guard, the
bootstrap smoke-check, and the new-guard probe) currently run **UNSANDBOXED on the host**. They
escape because the `vr-guard` profile is deny-by-default for writes and grants only the workspace
plus `~/.config/swival`; a target repo's `dotnet restore` / `swift build` / `npm install` writes
to `~/.nuget/packages`, `~/.swiftpm`, `~/.npm`, etc., which nono blocks, so those commands could
not succeed in-sandbox and were left on the host.

This task **eliminates the unsandboxed escape**: extend the `vr-guard` allowlist to cover the
read/write paths the major toolchains legitimately need (package caches, toolchain state, temp),
then **route verification through nono** so `restore` / `build` / `test` succeed *inside* the
sandbox. The destructive surface (`~/Documents`, `~/Pictures`, the rest of `~/Library`, other
users' homes, system dirs, credentials) **stays denied** — the sandbox's job is OS-stability and
not clobbering the user's documents/photos, not blocking routine build caches. The existing
**bypass toggle** (the "Bypass nono sandbox" checkbox → `bypassSandbox` config key) remains the
*only* sanctioned no-sandbox path; there is no other host escape.

## Current state (researched)

> **Freshness contract.** The line numbers and quoted snippets below are a snapshot taken
> 2026-06-15 and may have drifted. **Locate every anchor by searching for the quoted code/JSON,
> not by line number.** If a quoted snippet no longer matches verbatim, treat this section as
> stale, re-read the whole file, and adapt to what is actually there before editing. Re-run the
> `nono` probes (commands given inline) against the installed nono version before relying on a
> specific verdict — nono's bundled `swival` pack and policy groups can change between versions.

### 1. How Swival is sandboxed today (the part that already works)

VR runs `nono` as the **wrapper** of Swival (it does NOT use Swival's own `--sandbox nono`).
`src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs` — `BuildLaunchTarget`:

```csharp
internal (string FileName, IReadOnlyList<string> Arguments) BuildLaunchTarget(List<string> swivalArguments)
{
    if (_config.BypassSandbox)
        return (_swivalBinary, swivalArguments);

    var nonoArguments = new List<string>
    {
        "run",
        "-p", NonoProfile,        // "vr-guard"
        "--allow-cwd",
        "--rollback",
        "--no-rollback-prompt",
        "--",
        _swivalBinary
    };
    nonoArguments.AddRange(swivalArguments);
    return (NonoBinary, nonoArguments);   // NonoBinary = "nono"
}
```

`NonoBinary = "nono"` and `NonoProfile = "vr-guard"` are constants in
`src/VisualRelay.Core/Execution/ProcessRunners.cs`. `BypassSandbox` defaults to **`false`**
(sandbox ON) — `src/VisualRelay.Domain/RelayConfig.cs` (`bool BypassSandbox = false`) and
`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` (`BypassSandbox: false` in `Defaults`,
merged via `OptionalBool(root, "bypassSandbox", …)` in `TryLoadAsync`).

The Swival run path also already redirects **Swival's own** transitive caches into the granted
`~/.config/swival` tree so they don't hit the deny wall — `ProcessRunners.Helpers.cs`,
`BuildSandboxEnvironment`:

```csharp
// Build environment overrides that redirect transitive-dependency caches
// into ~/.config/swival (already in the swival profile write-allow list)
// so nono's vr-guard sandbox does not block them.
internal static IReadOnlyDictionary<string, string>? BuildSandboxEnvironment(RelayConfig config)
{
    if (config.BypassSandbox) return null;
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return new Dictionary<string, string>
    {
        ["HF_HOME"] = Path.Combine(home, ".config", "swival", "huggingface"),
        ["XDG_CACHE_HOME"] = Path.Combine(home, ".config", "swival", "cache"),
        ["UV_CACHE_DIR"] = Path.Combine(home, ".config", "swival", "uv-cache"),
    };
}
```

This pattern (env-redirect caches into an allowed root) is **reusable** for verification — see
"What to build". It is applied in `ProcessRunners.RunAsync.cs` (search `BuildSandboxEnvironment(_config)`),
passed as `environment:` to `ProcessCapture.RunAsync`.

### 2. The `vr-guard` profile and what nono grants by default

The shipped profile is `packaging/nono/vr-guard.json` (installed to
`${XDG_CONFIG_HOME:-$HOME/.config}/nono/profiles/vr-guard.json` by the launcher's `_provision_nono`
in `./visual-relay`). Current content:

```json
{
  "extends": "swival",
  "meta": { "name": "vr-guard", "description": "Visual Relay guard: broad read + network + tools open; writes and deletes confined to the granted workspace. Accident containment, not adversarial isolation." },
  "filesystem": { "read": ["/"] },
  "allow_parent_of_protected": true,
  "unsafe_macos_seatbelt_rules": [
    "(allow user-preference-read)", "(allow ipc-posix-shm*)", "(allow mach-register)",
    "(allow sysctl-read)", "(allow iokit-open)", "(allow iokit-get-properties)", "(allow system-socket)"
  ]
}
```

`vr-guard` **extends `swival`** (a registry pack pulled by `nono pull jedisct1/swival`). Inspect
the resolved base with `nono profile show swival`. Verified 2026-06-15 (nono 0.62) — the `swival`
base already brings these **policy groups** (a curated default-deny + per-ecosystem write-grants):

- Toolchain write-grants already present: `python_runtime` (pyenv/conda/uv), `node_runtime`,
  `user_caches_macos` / `user_caches_linux`, `homebrew_macos` / `homebrew_linux`, `system_read_*`,
  `system_write_*`, `user_tools`, `git_config` (read).
- **NOT present** (so their caches are denied for writes): `go_runtime`, `rust_runtime`,
  `java_runtime`, `nix_runtime`. List all 24 with `nono profile groups`.
- **No .NET group and no Swift group exist in nono at all** — `~/.nuget`, `~/.dotnet`,
  `~/.swiftpm`, `~/Library/*.swiftpm`, `~/Library/Developer` must be granted explicitly.
- Deny groups in force (the destructive surface, kept): `deny_credentials`, `deny_keychains_*`,
  `deny_browser_data_*`, `deny_macos_private`, `deny_shell_history`, `deny_shell_configs`,
  `unlink_protection` (blocks deletion globally; overridden for user-writable grants),
  `dangerous_commands*`. The `swival` base grants r+w only to `~/.config/swival`,
  `~/.local/share/swival`, and nono's own profile dirs; workdir access is `readwrite`.

**The escape, proven.** Even paths the existing groups exist for are write-denied under `vr-guard`
because `filesystem.read:["/"]` grants only *read*; deny-by-default still blocks *writes* outside
the explicit allowlist. Verified with `nono why` (2026-06-15, nono 0.62):

```
$ nono why -p vr-guard --op readwrite --path ~/.nuget/packages/x
  DENIED  Reason: insufficient_access
  Details: Path is covered by '/', which grants read access from profile but read+write was requested
  Suggested fix: --allow /Users/.../.nuget/packages
$ nono why -p vr-guard --op readwrite --path ~/.swiftpm/x        → DENIED (--allow ~/.swiftpm)
$ nono why -p vr-guard --op readwrite --path ~/.cache/pip/x      → DENIED (--allow ~/.cache/pip)
$ nono why -p vr-guard --op readwrite --path ~/.npm/x            → DENIED (--allow ~/.npm)
$ nono why -p vr-guard --op write     --path ~/Documents/x       → DENIED  ✓ (must stay denied)
```

So `dotnet restore` / `swift build` / `npm install` cannot write their caches in-sandbox today,
which is exactly why verification was left on the host.

### 3. nono's interface for granting extra paths (verified, nono 0.62)

The **profile schema** is the right surface (run `nono profile schema` / `nono profile guide`).
Under `filesystem`, all are arrays and **support variable expansion**:

| field | meaning |
|-------|---------|
| `allow` | directories with **read+write** (recursive) |
| `read` | directories read-only (recursive) |
| `write` | directories **write-only** (recursive) — *deletion NOT included* |
| `allow_file` / `read_file` / `write_file` | single-file variants |
| `deny` | paths denied filesystem access |
| `bypass_protection` | exempts a path from a deny **group** (does NOT itself grant — the path must also appear in `allow`/`read`/`write`) |

Expanded variables in path fields: `$HOME`, `$WORKDIR`, `$TMPDIR`, `$UID`, `$XDG_CONFIG_HOME`,
`$XDG_DATA_HOME`, `$XDG_STATE_HOME`, `$XDG_CACHE_HOME`. **Platform predicates** are supported —
an entry may be a plain string or `{ "path": "...", "when": "macos" }` (also `linux`,
`linux:ubuntu:>=24.04`, `!linux:nixos`, arrays for any-of). This is how the macOS-only
`~/Library/...` Swift paths and the Linux-only `~/.cache/...` equivalents are expressed in one
profile.

**Inheritance semantics (load-bearing):** for a child profile, **array fields including
`filesystem.*` are appended to the base and de-duplicated** — so `vr-guard` adding to
`filesystem.allow` keeps everything `swival` granted. Critically, *"there is no mechanism to remove
inherited filesystem paths"* — adding grants is safe and additive; we cannot accidentally drop a
base deny by editing the child. The CLI equivalents (`nono run -a/--read/--write/--bypass-protection
<dir>`) exist too, but **prefer the profile** so the grant set is declarative, reviewable, version-
controlled, and identical for the Swival run and the verification run.

### 4. How verification commands run today — and why they escape

All four verification surfaces go through `_dependencies.TestRunner.RunAsync(rootPath, cmd, ct)`,
and the production `ITestRunner` is **`ShellTestRunner`** (and `DirectExecTestRunner` for init),
both of which exec **directly on the host** via `ProcessCapture.RunAsync` with **no nono wrapper**:

- **Test command** — `src/VisualRelay.Core/Execution/RelayDriver.Bootstrap.cs`,
  `RunTestCommandWithRetryAsync`: `await _dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, ct)`.
- **Guard + format** — `src/VisualRelay.Core/Execution/RelayDriver.RepoGuards.cs`,
  `RunGuardCheckAsync` / `IntegrateGuardAsync`: runs `config.FormatCommand` then `config.GuardCommand`
  via the same runner (including the stash/baseline second run).
- **Bootstrap smoke** — same file/flow; `ResolveBootstrapCheck` yields e.g. `nix develop --command true`,
  run through the runner.
- **New-guard probe** — `RelayDriver.RepoGuards.cs`, `NewGuardProbeAsync`. Its own doc-comment says
  it *"runs each once **unsandboxed on the host**"*:

```csharp
var candidates = manifest
    .Where(entry => patterns.Any(p => MatchesGuardGlob(entry, p)))
    .Select(entry => Path.Combine(rootPath, entry))   // ⚠ no containment check (see §6 below)
    .Where(File.Exists)
    .ToList();
...
var result = await _dependencies.TestRunner.RunAsync(rootPath, scriptPath, ct);
```

Production wiring of the runner (search these):
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` →
`new RelayDriverDependencies(subagentRunner, new ShellTestRunner(TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds)), sink)`;
`tools/VisualRelay.RunTask/Program.cs` and `tools/VisualRelay.DrainQueue/Program.cs` →
`new ShellTestRunner(...)`. `ShellTestRunner.RunAsync` is just
`ProcessCapture.RunAsync("/bin/sh", "-lc \"<cmd>\"", rootPath, _timeout, ct)`.

### 5. The bypass toggle exists (confirm — it does)

The "Bypass nono sandbox" checkbox is DONE (`llm-tasks/DONE-sandbox-3-add-a-bypass-nono-checkbox-to-the-ui.md`).
`[ObservableProperty] bool _bypassSandbox` lives in
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs`; `OnBypassSandboxChanged` calls
`RelayConfigWriter.UpsertBypassSandbox(RootPath, value)` (a key-preserving read-modify-write in
`src/VisualRelay.Core/Init/RelayConfigWriter.cs`); the VM hydrates it in
`MainWindowViewModel.Helpers.cs` (`BypassSandbox = configResult.Config.BypassSandbox`). README §Sandbox
documents the key. **This toggle is the only sanctioned escape and must stay the only one** — this
task must NOT add a second "run on host" path.

### 6. Related defect — `NewGuardProbeAsync` path-traversal gap

In `NewGuardProbeAsync` (above), `Path.Combine(rootPath, entry)` is executed for any manifest entry
matching a `NewGuardPatterns` glob (default `["tools/guards/**/*.sh"]`) with **no check that the
resolved path stays under `rootPath/tools/guards/`**. A manifest entry like
`tools/guards/../../../../tmp/evil.sh` resolves outside the guards dir and would be executed as an
arbitrary host script. Once guards run in-sandbox (this task) the blast radius shrinks to the
sandbox, but the spec still calls for a belt-and-suspenders containment check.

## What to build

Three coordinated changes — (A) extend the allowlist, (B) route verification through nono, (C) the
path-containment fix. Keep everything **general and platform-agnostic**; VR drives any codebase, so
the allowlist must be extensible per-ecosystem and not hard-coded to .NET.

### A. Extend `vr-guard` to allow the toolchain read/write paths

Add the per-ecosystem grants to `packaging/nono/vr-guard.json` under `filesystem.allow` (r+w),
`filesystem.read` (read-only), and `filesystem.write` (write-only where deletion must stay blocked),
using `$HOME`/`$XDG_*`/`$TMPDIR` expansion and `when` predicates for OS-specific paths. Because
`swival` already grants Node/Python/Homebrew/system groups, only the **gaps** need to be added —
but enumerate the full intended set in the profile (a few redundant grants are harmless and
de-duplicated on inherit), so the profile is self-documenting and survives a base-pack change that
drops a group. **Verify each entry with `nono why -p vr-guard --op readwrite --path <p>` after
editing** — the profile is the source of truth, not this list.

Concrete groups to cover (recursive dir grants unless noted). **Every per-ecosystem cache is
specified for BOTH platforms**: paths with no `when` apply everywhere; `{ "path": …, "when": "macos" }`
and `{ "path": …, "when": "linux" }` carry the OS-specific cache locations. Seatbelt and Landlock
differ in path granularity, so the Linux set is enumerated explicitly here (not "the macOS paths
minus `~/Library`") and is a hard validation target — see §Done when.

- **.NET** — shared: `$HOME/.nuget/packages`, `$HOME/.dotnet`, `$HOME/.templateengine`, the
  `DOTNET_CLI_HOME` root. The NuGet http-cache differs by OS:
  `{ "$HOME/Library/Caches/NuGet/v3-cache", "when": "macos" }` (and `$HOME/.local/share/NuGet`),
  `{ "$HOME/.local/share/NuGet", "when": "linux" }` + `{ "$XDG_CACHE_HOME/NuGet", "when": "linux" }`.
  Plus `$TMPDIR` (macOS `/var/folders`) / `/tmp`. (Also honor `$NUGET_PACKAGES` if the env-redirect
  route of B is taken.) Workspace `obj/` and `bin/` are already covered by the `readwrite` workdir grant.
- **Swift / SwiftPM** — shared: `$HOME/.swiftpm`. macOS-only:
  `{ "$HOME/Library/Caches/org.swift.swiftpm", "when": "macos" }`,
  `{ "$HOME/Library/org.swift.swiftpm", "when": "macos" }`, the module/derived-data cache
  `{ "$HOME/Library/Developer", "when": "macos" }` (or the `CLANG_MODULE_CACHE_PATH` root), and
  `DEVELOPER_DIR` as **read-only**. Linux-only (no `~/Library` exists):
  `{ "$HOME/.cache/org.swift.swiftpm", "when": "linux" }` and
  `{ "$XDG_CACHE_HOME/org.swift.swiftpm", "when": "linux" }` (both forms, since `$XDG_CACHE_HOME`
  may be unset and default to `~/.cache`). Plus `$TMPDIR` / `/tmp`; workspace `.build/` covered by workdir.
- **Node / npm / pnpm / yarn / Bun / Deno** — shared: `$HOME/.npm`, `$HOME/.bun`, `$HOME/.deno`,
  `$HOME/.pnpm-store`, `$HOME/.yarn`, the `COREPACK_HOME` root. Caches:
  `{ "$HOME/Library/Caches/node-gyp", "when": "macos" }` /
  `{ "$XDG_CACHE_HOME", "when": "linux" }` (`~/.cache`, npm/yarn/deno — already largely covered by
  `user_caches_linux`; confirm). Grant `$HOME/.config` *selectively* (some tools write a specific
  subdir — grant that subdir, NOT all of `~/.config`, to avoid re-opening denied trees). Plus
  `$TMPDIR`; workspace `node_modules/` covered by workdir.
- **Python / pip / venv / uv / pyenv** — shared: `$HOME/.local`, `$HOME/.pyenv`. Caches:
  `{ "$HOME/Library/Caches/pip", "when": "macos" }` + `{ "$HOME/Library/Caches/uv", "when": "macos" }`,
  `{ "$HOME/.cache/pip", "when": "linux" }` + `{ "$HOME/.cache/uv", "when": "linux" }`
  (and the `$XDG_CACHE_HOME/{pip,uv}` forms). Plus `$TMPDIR`; workspace `.venv/` covered by workdir.
  (`python_runtime` covers pyenv/uv already — confirm and keep the explicit entries as belt-and-braces.)
- **Go / Rust (gaps the base omits)** — Go: shared `$HOME/go/pkg/mod`, plus `GOCACHE`
  (`{ "$HOME/Library/Caches/go-build", "when": "macos" }` /
  `{ "$HOME/.cache/go-build", "when": "linux" }`, also `$XDG_CACHE_HOME/go-build`). Rust: shared
  `$HOME/.cargo/registry`, `$HOME/.cargo/git`; workspace `target/` (workdir). Or include the
  `go_runtime` / `rust_runtime` groups via `groups.include` instead of listing paths — **prefer the
  group** when it exists and matches, since it is maintained upstream; the explicit per-OS paths above
  are the fallback when the base pack has no such group.
- **General** — toolchain install roots are already readable via `read:["/"]`; if Nix is used,
  `/nix/store` is read-only and already readable. `$HOME/.gitconfig` read (covered by `git_config`).
  The system trust store and read-only system paths come from `system_read_*`.

**Stays DENIED** (deny-by-default already enforces these; call them out so a reviewer can confirm
the profile did not accidentally open them, and so the test in §Tests can assert them):
`$HOME/Documents`, `$HOME/Desktop`, `$HOME/Pictures`, `$HOME/Movies`, `$HOME/Music`, the rest of
`$HOME/Library` (outside the specific toolchain subdirs granted above), other users' homes,
`/System`, `/Applications`, `/etc` (writes), `/usr` (writes), and the credential/keychain/browser/
shell-history/shell-config trees held by the `deny_*` groups.

**Make the set configurable/extensible — use BOTH mechanisms, with distinct roles** (settled; see
Decisions). They are not alternatives; they sit at different layers and compose by union:

1. **The profile (`vr-guard.json`) carries the per-ecosystem baseline** (.NET / Swift / Node /
   Python / Go / Rust, the lists above). This is the shipped default so the common toolchains work
   out-of-the-box, and it is **declarative and readable by any nono user** (`nono profile show vr-guard`),
   reviewable, and version-controlled. Operators editing the baseline add `filesystem.allow` entries
   / `groups.include` here. The profile is the source of truth for the common case.
2. **`RelayConfig.SandboxExtraAllowPaths: IReadOnlyList<string>?` is the per-repo escape hatch** for
   exotic toolchains the baseline does not cover. It travels in `.relay/config.json` next to
   `testCmd`, so a repo with an unusual cache dir is self-describing without forking the shipped
   profile. When the sandbox is enabled, each entry is appended as an `-a <path>` flag to BOTH the
   Swival `nono run` invocation (`BuildLaunchTarget`) and the verification `nono run` invocation (B),
   so Swival and verification see an identical grant set.

   **Precedence / merge:** the effective allowlist is the **union** of the profile's grants and the
   config field's `-a` flags (nono de-duplicates; order is irrelevant). Both layers grant **read+write**
   — there is no read-only or write-only convention on the config field in this iteration (if a
   read-only escape hatch is ever needed, add a separate `SandboxExtraReadPaths` later rather than
   overloading this one). The config field never *removes* a grant; like nono's inheritance, it is
   additive only.

   **Validation / normalization** (do this in `RelayConfigLoader`: read the array like
   `OptionalStringArray(root, "sandboxExtraAllowPaths")`, but route validation failures through the
   error-returning path used by `TryReadStringArray`/`RelayConfigStatus` so a bad entry surfaces as a
   config load error, not a silent drop): default `null`/empty (no extra grants); reject any entry
   containing `..` (path traversal) with a load error; expand a leading `~` and `$HOME` to the user profile
   in C# before passing to nono (nono expands `$HOME` itself, but normalize defensively); and require
   each normalized path to resolve **under `$HOME` or under the workspace root** — an absolute path
   pointing at `/etc`, `/System`, another user's home, or the credential trees is rejected, so the
   escape hatch cannot be used to re-open the destructive surface the deny groups protect. Add the
   field with a doc-comment to `RelayConfig.cs` (positional record; follow the `BoostTurnsTaskIds` /
   `NewGuardPatterns` precedent) and document the `sandboxExtraAllowPaths` key in README §Sandbox.

Keep the **env-redirect** option (B) in mind as a third, narrowest tool: redirecting a toolchain's
cache into the already-granted workspace or `~/.config/swival` (via `NUGET_PACKAGES`, `GOCACHE`, `CARGO_HOME`,
`npm_config_cache`, `UV_CACHE_DIR`, …) is an equally valid way to satisfy a write without widening
the profile, and it keeps caches scoped per-run. Prefer the **narrowest** approach that lets
`restore`/`build`/`test` pass: profile grant for shared immutable caches (NuGet packages), env-
redirect for anything that should not leak across repos.

### B. Route verification through nono (the core change)

Verification must run under the **same** `nono run -p vr-guard ...` wrapper as Swival, so the
extended allowlist applies to `restore`/`build`/`test`/guard/bootstrap/new-guard. Design:

- Introduce a **sandbox-aware `ITestRunner`** — e.g. `SandboxedTestRunner` — that wraps an inner
  runner and, when the sandbox is enabled, transforms the command into a nono-wrapped invocation
  before exec, applying the same `BuildSandboxEnvironment` redirects. **Factor the nono prefix into a
  shared helper** so the Swival path and the verification path can't drift — extract
  `BuildLaunchTarget`'s `run -p vr-guard --allow-cwd … --` builder, with **`rollback` as a parameter**
  (the one deliberate difference: Swival passes `rollback: true` → emits `--rollback
  --no-rollback-prompt`; verification passes `rollback: false` → omits them, per Decisions) and the
  `SandboxExtraAllowPaths` entries appended as `-a <path>` flags. For a shell command (`ShellTestRunner`
  semantics) the verification wrapped form is
  `nono run -p vr-guard --allow-cwd [-a <extra> …] -- /bin/sh -lc "<cmd>"` (note: **no `--rollback`**);
  for a direct exec (`DirectExecTestRunner`, single script path) it is
  `nono run -p vr-guard --allow-cwd [-a <extra> …] -- <script> <args>`. The Swival form
  (`BuildLaunchTarget`, unchanged) keeps `--rollback --no-rollback-prompt` between `--allow-cwd` and
  `--`. The runner takes `RelayConfig` (for `BypassSandbox` and any `SandboxExtraAllowPaths`) so it
  decides per-run.
- **Wire it in production** at every site that currently constructs `ShellTestRunner` /
  `DirectExecTestRunner` for execution: `MainWindowViewModel.Execution.cs`,
  `tools/VisualRelay.RunTask/Program.cs`, `tools/VisualRelay.DrainQueue/Program.cs`. When
  `BypassSandbox == true`, the wrapper is a pass-through to the inner host runner (so the bypass
  checkbox still disables sandboxing for verification too — keeping ONE escape).
- **Rollback — OMITTED for verification** (settled; see Decisions). The verification wrapper uses
  `nono run -p vr-guard --allow-cwd --` **without** `--rollback` / `--no-rollback-prompt`. Rollback
  exists to undo the *agent's* exploratory edits during the Swival run; a verification command
  (test/guard/bootstrap/new-guard) that mutates the tree outside the workspace is **a bug to surface,
  not silent state to undo** — rolling it back would hide a misconfigured `testCmd`/guard that writes
  where it shouldn't. So verification does **not** snapshot/restore: if a verify command somehow
  mutates a granted path it should not, that surfaces as a real effect (and the deny groups still
  block the destructive surface regardless). This also avoids snapshotting on every test loop, which
  would be slow. Keep `--rollback`/`--no-rollback-prompt` on the **Swival run only** (unchanged in
  `BuildLaunchTarget`). Keep `--allow-cwd` on the verification wrapper so the workspace stays writable
  for `obj/`/`.build/`/`node_modules/`. The shared nono-prefix builder (below) must therefore expose
  rollback as a parameter (on for Swival, off for verification) — that is the *one* deliberate flag
  difference between the two callers, and the shared-prefix test (§Tests) pins both shapes so they
  cannot drift accidentally.
- **Planning-phase runners** (`PlanPhaseRunner` / `RelayQueueController` `planTestRunner`) run in
  isolated worktrees and are read-only-ish; apply the same wrapping for consistency, or document why
  they stay on the host. (Plan stages 1–4 are read-only, so the risk is low, but the test/guard they
  may invoke still benefits from the allowlist.)
- **Init validation** (`TestCommandValidator` via `DirectExecTestRunner` in
  `tools/VisualRelay.Init/Program.cs`) runs *before* a repo is configured and may run before nono is
  even guaranteed present; keep it on the host OR wrap it only when nono resolves. Note: `init` does
  not run Swival, so its escape is not the one this task targets — but be explicit about leaving it
  host-side so a future reader does not think it was missed.

Net effect: with the sandbox enabled (default), **every** `restore`/`build`/`test`/guard/bootstrap/
new-guard command runs under `nono -p vr-guard`; the only host-side execution left is the
deliberately-bypassed path (checkbox) and pre-config `init` validation.

### C. Contain the new-guard probe path (belt-and-suspenders)

In `NewGuardProbeAsync`, after `Path.Combine(rootPath, entry)`, reject any candidate whose
`Path.GetFullPath(...)` does not start with `Path.GetFullPath(Path.Combine(rootPath, "tools", "guards"))`
(use the directory implied by the matched pattern's literal prefix if patterns are configurable;
default to the `tools/guards/` root). Drop (and optionally log a `warn` event for) any entry that
escapes containment, rather than executing it. This holds even when guards run in-sandbox, since the
sandbox allowlist now grants workspace + caches and a `..`-escape could still hit a granted path.

## Tests

Write failing tests first. Mix of unit (argument-shape, pure logic) and an opt-in integration tier
that actually invokes nono (skipped when `nono` is absent, mirroring how existing sandbox/real-run
tests gate).

### Unit (always run)

- **Sandboxed runner argument shape.** With `BypassSandbox == false`, `SandboxedTestRunner`
  transforms `bun test` into an exec of `nono` whose args are exactly
  `run -p vr-guard --allow-cwd -- /bin/sh -lc "bun test"` — assert the prefix via the shared builder,
  the `--` separator, that the inner command/script is unchanged, **and that `--rollback` /
  `--no-rollback-prompt` are ABSENT** (the verification path omits rollback per Decisions). With
  `BypassSandbox == true`, it execs the inner host runner unchanged (no `nono`, no `run`). Mirror the
  existing assertions in `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs`
  (`BuildLaunchTarget_*`).
- **Shared nono-prefix builder pins BOTH shapes.** One test pins the shared builder so the two
  callers provably differ in exactly one flag and nothing else: with `rollback: true` (Swival) it
  emits `run -p vr-guard --allow-cwd --rollback --no-rollback-prompt --`; with `rollback: false`
  (verification) it emits `run -p vr-guard --allow-cwd --` — identical but for the rollback pair.
  Also assert that `SandboxExtraAllowPaths` entries are appended as `-a <path>` flags in both shapes.
- **Profile JSON is valid and well-formed.** Parse `packaging/nono/vr-guard.json`; assert it
  `extends: "swival"`, contains the new `.NET`/`Swift` allow entries, and uses `$HOME`/`when`
  expansion (not hardcoded `/Users/...`). Optionally shell out to `nono profile validate
  packaging/nono/vr-guard.json` in the integration tier.
- **New-guard containment.** A manifest entry `tools/guards/../../evil.sh` is dropped (not executed)
  by `NewGuardProbeAsync`; a legitimate `tools/guards/check.sh` is still selected. Assert via the
  candidate-selection seam (extract it if not already testable) without executing anything.
- **Config field `sandboxExtraAllowPaths` (always present — both mechanisms ship).**
  `RelayConfigLoader` reads `sandboxExtraAllowPaths` from `.relay/config.json` and defaults it to
  empty/null; the runner appends `-a <expanded path>` for each, in BOTH the Swival and verification
  invocations. **Validation/normalization asserts:** a leading `~`/`$HOME` expands to the user
  profile; an entry containing `..` produces a config **load error** (not a silent drop); an absolute
  path outside `$HOME` and outside the workspace root (e.g. `/etc/x`, another user's home) is
  **rejected**; a legitimate `$HOME/.cache/exotic-tool` is accepted and forwarded as `-a`.

### Integration (opt-in; skip when `nono` not on PATH — prove the escape is closed)

Use the `nono why` oracle for cheap, deterministic allow/deny assertions (no real build needed for
the path checks), plus at least one real in-sandbox build for .NET and Swift:

- **Allowed paths resolve r+w under vr-guard — per OS.** Assert `nono why -p vr-guard --op readwrite
  --path <p>` reports **allowed** (parse for `ALLOWED` / absence of `DENIED`) for the platform's
  cache set. On **macOS**: `~/.nuget/packages`, `~/.dotnet`, `~/.swiftpm`,
  `~/Library/Caches/org.swift.swiftpm`, `~/Library/Caches/pip`, `~/.npm`. On **Linux**:
  `~/.nuget/packages`, `~/.dotnet`, `~/.swiftpm`, `~/.cache/org.swift.swiftpm`, `~/.cache/pip`,
  `~/.npm`, `~/go/pkg/mod`, `~/.cache/go-build`. These are the regression tests for §A — they FAIL
  today (all currently `DENIED`) and pass after the grants land. Because the `when` predicates gate
  the OS-specific paths, this test gives Landlock-vs-Seatbelt path granularity real coverage rather
  than assuming the macOS grants carry over.
- **Denied paths stay denied (both OS).** `nono why -p vr-guard --op write --path ~/Documents/x`
  (and `~/Pictures`, `~/Desktop`, a credential path like `~/.ssh`) reports **DENIED**. This proves the
  widened allowlist did not open the destructive surface. (`~/Pictures`/`~/Movies` are macOS-shaped;
  on Linux substitute the XDG user dirs, e.g. `~/.ssh` and `~/.gnupg`, which must stay denied.)
- **Real .NET build in-sandbox — macOS AND Linux.** In a scratch .NET repo, run the configured
  test/guard command through `SandboxedTestRunner` (sandbox enabled) and assert `dotnet restore` +
  `dotnet build` + `dotnet test` exit 0 — i.e. NuGet restore writes succeed under nono. **This must
  pass on both macOS (Seatbelt) and Linux (Landlock)** so the per-OS `.NET` grants are validated, not
  assumed. (This is the real-run smoke the memory `pipeline-mocks-process-layer-blindspot` warns is
  needed — a mocked test can pass while the real nono-wrapped exec breaks.)
- **Real non-.NET build in-sandbox — macOS AND Linux.** At least one non-.NET ecosystem must also pass
  a real in-sandbox restore+build+test on both platforms (so a non-`~/Library` cache layout is
  exercised on Linux). Swift is the canonical choice (scratch `Package.swift`: `swift build` + `swift
  test` exit 0 under the sandbox — SwiftPM cache + `.build/` writes succeed) **where SwiftPM is
  available on the Linux runner**; if Swift-on-Linux is not provisioned in CI, substitute Node
  (`npm ci` + `npm test`) or Go (`go build` + `go test ./...`) as the cross-platform non-.NET smoke.
  The requirement is: .NET + one other ecosystem, each green in-sandbox on macOS and Linux.
- **Bypass still escapes.** With `bypassSandbox: true`, the same commands run on the host (no nono in
  the process tree) — assert the runner did not wrap.

## Done when

- The four verification surfaces — test command, guard (`IntegrateGuardAsync`/`RunGuardCheckAsync`),
  bootstrap smoke-check, and `NewGuardProbeAsync` — execute under `nono run -p vr-guard --allow-cwd`
  **without `--rollback`** when the sandbox is enabled (the default), at every production wiring site
  (`MainWindowViewModel.Execution.cs`, `RunTask`, `DrainQueue`). The Swival run keeps `--rollback
  --no-rollback-prompt`; the two callers share one prefix builder differing only in that flag pair.
  No verification command runs on the host while the sandbox is on, except the deliberately-bypassed
  path.
- `packaging/nono/vr-guard.json` grants the per-ecosystem read/write paths (≥ .NET and Swift fully;
  Node/Python/Go/Rust covered by group-or-grant), with **explicit macOS and Linux cache entries**
  expressed via `$HOME`/`$XDG_*`/`when` predicates, and `nono why` confirms each new path is
  r+w-allowed **on its platform** while `~/Documents`/`~/Pictures`/`~/.ssh` remain denied on both.
- A real `dotnet restore`/`build`/`test` **and** a real build+test for at least one non-.NET ecosystem
  (Swift, or Node/Go where Swift-on-Linux is unavailable) complete **in-sandbox on BOTH macOS and
  Linux** (scratch repos), proving package-manager writes succeed under both Seatbelt and Landlock —
  the path-granularity difference is validated, not assumed.
- The `bypassSandbox` checkbox remains the **only** no-sandbox path — both Swival and verification
  honor it, and there is no other host escape.
- `NewGuardProbeAsync` rejects manifest entries that resolve outside the guards directory.
- The allowlist is extensible via **both** layers: the `vr-guard.json` per-ecosystem baseline (the
  declarative default any nono user can read) **and** `RelayConfig.SandboxExtraAllowPaths` in
  `.relay/config.json` (the per-repo escape hatch, validated to reject `..` and out-of-`$HOME`/
  -workspace paths, appended as `-a` flags to both nono invocations).
- `./visual-relay check` is green; all touched files stay under 300 lines; Conventional Commit
  subjects.

## Decisions

These three design questions are **settled** — the body above is written to them; an implementer
needs no further design input.

1. **How to express the extra allow-paths → BOTH, with distinct roles** (profile baseline + config
   escape hatch). The `vr-guard.json` profile carries the per-ecosystem baseline (.NET/Swift/Node/
   Python/Go/Rust) so the common toolchains work out-of-the-box and the grant set is declarative and
   readable by any nono user; `RelayConfig.SandboxExtraAllowPaths` (a `string[]` in `.relay/config.json`)
   is the per-repo escape hatch for exotic toolchains, appended as `-a` flags to both the Swival and
   verification `nono run` invocations. The effective allowlist is their **union** (nono de-dups),
   both layers read+write, additive-only. *Why:* the two needs are different layers — a readable,
   shared, version-controlled default that benefits every repo, and a repo-local override that travels
   with the codebase that needs it — so forcing a single mechanism would either bloat the shipped
   profile with one-off paths or hide the common grants behind per-repo config. The config field is
   validated (reject `..`; expand `~`/`$HOME`; require resolution under `$HOME` or workspace) so the
   escape hatch cannot re-open the destructive surface the deny groups protect.

2. **`--rollback` on the verification wrapper → OMIT it** (verification runs `nono … --allow-cwd --`
   with no rollback). Rollback stays on the Swival run only. *Why:* rollback exists to undo the
   *agent's* exploratory edits during a Swival run. A verification command (test/guard/bootstrap/
   new-guard) that mutates the tree outside the workspace is a **bug to surface** — a misconfigured
   `testCmd`/guard writing where it shouldn't — not silent state to undo; rolling it back would mask
   the misconfiguration. Omitting it also avoids snapshotting on every verify loop (slower). The deny
   groups still block the destructive surface regardless, so omitting rollback does not widen risk.

3. **Linux/Landlock → enumerate the Linux cache set explicitly and validate on Linux.** The profile
   carries macOS paths (`~/Library/...`) and their Linux equivalents under `when: linux` predicates:
   .NET `~/.nuget/packages` + `~/.dotnet` (shared) with Linux http-cache `~/.local/share/NuGet` +
   `$XDG_CACHE_HOME/NuGet`; Swift `~/.swiftpm` (shared) + `~/.cache/org.swift.swiftpm` /
   `$XDG_CACHE_HOME/org.swift.swiftpm` (Linux, no `~/Library`); Node `~/.npm` + `~/.cache`; Python
   `~/.cache/{pip,uv}` + `~/.local`; Go `~/go/pkg/mod` + `~/.cache/go-build`; Rust `~/.cargo/registry`
   + `target/`. **Done-when requires** an in-sandbox restore+build+test to pass on **both macOS and
   Linux** for .NET and at least one non-.NET ecosystem. *Why:* Seatbelt and Landlock differ in path
   granularity, so the Linux layout is specified (not derived as "macOS minus `~/Library`") and proven
   by a real cross-platform build rather than assumed from the macOS result.

## Notes

- **Pre-config `init` validation** is intentionally left host-side (it runs before a repo is set up
  and before Swival is involved); noted here so it is not mistaken for a missed escape.
