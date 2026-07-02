# Report Windows process-launch failures distinctly, not as model-backend errors

## Symptom
On Windows, when the sandbox cannot LAUNCH the agent process, swival exits with a Windows
NTSTATUS launch code (e.g. -1073741502 = 0xC0000142 STATUS_DLL_INIT_FAILED) and zero
output. Visual Relay then flags the task with: "model call failed — swival produced no
diagnostic output and the model backend rejected or failed the request. Check the litellm
proxy log." That is a MISDIAGNOSIS: the model was never called (the process never
started), the proxy log holds no such cause, and the operator is sent to the wrong place.

## Root cause
`SwivalSubagentRunner.BuildNonzeroExitReason` (`ProcessRunners.Diagnostics.cs`), when
swival yields no usable diagnostic, presumes a model-backend error and consults/blames the
proxy. It never considers that the process failed to START.

## Fix (general observability improvement)
Recognize the Windows process-launch NTSTATUS codes and report them accurately:
- Add `IsProcessLaunchFailureExitCode(exitCode)` matching 0xC0000142 (DLL init failed),
  0xC0000135 (DLL not found), 0xC000007B (bad image). Harmless cross-platform — these exact
  negative values are not normal Unix exit codes.
- In the no-usable-diagnostic branch of `BuildNonzeroExitReason`, when the exit code is a
  launch failure, lead with a process-launch / DLL-initialization failure message (with the
  hex code) that states no model call was made and suggests `VR_WINDOWS_SANDBOX=builtin`,
  instead of the model-backend text.

## Done when
- A nonzero exit of 0xC0000142 with no swival output yields a reason that names a
  launch / DLL-init failure and the builtin escape hatch, and NOT "model backend rejected."
- Existing proxy-log / diagnostic tests stay green; a new test covers the launch-failure case.
- Every touched `*.cs` stays <= 300 lines. Full gate green (`./visual-relay check`).

## Provenance
Found while driving VR (via the control API) to build a real Unity C# project on Windows
Server 2025 with no MXC BaseContainer: the DACL fallback broke swival's process init
(0xC0000142) and VR blamed the model backend, obscuring the real sandbox-launch cause and
costing debugging time. Companion to task 16 (which fixed the DACL-fallback denied-paths abort).

## Files in scope
- src/VisualRelay.Core/Execution/ProcessRunners.Diagnostics.cs
- tests/VisualRelay.Tests/ProxyLogFailureReasonTests.cs
