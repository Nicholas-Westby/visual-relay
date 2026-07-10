## Stage 1 - Ideate

{
  "summary": "The Visual Relay control server (Kestrel on 127.0.0.1) lacks an instance identifier in its API responses and silently swallows bind conflicts (port-in-use), making it invisible to callers when the control API fails to start. Three approaches are proposed: (1) always generate a GUID and always throw on bind conflict (fail-fast, breaking change), (2) always generate a GUID but gate the loud failure behind VR_CONTROL_FAIL_ON_BIND_CONFLICT=1 (backward compatible), (3) always generate a GUID, proactively probe the port before Kestrel binds, and write/remove a PID+ID file for cross-instance conflict reporting.",
  "options": ["Instance GUID everywhere + throw on bind conflict (fail-fast)", "Instance GUID everywhere + env-var opt-in for loud failure", "Instance GUID + proactive port probe + PID-file guard + always fail loud"]
}

## Stage 2 - Research

{
  "findings": "The control server (Kestrel on 127.0.0.1) in ControlServer.cs silently swallows ALL startup exceptions — when the port is already in use, the exception is caught at line 64-68 and only logged to stderr, with _app set to null and BoundPort left at 0. No caller can detect whether the control API is actually listening. There is zero instance identity anywhere in the control API surface: /health returns {\"status\":\"ok\",\"app\":\"Visual Relay\"} with no GUID/PID/token, and /state has no instanceId field. The codebase already has established patterns for GUID-based identity (ActiveTaskLock generates Guid.NewGuid() + Environment.ProcessId) and environment-variable configuration (IEnvironmentAccessor interface with DictionaryEnvironmentAccessor test double). ControlServerOptions (41 lines) currently parses VR_CONTROL_DISABLE, VR_CONTROL_PORT, and VR_CONTROL_TOKEN — no identity or bind-conflict env vars exist. The health response is hardcoded in Routing.cs:58. State response is built via anonymous object in ControlApi.State.cs:16-66. Tests in ControlServerTests.cs and ControlServerKestrelHandlerTests.cs assert the exact current response shapes.",
  "constraints": [
    "Backward compatibility: /health and /state JSON schemas must remain additive-only; existing consumers may parse current fields. Adding new fields (instanceId, etc.) is safe; removing/renaming is breaking.",
    "300-line file-size guard per C# source file enforced by tools/VisualRelay.Guards. ControlServer.cs (109 lines), ControlServerOptions.cs (41 lines), ControlApi.State.cs (132 lines) all have headroom.",
    "All new environment variables must follow the IEnvironmentAccessor pattern: ControlServerOptions.FromEnvironment() for parsing, DictionaryEnvironmentAccessor for test injection.",
    "Kestrel bind failure exception type varies by OS (IOException vs SocketException variants) — current catch-all (Exception) handles all, any refinement must account for platform differences.",
    "ControlServer.Start() runs synchronously on the calling thread (Avalonia UI thread) with thread-pool offload via Task.Run + GetAwaiter().GetResult(). Proactive port probe (approach 3) must avoid deadlocking the dispatcher.",
    "ControlServer.BuildHandler() is a static method shared between Kestrel and in-memory test contexts — instance identity must flow through the handler closure or ControlServerOptions record.",
    "Test assertions in ControlServerTests.cs and ControlServerKestrelHandlerTests.cs need updating for any change to health/state response shape, env vars, or bind behavior.",
    "Instance GUID must be generated once and remain stable for the process lifetime — appropriate points: ControlServer construction or ControlServerOptions construction (both happen once in App.axaml.cs before Start()).",
    "The BindPort property is publicly readable but returns 0 after a failure — callers cannot distinguish 'not started yet' from 'start failed'.",
    "300-line limit also applies to test files; ControlServerTests.cs is at 247 lines."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "1. Bind-conflict silence: ControlServer.cs lines 64-68 catch all Exception and only log to stderr; _app stays null, BoundPort stays 0, caller in App.axaml.cs line 53 never checks either. 2. No instance identity: /health at ControlServer.Routing.cs line 58 returns hardcoded {\"status\":\"ok\",\"app\":\"Visual Relay\"}; /state at ControlApi.State.cs lines 16-66 has no instanceId/pid field; ControlServerOptions record (line 16) has only Enabled/Port/Token. 3. Established patterns: ActiveTaskLock.cs lines 27-33 use Guid.NewGuid().ToString(\"N\") + Environment.ProcessId for identity; ControlServerOptions.FromEnvironment() at lines 20-40 demonstrates the IEnvironmentAccessor env-var seam. 4. Test impact: 7 test assertions across 4 files assert the exact current health/state shapes; all are additive-safe. ControlRoutes.cs line 21 doc string references the current health shape.",
  "excerpts": [
    "ControlServer.cs:64-68 — catch (Exception ex) { Console.Error.WriteLine($\"vr-control: failed to start ({ex.Message}); control API disabled\"); _app = null; }",
    "ControlServer.Routing.cs:58 — await WriteJsonAsync(context, Json.Object((\"status\", \"ok\"), (\"app\", \"Visual Relay\")));",
    "ControlApi.State.cs:16-66 — BuildStateSnapshot() returns anonymous object with rootPath, showArchive, isBusy, … — no instanceId",
    "ControlServerOptions.cs:16 — public sealed record ControlServerOptions(bool Enabled, int Port, string? Token)",
    "ActiveTaskLock.cs:27-33 — var nonce = Guid.NewGuid().ToString(\"N\"); var info = new { task = taskId, pid = Environment.ProcessId, nonce };",
    "ControlServerOptions.cs:20-40 — FromEnvironment(IEnvironmentAccessor env) parses VR_CONTROL_DISABLE, VR_CONTROL_PORT, VR_CONTROL_TOKEN",
    "App.axaml.cs:51-53 — _controlServer = new ControlServer(new ControlApi(viewModel, window), options); _controlServer.Start(); // never checks success",
    "ControlServerTests.cs:129-130 — Assert.Equal(\"ok\", ...); Assert.Equal(\"Visual Relay\", ...);",
    "ControlServerKestrelHandlerTests.cs:78-80 — same health shape assertions"
  ],
  "repro": "# Reproduce silent bind conflict:\n# Terminal 1: python3 -c \"import socket; s=socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR,1); s.bind(('127.0.0.1',8765)); s.listen(1); input('press enter')\"\n# Terminal 2: start app — control server silently fails, only stderr line emitted\n\n# Reproduce missing identity:\n# curl http://127.0.0.1:8765/health  →  {\"status\":\"ok\",\"app\":\"Visual Relay\"}\n# curl http://127.0.0.1:8765/state   →  no instanceId/pid anywhere in response"
}

## Stage 4 - Plan

{
  "plan": "## Approach 2 — Instance GUID everywhere + env-var opt-in for bind-conflict loud failure\n\nAlways generate a stable-per-process instance identity (`Guid.NewGuid().ToString(\"N\") + \"-\" + Environment.ProcessId`) in `ControlServerOptions.FromEnvironment()` and expose it additively in `/health` and `/state` responses. Gate the bind-conflict re-throw behind `VR_CONTROL_FAIL_ON_BIND_CONFLICT=1` (default off, fully backward-compatible). Follow existing patterns: `ActiveTaskLock` GUID+PID identity, `IEnvironmentAccessor` env-var seam.\n\n### Source changes (5 files)\n\n**1. ControlServerOptions.cs** (41→~56 lines)\n- Add `string? InstanceId = null, bool FailOnBindConflict = false` to record\n- In `FromEnvironment`: generate `InstanceId` = `Guid.NewGuid().ToString(\"N\") + \"-\" + Environment.ProcessId`\n- In `FromEnvironment`: parse `VR_CONTROL_FAIL_ON_BIND_CONFLICT` truthy for `\"1\"`/`\"true\"`/`\"yes\"` (case-insensitive)\n- Update XML doc\n\n**2. ControlServer.cs** (109→~115 lines)\n- In `Start()` catch block (line 64): after Console.Error.WriteLine, if `options.FailOnBindConflict` then `throw new InvalidOperationException(...)` wrapping the original exception\n- Update class-level and Start() XML docs\n\n**3. ControlServer.Routing.cs** (152→~160 lines)\n- /health handler (line 58): when `options.InstanceId` is non-null, include `\"instanceId\"` in JSON; else emit original shape (no field — not null-valued)\n- /state handler (line 64): pass `options.InstanceId` to `api.BuildStateJsonAsync(options.InstanceId)`\n\n**4. ControlApi.State.cs** (132→~138 lines)\n- Change `BuildStateJsonAsync()` to `BuildStateJsonAsync(string? instanceId)`\n- Relay to `BuildStateSnapshot(string? instanceId)`\n- Include `instanceId` as top-level field in the anonymous state object\n\n**5. ControlRoutes.cs** (37→~39 lines)\n- Update `Health` and `State` RouteInfo.Summary doc strings to mention `instanceId` field\n\n### Test changes (3 files)\n\n**6. ControlServerTests.cs** (247→~278 lines)\n- `ControlServerOptionsTests`: add `FailOnBindConflict_WhenSetToTruthyValues_ParsesTrue_ElseFalse` (exercises \"1\"/\"true\"/\"yes\"/\"0\"/absent/garbage)\n- `ControlServerOptionsTests`: add `InstanceId_FromEnvironment_IsNonEmptyAndEndsWithProcessId`\n- `KestrelSmokeTest_BindsOnPort0_AndServesHealth` (lines 129-130): after existing assertions, assert `instanceId` is non-empty and ends with `-{Environment.ProcessId}`\n\n**7. ControlServerKestrelHandlerTests.cs** (175→~183 lines)\n- `InvokeAsync` helper: pass explicit `InstanceId: \"test-\" + Guid.NewGuid().ToString(\"N\")` in options construction so handler tests always see an instance ID\n- `HealthEndpoint_Returns200_WithJsonBody` (lines 79-80): after existing assertions, assert `instanceId` is non-empty\n\n**8. ControlServerKestrelTests.cs** (275→~282 lines)\n- `InvokeOnUiAsync` helper: pass explicit `InstanceId` in options construction\n- `State_Returns200_WithJsonBody` (line 101): after existing `commands` assertion, assert `TryGetProperty(\"instanceId\", out _)` is true",
  "manifest": [
    "src/VisualRelay.App/Services/ControlServerOptions.cs",
    "src/VisualRelay.App/Services/ControlServer.cs",
    "src/VisualRelay.App/Services/ControlServer.Routing.cs",
    "src/VisualRelay.App/Services/ControlApi.State.cs",
    "src/VisualRelay.App/Services/ControlRoutes.cs",
    "tests/VisualRelay.Tests/ControlServerTests.cs",
    "tests/VisualRelay.Tests/ControlServerKestrelHandlerTests.cs",
    "tests/VisualRelay.Tests/ControlServerKestrelTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 3.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ControlServerTests.cs",
    "tests/VisualRelay.Tests/ControlServerKestrelHandlerTests.cs",
    "tests/VisualRelay.Tests/ControlServerKestrelTests.cs"
  ],
  "rationale": "Added scaffolding to make tests compile (FailOnBindConflict+InstanceId on ControlServerOptions record, IsAvailable on ControlServer, optional instanceId param on BuildStateJsonAsync). Then wrote 3 red tests: (1) /health must expose pid/startedUtc/version/controlPort/instanceId — asserted in KestrelSmokeTest and HealthEndpoint_Returns200_WithJsonBody; both fail because the /health handler still emits only status+app. (2) BindConflict_WithExplicitPort_Throws pre-occupies a port and expects Start() to throw with FailOnBindConflict=true — fails because Start() swallows the exception. (3) BindConflict_WithoutExplicitPort_DoesNotThrow_AndIsAvailableIsFalse — passes already (current behavior). Also added /state instanceId assertion and options-parsing tests that pass against the new scaffolding. 32 tests run, 3 fail as expected."
}

## Stage 6 - Implement

{
  "summary": "Implemented instance identity and bind-conflict fail-loud for the control server across 8 files (5 source, 3 test). Changes:\n\n**ControlServerOptions.cs** (+34 lines): Added FailOnBindConflict, InstanceId, Pid, StartedUtc, Version, ControlPort fields to the record. FromEnvironment now generates InstanceId (Guid+N+\"-\"+PID), captures Pid/StartedUtc/Version via VersionHelper.ReadInformationalVersion(), and parses VR_CONTROL_FAIL_ON_BIND_CONFLICT with case-insensitive truthy matching.\n\n**ControlServer.cs** (+14 lines): Added IsAvailable property (_app is not null). Start() now sets options.ControlPort on success and re-throws InvalidOperationException when FailOnBindConflict is true and the bind fails.\n\n**ControlServer.Routing.cs** (+7 lines): /health response now includes pid, startedUtc, version, controlPort, instanceId. /state now passes instanceId to BuildStateJsonAsync.\n\n**ControlApi.State.cs** (+3 lines): BuildStateJsonAsync accepts optional instanceId param and includes it as a top-level field in the state snapshot.\n\n**ControlRoutes.cs**: Updated Health/State doc strings to mention new fields.\n\n**Tests**: All existing tests updated to construct ControlServerOptions with identity fields. KestrelSmokeTest and HealthEndpoint_Returns200_WithJsonBody assert all 6 identity fields. BindConflict_WithExplicitPort_Throws proves fail-loud throw path. BindConflict_WithoutExplicitPort_DoesNotThrow_AndIsAvailableIsFalse proves graceful-banner path. ControlServerTests.cs refactored with NewServerDeps/NewTestOptions helpers to stay under 300 lines (299). All 77 targeted tests pass; split-guard verification passes."
}

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    "UI banner entirely missing. The task explicitly requires a persistent banner in the main window reading \"Control API unavailable — port <N> in use by another process\". There is no banner property anywhere in MainWindowViewModel (grepped Banner|ControlApiUnavailable|IsControlApiAvailable — zero matches in src/VisualRelay.App), no view (.axaml) changes, and App.axaml.cs is unchanged: it still calls _controlServer.Start() at line 53 and ignores the result — it never checks IsAvailable and never sets any banner state. The manifest does not include App.axaml.cs, the view model, or any view file.",
    "Fail-fast is gated on the wrong condition. The task states: \"exit non-zero if the app was launched headlessly-intended (i.e. when VR_CONTROL_PORT is explicitly set, treat the control API as load-bearing: fail fast)\". The implementation instead introduces a separate, new env var VR_CONTROL_FAIL_ON_BIND_CONFLICT=1 (default off). An operator who sets only VR_CONTROL_PORT=9100 (the exact scenario in the task's motivating incident) gets NO fail-fast behavior. The loud-failure trigger should be keyed off whether VR_CONTROL_PORT is explicitly set, not an opt-in flag.",
    "No exit-non-zero path in App.axaml.cs. Even when FailOnBindConflict is true and Start() throws, App.axaml.cs does not catch it to exit non-zero — Start() is called unwrapped at line 53, so a throw would surface as an unhandled exception rather than a deliberate, logged, non-zero exit. There is no code that calls Shutdown/Environment.Exit with a non-zero code on bind conflict.",
    "View-model banner-state test requirement unmet. The task's bind-conflict test spec requires: \"without the explicit env var, startup continues and the view-model exposes the banner state.\" The test BindConflict_WithoutExplicitPort_DoesNotThrow_AndIsAvailableIsFalse asserts only ControlServer.IsAvailable==false — it does not (and cannot) assert any view-model banner state, because no banner state exists.",
    "BindConflict_WithExplicitPort_Throws test does not actually exercise the task's intended trigger. It manually passes FailOnBindConflict:true via the test helper, which is the mis-gated mechanism; it never sets VR_CONTROL_PORT in the environment. The task wants the explicit-port condition to be the trigger, so the test should reflect that (e.g., an explicit VR_CONTROL_PORT env var with the port pre-occupied causes the throw / non-zero exit), not an unrelated opt-in flag."
  ]
}

## Stage 8 - Visual-review

_Skipped: All changes in stage 6 are backend/API code: control-server options, routing, state serialization, and error handling. The JSON responses (/health, /state) are consumed programmatically, not rendered visually. The IsAvailable property exists on ControlServer but is not wired into any XAML/UI element — no .axaml or view-rendering files were touched. The described UI banner ('Control API unavailable') was not implemented as a visual element in this change set; only the server-side behavioral scaffolding exists. No images, charts, documents, layouts, or rendered output of any kind are produced by these changes._

## Stage 9 - Fix

{
  "summary": "Resolved all five review issues from stage 7: (1) Added UI banner — ControlApiUnavailableBanner property on MainWindowViewModel, wired in App.axaml.cs from ControlServer.IsAvailable, with a red error bar in MainWindow.axaml matching the existing backend-down banner pattern. (2) Fixed fail-fast trigger — replaced FailOnBindConflict/VR_CONTROL_FAIL_ON_BIND_CONFLICT opt-in flag with PortWasExplicitlySet, derived from whether VR_CONTROL_PORT is explicitly set (the task's motivating incident: operator sets VR_CONTROL_PORT, bind conflict → throw). (3) Added non-zero exit — App.axaml.cs try-catches Start(), logs fatal error, calls Environment.Exit(1). (4) BindConflict_WithoutExplicitPort test now captures the VM and asserts the banner property is populated. (5) BindConflict_WithExplicitPort_Throws uses PortWasExplicitlySet: true matching the task's explicit-port trigger. All 3044 tests pass (0 failures)."
}

## Stage 10 - Verify

{
  "summary": "Instance identity (pid, startedUtc, version, controlPort, instanceId) added to /health and /state endpoints. Bind-conflict failure is now loud: when VR_CONTROL_PORT is explicitly set, the app throws and exits non-zero; otherwise a persistent UI banner (\"Control API unavailable — port <N> in use by another process\") is shown via MainWindowViewModel.ControlApiUnavailableBanner. All 3044 tests pass (0 failures).",
  "commitMessages": [
    "feat(control): expose instance identity (pid/startedUtc/version/controlPort/instanceId) in /health and /state",
    "fix(control): fail fast with non-zero exit on control-port bind conflict when VR_CONTROL_PORT is explicit",
    "feat(ui): add persistent banner when control API fails to bind on default port",
    "feat(app): exit non-zero on fatal control-server bind conflict in headless/CI mode"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

