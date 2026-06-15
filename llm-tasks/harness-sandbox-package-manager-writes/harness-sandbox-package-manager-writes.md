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

Concrete groups to cover (recursive dir grants unless noted):

- **.NET** — `$HOME/.nuget/packages`, the NuGet http-cache (`$HOME/.local/share/NuGet`; also honor
  `$NUGET_PACKAGES` if the env-redirect route is taken — see B), `$HOME/.dotnet`,
  `$HOME/.templateengine`, the `DOTNET_CLI_HOME` root, plus `$TMPDIR` (macOS `/var/folders`) / `/tmp`.
  Workspace `obj/` and `bin/` are already covered by the `readwrite` workdir grant.
- **Swift / SwiftPM** — `$HOME/.swiftpm`, `{ "$HOME/Library/Caches/org.swift.swiftpm", "when": "macos" }`,
  `{ "$HOME/Library/org.swift.swiftpm", "when": "macos" }`, the module/derived-data cache
  (`{ "$HOME/Library/Developer", "when": "macos" }` or the `CLANG_MODULE_CACHE_PATH` root),
  `DEVELOPER_DIR` as **read-only**, `$TMPDIR`; workspace `.build/` covered by workdir.
- **Node / npm / pnpm / yarn / Bun / Deno** — `$HOME/.npm`, `$HOME/.cache` (npm/yarn/deno; note this
  is `$XDG_CACHE_HOME`, already largely covered by `user_caches_*` — confirm), `$HOME/.bun`,
  `$HOME/.deno`, `$HOME/.pnpm-store`, `$HOME/.yarn`, `$HOME/.config` *selectively* (some tools write
  here — grant the specific subdir, NOT all of `~/.config`, to avoid re-opening denied trees), the
  `COREPACK_HOME` root, `$TMPDIR`; workspace `node_modules/` covered by workdir.
- **Python / pip / venv / uv / pyenv** — `$HOME/.cache/pip`, `{ "$HOME/Library/Caches/pip", "when": "macos" }`,
  `$HOME/.local`, `$HOME/.pyenv`, `$HOME/.cache/uv`, `$TMPDIR`; workspace `.venv/` covered by workdir.
  (`python_runtime` covers pyenv/uv already — confirm and keep the explicit entries as belt-and-braces.)
- **Go / Rust (gaps the base omits)** — Go: `$HOME/go/pkg/mod` (+ `GOCACHE`, default
  `$HOME/Library/Caches/go-build` on macOS / `$XDG_CACHE_HOME/go-build` on Linux); Rust:
  `$HOME/.cargo/registry`, `$HOME/.cargo/git`, workspace `target/` (workdir). Or include the
  `go_runtime` / `rust_runtime` groups via `groups.include` instead of listing paths — **prefer the
  group** when it exists and matches, since it is maintained upstream.
- **General** — toolchain install roots are already readable via `read:["/"]`; if Nix is used,
  `/nix/store` is read-only and already readable. `$HOME/.gitconfig` read (covered by `git_config`).
  The system trust store and read-only system paths come from `system_read_*`.

**Stays DENIED** (deny-by-default already enforces these; call them out so a reviewer can confirm
the profile did not accidentally open them, and so the test in §Tests can assert them):
`$HOME/Documents`, `$HOME/Desktop`, `$HOME/Pictures`, `$HOME/Movies`, `$HOME/Music`, the rest of
`$HOME/Library` (outside the specific toolchain subdirs granted above), other users' homes,
`/System`, `/Applications`, `/etc` (writes), `/usr` (writes), and the credential/keychain/browser/
shell-history/shell-config trees held by the `deny_*` groups.

**Make the set configurable/extensible.** Two acceptable mechanisms — pick one and state which in
the PR:
1. **Profile groups + a documented "extra grants" block in `vr-guard.json`.** The profile is the
   knob: operators (or VR's own per-repo init) can add `filesystem.allow` entries / `groups.include`
   for an ecosystem the default set misses. Keep `vr-guard.json` the shipped baseline.
2. **A config field** (e.g. `RelayConfig.SandboxExtraAllowPaths: IReadOnlyList<string>?`) that, when
   the sandbox is enabled, appends `-a <path>` flags to BOTH the Swival `nono run` invocation
   (`BuildLaunchTarget`) and the verification `nono run` invocation (B). This keeps per-repo grants
   in `.relay/config.json` next to `testCmd`. If you add it: default `null` (no extra grants),
   merge it in `RelayConfigLoader` like the other optional lists, expand `$HOME`/`~` in C#, and add
   the env var to `RelayConfig.cs` doc-comment. (The profile route is simpler and is the
   recommended default; the config field is the escape hatch for repos with exotic cache dirs.)

Either way, keep the **env-redirect** option (B) in mind: redirecting a toolchain's cache into the
already-granted workspace or `~/.config/swival` (via `NUGET_PACKAGES`, `GOCACHE`, `CARGO_HOME`,
`npm_config_cache`, `UV_CACHE_DIR`, …) is an equally valid way to satisfy a write without widening
the profile, and it keeps caches scoped per-run. Prefer the **narrowest** approach that lets
`restore`/`build`/`test` pass: profile grant for shared immutable caches (NuGet packages), env-
redirect for anything that should not leak across repos.

### B. Route verification through nono (the core change)

Verification must run under the **same** `nono run -p vr-guard ...` wrapper as Swival, so the
extended allowlist applies to `restore`/`build`/`test`/guard/bootstrap/new-guard. Design:

- Introduce a **sandbox-aware `ITestRunner`** — e.g. `SandboxedTestRunner` — that wraps an inner
  runner and, when the sandbox is enabled, transforms the command into a nono-wrapped invocation
  before exec, applying the same `BuildSandboxEnvironment` redirects. Reuse the existing nono prefix
  (factor `BuildLaunchTarget`'s `run -p vr-guard --allow-cwd --rollback --no-rollback-prompt --`
  builder into a shared helper so the Swival path and the verification path can't drift). For a
  shell command (`ShellTestRunner` semantics) the wrapped form is
  `nono run -p vr-guard --allow-cwd --rollback --no-rollback-prompt -- /bin/sh -lc "<cmd>"`; for a
  direct exec (`DirectExecTestRunner`, single script path) it is
  `nono run ... -- <script> <args>`. The runner takes `RelayConfig` (for `BypassSandbox` and any
  `SandboxExtraAllowPaths`) so it decides per-run.
- **Wire it in production** at every site that currently constructs `ShellTestRunner` /
  `DirectExecTestRunner` for execution: `MainWindowViewModel.Execution.cs`,
  `tools/VisualRelay.RunTask/Program.cs`, `tools/VisualRelay.DrainQueue/Program.cs`. When
  `BypassSandbox == true`, the wrapper is a pass-through to the inner host runner (so the bypass
  checkbox still disables sandboxing for verification too — keeping ONE escape).
- **Rollback consideration.** Swival's run uses `--rollback`; a verification run that only restores
  packages and builds generally should not need rollback snapshots, and snapshotting every test run
  could be slow. Decide deliberately: either drop `--rollback`/`--no-rollback-prompt` for the
  verification wrapper (verification shouldn't be destroying workspace files — and if it does, that's
  a bug to surface, not silently roll back), or keep it for symmetry. **State the choice and reasoning
  in the PR.** (Recommendation: omit rollback for verification; keep `--allow-cwd` so the workspace
  is writable for `obj/`/`.build/`/`node_modules/`.)
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
  transforms `bun test` into an exec of `nono` with args beginning
  `run -p vr-guard --allow-cwd … -- /bin/sh -lc "bun test"` (assert the exact nono prefix via the
  shared builder, the `--` separator, and that the inner command/script is unchanged). With
  `BypassSandbox == true`, it execs the inner host runner unchanged (no `nono`, no `run`). Mirror the
  existing assertions in `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs`
  (`BuildLaunchTarget_*`).
- **Shared nono-prefix builder.** A single test pins the prefix so the Swival path and verification
  path provably use the same flags (guard against drift).
- **Profile JSON is valid and well-formed.** Parse `packaging/nono/vr-guard.json`; assert it
  `extends: "swival"`, contains the new `.NET`/`Swift` allow entries, and uses `$HOME`/`when`
  expansion (not hardcoded `/Users/...`). Optionally shell out to `nono profile validate
  packaging/nono/vr-guard.json` in the integration tier.
- **New-guard containment.** A manifest entry `tools/guards/../../evil.sh` is dropped (not executed)
  by `NewGuardProbeAsync`; a legitimate `tools/guards/check.sh` is still selected. Assert via the
  candidate-selection seam (extract it if not already testable) without executing anything.
- **Config field (if mechanism #2 chosen).** `RelayConfigLoader` reads `sandboxExtraAllowPaths`
  from `.relay/config.json` and defaults it to empty/null; the runner appends `-a <expanded path>`
  for each.

### Integration (opt-in; skip when `nono` not on PATH — prove the escape is closed)

Use the `nono why` oracle for cheap, deterministic allow/deny assertions (no real build needed for
the path checks), plus at least one real in-sandbox build for .NET and Swift:

- **Allowed paths resolve r+w under vr-guard.** For each new grant
  (`~/.nuget/packages`, `~/.dotnet`, `~/.swiftpm`, `~/Library/Caches/org.swift.swiftpm`,
  `~/.cache/pip`, `~/.npm`), assert `nono why -p vr-guard --op readwrite --path <p>` reports
  **allowed** (parse for `ALLOWED` / absence of `DENIED`). These are the regression tests for §A —
  they FAIL today (all currently `DENIED`) and pass after the grants land.
- **Denied paths stay denied.** `nono why -p vr-guard --op write --path ~/Documents/x` (and
  `~/Pictures`, `~/Desktop`, a credential path like `~/.ssh`) reports **DENIED**. This proves the
  widened allowlist did not open the destructive surface.
- **Real .NET build in-sandbox.** In a scratch .NET repo, run the configured test/guard command
  through `SandboxedTestRunner` (sandbox enabled) and assert `dotnet restore` + `dotnet build` +
  `dotnet test` exit 0 — i.e. NuGet restore writes succeed under nono. (This is the real-run smoke
  the memory `pipeline-mocks-process-layer-blindspot` warns is needed — a mocked test can pass while
  the real nono-wrapped exec breaks.)
- **Real Swift build in-sandbox.** In a scratch `Package.swift` repo, `swift build` + `swift test`
  exit 0 under the sandbox (SwiftPM cache + `.build/` writes succeed).
- **Bypass still escapes.** With `bypassSandbox: true`, the same commands run on the host (no nono in
  the process tree) — assert the runner did not wrap.

## Done when

- The four verification surfaces — test command, guard (`IntegrateGuardAsync`/`RunGuardCheckAsync`),
  bootstrap smoke-check, and `NewGuardProbeAsync` — execute under `nono run -p vr-guard` when the
  sandbox is enabled (the default), at every production wiring site
  (`MainWindowViewModel.Execution.cs`, `RunTask`, `DrainQueue`). No verification command runs on the
  host while the sandbox is on, except the deliberately-bypassed path.
- `packaging/nono/vr-guard.json` grants the per-ecosystem read/write paths (≥ .NET and Swift fully;
  Node/Python/Go/Rust covered by group-or-grant), expressed with `$HOME`/`$XDG_*`/`when` expansion,
  and `nono why` confirms each new path is r+w-allowed while `~/Documents`/`~/Pictures`/`~/.ssh`
  remain denied.
- A real `dotnet restore`/`build`/`test` and a real `swift build`/`test` complete **in-sandbox**
  (scratch repos), proving package-manager writes succeed under nono.
- The `bypassSandbox` checkbox remains the **only** no-sandbox path — both Swival and verification
  honor it, and there is no other host escape.
- `NewGuardProbeAsync` rejects manifest entries that resolve outside the guards directory.
- The allowlist is extensible (documented profile grants and/or a config field) so VR can drive a
  codebase whose toolchain the default set does not cover.
- `./visual-relay check` is green; all touched files stay under 300 lines; Conventional Commit
  subjects.

## Notes / open questions for the author

- **Profile-grant vs env-redirect vs config-field.** The spec leaves the *mechanism* of
  extensibility (profile block vs `SandboxExtraAllowPaths` config field) and the *narrowness* choice
  (shared profile grant vs per-run env-redirect into the workspace) to the implementer, with
  recommendations. If the user has a preference (declarative profile that any nono user can read, vs
  config-in-`.relay/` that travels with the repo), that should be settled before building.
- **`--rollback` on the verification wrapper.** Recommended OMITTED (verification shouldn't be
  rolling back workspace state; a destructive verify command is a bug to surface). Confirm.
- **Linux/Landlock path granularity.** Seatbelt and Landlock differ; the profile's `when` predicates
  cover the macOS-only `~/Library/...` paths and Linux-only `~/.cache/...` equivalents, but the
  Swift-on-Linux and Go/Rust-on-Linux cache layouts should be validated on Linux separately (the
  `swival` base's `*_linux` groups + explicit `$XDG_CACHE_HOME` grants are the starting point).
- **Pre-config `init` validation** is intentionally left host-side (it runs before a repo is set up
  and before Swival is involved); flagged here so it is not mistaken for a missed escape.
