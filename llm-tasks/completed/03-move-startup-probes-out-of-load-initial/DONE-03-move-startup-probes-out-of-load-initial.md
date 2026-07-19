# Task: Move the sandbox/nono inspection and backend probe out of LoadInitialAsync

Every `MainWindowViewModel.LoadInitialAsync()` fire-and-forgets a sandbox path
inspection that spawns multiple real `nono` subprocesses and issues a real
localhost HTTP readiness probe. Roughly 198 test call sites load the VM, so a
full suite launches on the order of 1,000-3,000 unawaited background
subprocesses that outlive their tests — process churn that inflates queuing
for the entire aggressively-parallel suite. Move both behind the existing
"app-startup-only" pattern the class already uses for backend monitoring.

### Evidence (2026-07-19 slow-test investigation)

- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs:235` —
  `_ = LoadSandboxPathsAsync(); // fire async — nono calls are subprocesses`
  inside `LoadInitialAsync`. The loader
  (`MainWindowViewModel.Sandbox.cs:52-105`) calls
  `SandboxPathInspector.InspectAsync`, which resolves the `nono` binary on
  PATH and makes per-group subprocess expansion calls (~6-16 spawns per
  invocation). `nono` is pinned in the nix devshell, so in the test
  environment these spawns all succeed (slower than failing).
- `MainWindowViewModel.cs:242-249` — `LoadInitialAsync` awaits
  `RefreshAsync()` or `RefreshBackendStatusAsync()`, and
  `MainWindowViewModel.cs:259-264` shows the latter calling
  `BackendReadinessProbe.CheckAsync()` — a real HTTP GET to the backend
  `/health/readiness` endpoint with a 2s timeout
  (`BackendReadinessProbe.cs:24,35`). Connection-refused resolves in ~ms, but
  anything actually listening on the port costs up to 2s per probe, and every
  probe is real socket work.
- The repo already solved this exact problem once:
  `MainWindowViewModel.cs:266-269` — `StartBackendMonitoring` is "Called ONLY
  from App startup (never the ctor or LoadInitialAsync) so unit tests spin no
  timer." That is the pattern to extend.
- Scale: ~198 test call sites construct/load the VM (2026-07-19 count);
  affected suites (`MainWindowViewModelTests`, `…InitTests`,
  `…SettingsTests`, `LiveStateViewModelTests`, control-server tests) all
  reported 5-18s in the user's host run while measuring 0.1-0.5s solo — the
  gap is suite-wide contention these spawns feed.

### What to build

1. Add `StartBackgroundInspections()` to `MainWindowViewModel` (same partial
   as `StartBackendMonitoring`), containing exactly the two moves:
   - the `_ = LoadSandboxPathsAsync();` fire-and-forget, moved out of
     `LoadInitialAsync`;
   - the initial backend probe: `LoadInitialAsync` keeps its
     `RefreshAsync()` call (it loads the queue) but no longer falls back to
     `RefreshBackendStatusAsync()` when there is no root — that initial
     no-root probe moves into `StartBackgroundInspections()`.
2. Call `StartBackgroundInspections()` from the one real composition root
   that already calls `StartBackendMonitoring` (App startup in
   `App.axaml.cs`), immediately before or after that call.
3. If any UI affordance depends on sandbox info appearing after a settings
   open rather than app start (check the settings panel bindings for
   `IsSandboxInfoAvailable`/`IsSandboxInfoLoading`), also invoke the loader
   from that panel's open command — on-demand is fine; per-`LoadInitialAsync`
   is what must die.
4. Do NOT add a probe interface, environment flag, or per-test fakes — the
   structural move is the whole fix, exactly like `StartBackendMonitoring`.

Note: `RefreshAsync` itself still probes the backend when a root exists
(`MainWindowViewModel.cs:238-241`); that is intentional, observable behavior
and stays. This task removes only the load-time fire-and-forget spawns and
the no-root probe from the test-hot path.

### Constraints

- Production UX unchanged: on app launch the sandbox panel still populates
  and the backend dot still turns green, via the new startup call.
- Coverage is non-negotiable: no test deleted, skipped, or weakened. Any
  existing test that asserted sandbox state after `LoadInitialAsync` must be
  re-pointed at `StartBackgroundInspections()`/the panel command with a
  name-by-name mapping in the run summary.
- Keep files under the 300-line guard.

### Tests (red first)

- `LoadInitialAsync` on a VM with no root: `IsSandboxInfoLoading` and
  `IsSandboxInfoAvailable` remain false and `BackendStatusMessage` stays
  null — i.e. no inspection and no probe ran.
- `StartBackgroundInspections()` sets `IsSandboxInfoLoading = true`
  (observable synchronously before the first await completes, matching the
  loader's first line) and triggers exactly one backend status refresh.
- Existing sandbox-panel population tests keep passing via the new entry
  point (mapping documented).

### Verification

- `./visual-relay check` fully green.
- Sanity: `./visual-relay launch` shows the sandbox paths panel populated and
  the backend indicator behaving as today.

### Commit-message evidence

Measure before and after while implementing (full-suite wall time is the
right scope for a suite-wide contention fix), then put one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Never pre-fill that bullet — numbers are
measured at implementation time and go into the eventual commit message,
nowhere else.
