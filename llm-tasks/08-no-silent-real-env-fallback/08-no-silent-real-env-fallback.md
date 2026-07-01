# Require an Explicit Environment Accessor — Kill the Silent Real-Env Fallback

Visual Relay resolves its user-level config paths through one seam: `KeyEnvFile.GetEnv`. That
seam **silently falls back to the real process environment** whenever the injected accessor does
not supply a value. The consequence: a test that builds an environment accessor without `HOME`
or `XDG_CONFIG_HOME` does not fail — it quietly reads and, worse, **writes the developer's real
`~/.config/visual-relay/.env`** (the file that holds provider API keys and Obsidian settings),
plus the sibling `ui-state.json` and diagnostics keys that resolve through the same seam.

This task removes that fallback. After it: an environment source must be **explicitly provided**
to every config-resolving call, enforced by the compiler; and if a required path variable is
absent, resolution **throws** rather than touching any real file. There is no code path left that
can silently resolve the real environment. Do all four parts in one change.

## Background: the leak that motivated this

The developer's real `~/.config/visual-relay/.env` was found containing:

```
VR_OBSIDIAN_VAULT_ROOT=/Users/dev/obsidian-vault
VR_OBSIDIAN_POLL_SECONDS=60
VR_OBSIDIAN_ENABLED=false
```

The string `/Users/dev/obsidian-vault` is a **hardcoded test fixture** — it is set in
`RevealVaultRootCommandTests`:

```csharp
var env = new DictionaryEnvironmentAccessor();
var viewModel = new MainWindowViewModel(environmentAccessor: env)
{
    ObsidianVaultRoot = "/Users/dev/obsidian-vault"
};
```

So a unit test wrote to the real config file. The mechanism, end to end:

1. `DictionaryEnvironmentAccessor` (in `tests/VisualRelay.Tests/TestDoubles.cs`) is an in-memory
   dictionary; `GetEnvironmentVariable` returns `null` for any key it was not given. The test
   above never sets `HOME` or `XDG_CONFIG_HOME`, so the accessor returns `null` for both.
2. Assigning `ObsidianVaultRoot` fires the view-model's change handler, which calls
   `ObsidianBridgeSettings.Save(...)` → `KeyEnvFile.Upsert(...)`.
3. `Upsert` resolves the target path via `KeyEnvFile.GetEnv("HOME", accessor)`. Its body is:

   ```csharp
   public static string? GetEnv(string name, IEnvironmentAccessor? accessor = null) =>
       accessor?.GetEnvironmentVariable(name)
       ?? Environment.GetEnvironmentVariable(name);
   ```

   The accessor returns `null` for `HOME`, so `?? Environment.GetEnvironmentVariable(name)` kicks
   in and returns the **developer's real `HOME`**. The write lands in the real
   `~/.config/visual-relay/.env`.

This particular write goes through `Upsert`, which preserves other lines, so it "only" pollutes
the file with Obsidian keys rather than deleting provider keys. That is luck, not safety: the same
seam is used to read keys (a test could read stale real keys and make a wrong assertion) and any
future test shaped slightly differently could clobber real settings. The point of this task is to
**remove the capability**, so no present or future test can reach the real environment by accident.

Fixing the one test is explicitly **not** enough: `new DictionaryEnvironmentAccessor()` with no
`HOME` compiles cleanly and silently resolves real paths. The class of bug survives until the
silent fallback is gone.

Note on the sandbox: when the suite runs inside VR's own `nono`/`vr-guard` sandbox, writes to
`~/.config` are denied, so this leak is masked there. It manifests when the suite runs **outside**
the sandbox (a plain `dotnet test` / `./test.sh` on the host), which is the normal developer loop.
Do not "solve" this by assuming the sandbox is always present — fix the seam.

---

## Part A — Remove the implicit real-env fallback and make the accessor required

**Symptom.** `KeyEnvFile.GetEnv` silently substitutes the real process environment when the
accessor lacks a key (the `?? Environment.GetEnvironmentVariable(name)` shown above). Every config
resolver in `VisualRelay.Core.Configuration` reads through this seam, so all of them inherit the
silent fallback.

**Fix — make the environment explicit and mandatory:**

1. **Drop the fallback.** `GetEnv` must read **only** from the accessor:

   ```csharp
   public static string? GetEnv(string name, IEnvironmentAccessor accessor) =>
       accessor.GetEnvironmentVariable(name);
   ```

   No `Environment.GetEnvironmentVariable` call remains in `KeyEnvFile`.

2. **Make the accessor a required, non-nullable parameter** everywhere it is currently
   `IEnvironmentAccessor? accessor = null`. Remove the `= null` defaults so omission is a
   **compile error** — the compiler then enumerates every call site for you. This applies to the
   public surface of:
   - `KeyEnvFile` — `GetEnv`, `Read`, `Upsert`, `GetUnsetKeysPublic`, and the private `ResolvePath`.
   - `XdgConfig.ResolveConfigDir(IEnvironmentAccessor accessor)` (the accessor overload).
   - `ObsidianBridgeSettings.Load` / `Save`.
   - `UiStateStore.Load` / `Save`.
   - `DiagnosticsSettings.LoadVerboseDiagnostics` / `SaveVerboseDiagnostics`.

3. **Delete the no-argument `ResolvePathForCurrentUser()` overload.** Today it is
   `public static string ResolvePathForCurrentUser() => ResolvePathForCurrentUser(null);` — a
   convenience that resolves the real environment. Remove it; callers must pass an accessor.

4. **Throw instead of guessing a path — this already works once the fallback is gone.**
   `XdgConfig.ResolveConfigDir(string?, string?, bool, string?)` already ends with:

   ```csharp
   throw new InvalidOperationException(
       "Cannot resolve config directory: neither XDG_CONFIG_HOME nor HOME is set.");
   ```

   It never fires today only because `GetEnv` hands it the real `HOME`. After step 1, an accessor
   that lacks both `XDG_CONFIG_HOME` and `HOME` reaches this throw — which is exactly the desired
   loud failure. Do **not** add any new default path. Keep the existing Windows `%APPDATA%` branch
   in `ResolveConfigDir` as-is: on Windows the launcher intentionally leaves `XDG`/`HOME` unset and
   the OS supplies `ApplicationData`; that is a provided value, not a silent env fallback, and is
   out of scope.

**Preserve legitimate file-tier precedence.** `DiagnosticsSettings` and `ObsidianBridgeSettings`
use the pattern `GetEnv(key, accessor) ?? KeyEnvFile.Read(accessor).GetValueOrDefault(key)` — that
is "process env (via accessor) wins over the `.env` file," which is correct and must stay. Only the
**real-process-env** fallback inside `GetEnv` is being removed, not the file tier.

---

## Part B — Provide the real environment explicitly in production

**Symptom.** Production relies on the silent fallback: `App.axaml.cs` builds the main view model
with no accessor —

```csharp
var viewModel = new MainWindowViewModel();
```

— and the backend reads the real `.env` path via the no-arg overload in
`BackendLifecycle` (`src/VisualRelay.Core/Execution/BackendLifecycle.Start.cs`):

```csharp
var userEnv = KeyEnvFile.ResolvePathForCurrentUser();
```

Once Part A lands, both stop compiling. Production must now say, explicitly, "use the real
environment."

**Fix:**

1. **Move `ProcessEnvironmentAccessor` into Core.** It currently lives at
   `src/VisualRelay.App/Services/ProcessEnvironmentAccessor.cs` in namespace
   `VisualRelay.App.Services`:

   ```csharp
   public sealed class ProcessEnvironmentAccessor : IEnvironmentAccessor
   {
       public string? GetEnvironmentVariable(string name) =>
           Environment.GetEnvironmentVariable(name);
   }
   ```

   The backend lifecycle lives in `VisualRelay.Core`, which cannot reference `VisualRelay.App`, so
   this type must move to `VisualRelay.Core` (place it in
   `src/VisualRelay.Core/Configuration/ProcessEnvironmentAccessor.cs`, namespace
   `VisualRelay.Core.Configuration`). Delete the `VisualRelay.App` copy and update all references
   (the compiler will surface them).

2. **Make the view model require an accessor.** Change the public constructor so
   `IEnvironmentAccessor` is a required parameter (no `= null` default), and make the
   `EnvironmentAccessor` member non-nullable and get-only (assigned once from the constructor;
   drop the nullable `?` and the `init` setter). This removes every downstream
   `EnvironmentAccessor?` null-forgiveness and guarantees the view model always has an explicit
   source. Note this also breaks the object-initializer style used in some tests
   (`new MainWindowViewModel { EnvironmentAccessor = env }`); move those to the constructor
   argument (`new MainWindowViewModel(env)`) — the compiler will surface every site.

3. **Wire the real accessor at the composition roots:**
   - `App.axaml.cs` → `new MainWindowViewModel(new ProcessEnvironmentAccessor())`.
   - The `BackendLifecycle` caller → pass `new ProcessEnvironmentAccessor()` (or thread one in
     from the caller) into `ResolvePathForCurrentUser(...)`.
   - Any other production construction the compiler surfaces.

This is the reframing of the principle: reading the real environment is now an **explicit,
provided** accessor at the edges of the app — never an implicit fallback buried in a shared helper.

---

## Part C — Make every test provide an isolated environment

**Symptom.** After Part A, tests that pass a bare `new DictionaryEnvironmentAccessor()` (no `HOME`
/ `XDG_CONFIG_HOME`) and then hit a config resolver will now **throw** — which is correct, but the
tests must be updated to be hermetic rather than to re-enable a fallback. At minimum
`RevealVaultRootCommandTests` leaks today; audit for every sibling the compiler and the new throw
surface (e.g. other view-model tests that construct an empty accessor).

**Fix:**

1. **Add one shared helper for an isolated accessor** so the fix is DRY and future tests fall into
   the pit of success. In the test harness (e.g. alongside `DictionaryEnvironmentAccessor` in
   `tests/VisualRelay.Tests/TestDoubles.cs`), add a factory that returns a
   `DictionaryEnvironmentAccessor` pre-seeded with `HOME` (and/or `XDG_CONFIG_HOME`) pointing at a
   unique directory under `Path.GetTempPath()`. Every test that needs "an environment that resolves
   to a throwaway config dir" uses this instead of `new DictionaryEnvironmentAccessor()`.

2. **Route the leaky/empty-accessor tests through it.** Replace bare
   `new DictionaryEnvironmentAccessor()` at config-touching construction sites with the isolated
   helper. Tests that deliberately exercise a specific key still start from the isolated accessor
   and then set that key, so they never resolve a real path.

3. **Do not weaken assertions or add `HOME` back as a real value.** The temp dir must be a
   throwaway, not the developer's home. Keep each test's intent intact.

4. **Prove hermeticity.** A test run (outside the sandbox) must leave the real
   `~/.config/visual-relay/.env` byte-for-byte unchanged. If practical, add a focused test that
   asserts a config write with an isolated accessor lands under the temp dir and that
   `KeyEnvFile`/`XdgConfig` **throw** when handed an accessor with neither `HOME` nor
   `XDG_CONFIG_HOME` — locking in the new contract.

---

## Part D — Guard against regressions

**Symptom.** Nothing stops someone from reintroducing a `?? Environment.GetEnvironmentVariable(...)`
fallback inside the config seam, which would silently restore the leak.

**Fix.** Add a convention guard modeled on the existing
`SplitGuardVerificationTests.NoTestFile_CallsEnvironmentSetEnvironmentVariable` rule (in
`tests/VisualRelay.Tests/SplitGuardVerificationTests.*.cs`; the injection-seam partial
`SplitGuardVerificationTests.InjectionSeams.cs` is the natural home):

- Scan the source files under `src/VisualRelay.Core/Configuration/`.
- Assert that **`Environment.GetEnvironmentVariable` / `Environment.GetEnvironmentVariables` appear
  in no file except `ProcessEnvironmentAccessor.cs`** — the one sanctioned place that reads the
  real environment.
- The failure message must list each offending `file` so a regression is obvious.

Rationale to include in the test: the config seam must reach the process environment only through
an injected `IEnvironmentAccessor`; the sole concrete real-env implementation is
`ProcessEnvironmentAccessor`, so any other direct `Environment.GetEnvironmentVariable` call in
`Configuration` is a silent-fallback regression. (This guard targets `GetEnvironmentVariable`
specifically; `XdgConfig`'s Windows `Environment.GetFolderPath(ApplicationData)` call is a
different API and is intentionally allowed.)

The guard must **fail on the pre-fix tree** (the current `KeyEnvFile.GetEnv` fallback trips it) and
**pass after** the fix.

---

## Global constraints

- **One change, gate green.** The full `Verify` gate must end `Failed: 0`, no crash, no
  blame-hang, exit 0 — including the new guard and every call site the required-accessor change
  surfaces.
- **Never weaken the gate to pass it.** No deleting or `Skip`-ing tests, no loosened assertions, no
  re-adding a real-env fallback "just for tests." VR's own reflection guards forbid this.
- **No new default path and no new silent fallback.** Absent a resolvable `XDG_CONFIG_HOME`/`HOME`
  from the provided accessor, resolution throws (except the pre-existing, intentional Windows
  `%APPDATA%` branch, which stays).
- **Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines** (VR's file-size guard enforces this).
- **Production must remain fully functional**: launching the app and starting the backend still
  resolve the real `~/.config/visual-relay/.env` — now via an explicitly provided
  `ProcessEnvironmentAccessor`, not a fallback.

## Files likely in scope (the plan stage will finalize the manifest)

- `src/VisualRelay.Core/Configuration/KeyEnvFile.cs` — remove the `?? Environment.GetEnvironmentVariable` fallback; required accessor; delete the no-arg `ResolvePathForCurrentUser()`.
- `src/VisualRelay.Core/Configuration/XdgConfig.cs` — required accessor on the accessor overload (the throw path already exists).
- `src/VisualRelay.Core/Configuration/ProcessEnvironmentAccessor.cs` — **new location** (moved from App).
- `src/VisualRelay.App/Services/ProcessEnvironmentAccessor.cs` — **remove** (moved to Core).
- `src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs` — required accessor on `Load`/`Save`.
- `src/VisualRelay.Core/Configuration/UiStateStore.cs` — required accessor on `Load`/`Save`.
- `src/VisualRelay.Core/Configuration/DiagnosticsSettings.cs` — required accessor.
- `src/VisualRelay.Core/Execution/BackendLifecycle.Start.cs` — pass `ProcessEnvironmentAccessor` to `ResolvePathForCurrentUser`.
- `src/VisualRelay.App/App.axaml.cs` — `new MainWindowViewModel(new ProcessEnvironmentAccessor())`.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` — required ctor accessor; non-nullable `EnvironmentAccessor`.
- `tests/VisualRelay.Tests/TestDoubles.cs` — shared isolated-accessor factory.
- `tests/VisualRelay.Tests/RevealVaultRootCommandTests.cs` — use the isolated accessor.
- additional test files surfaced by the required-accessor change and the new throw (audit empty-accessor / no-accessor view-model constructions).
- `tests/VisualRelay.Tests/SplitGuardVerificationTests.InjectionSeams.cs` — new regression guard.
