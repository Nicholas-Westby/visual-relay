# Existence-filter Windows MXC deniedPaths so the DACL fallback works

## Symptom
On a Windows host WITHOUT the MXC BaseContainer backend (e.g. Windows Server 2025),
every Visual Relay task is flagged at stage 1 before doing any work. `swival` exits 1
and the sandbox launcher (`wxc-exec`) reports:

    error: BaseContainer is unavailable; DACL fallback requires write-DAC permission on
    '%USERPROFILE%\.ssh', which the current user lacks
    (The system cannot find the path specified. (os error 3)).
    Using BaseContainer-fallback dispatcher (schema version 0.7.0-alpha)

Because it fails at the sandbox launch, NO task can run on such a host.

## Root cause
`wxc-exec` has two backends. When the AppContainer "BaseContainer" backend is
unavailable it falls back to DACL mutation, which stamps an ACE on every path named in
the policy — INCLUDING `filesystem.deniedPaths` — and fails if a path does not exist
(`os error 3` = path-not-found; the "lacks write-DAC permission" wording is misleading).
`MxcPolicyGenerator.WindowsCredentialDenyDirs()` emits credential dirs
(`%USERPROFILE%\.ssh`, `.aws`, …) UNCONDITIONALLY; the old comment even assumed
"denials of absent paths are harmless" — true for the BaseContainer backend, fatal for
the DACL fallback. Note the asymmetry: the readwrite `DefaultWindowsCacheDirs()` already
existence-filters (a missing path breaks container setup), but the denied paths did not.

## Fix (root cause; general, not language-specific)
Apply the same existence discipline to the emitted `deniedPaths` that the readwrite
cache dirs already use, so the policy only ever names paths that exist:

- Add `MxcPolicyGenerator.ExistingPaths(paths)` — expand env vars, keep only entries
  where `Directory.Exists || File.Exists`, distinct. Mirrors `DefaultWindowsCacheDirs`.
- Add a `Generate(workspaceRoot, cacheDirs, deniedDirs)` overload; keep the 2-arg
  overload (delegates with the full `WindowsCredentialDenyDirs()`) so `Generate` stays a
  pure, OS-agnostic serializer that tests can drive with explicit inputs.
- `MxcProvisioner.EnsurePolicy` (the real Windows launch path) passes
  `ExistingPaths(WindowsCredentialDenyDirs())`, so the policy the DACL fallback applies
  contains no missing paths.

Dropping absent credential dirs is safe: an absent dir has nothing to protect, and
Windows MXC does not natively enforce `deniedPaths` in the pinned release anyway
(`SandboxPathInspector.WindowsDeniedPathsEnforced == false`). `WindowsCredentialDenyDirs()`
stays the canonical intent set surfaced verbatim in the Settings panel.

## Done when
- On a Windows host without BaseContainer, `wxc-exec` launches VR's policy without the
  "os error 3" abort and a task proceeds past stage 1.
- `MxcRealSandboxTests` builds the policy the way production does (existence-filtered
  denials) and confirms writes stay confined to the workspace.
- New unit tests cover `ExistingPaths` (keeps existing, drops missing) and the 3-arg
  `Generate` pass-through. Existing deny-list / caveat / reads-summary tests stay green.
- Every touched `*.cs` stays ≤ 300 lines. Full gate green (`./visual-relay check`).

## Files in scope
- src/VisualRelay.Core/Execution/MxcPolicyGenerator.cs — `ExistingPaths` + `Generate` overload
- src/VisualRelay.Core/Execution/MxcProvisioner.cs — `EnsurePolicy` passes filtered denials
- tests/VisualRelay.Tests/WindowsCredentialDenyTests.cs — `ExistingPaths` + pass-through tests
- tests/VisualRelay.Tests/MxcRealSandboxTests.cs — build policy like production

## Provenance
Found while driving VR (via the control API) to build a real Unity C# project on Windows
Server 2025; the first task flagged at stage 1 on this exact sandbox error. Implemented
by hand to unblock the pipeline (the bug blocked every task, including its own fix), and
captured here so the fix is replayable through VR's own pipeline.
