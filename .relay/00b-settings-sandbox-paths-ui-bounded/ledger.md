## Stage 1 - Ideate

{
  "summary": "Fix two issues from prior review: (1) SandboxPathInspector.RunNonoGroupAsync hangs Settings indefinitely due to missing timeout, undrained stderr, and no process-tree kill — add a 10s timeout, concurrent stream reading, ArgumentList, and Kill(true) on failure. (2) SettingsWindow.Height=2030 is an unusable workaround — restore a reasonable height (~760), add text trimming on long sandbox paths in SandboxPaths.axaml, and update the test to assert bounded-scrolling behaviour rather than fit-all.",
  "options": [
    "Option A — Incremental bounded-subprocess fix + responsive layout: Add CancellationTokenSource timeout + concurrent stderr drain + process tree kill in RunNonoGroupAsync. Restore Height to ~760, add TextTrimming on path rows, update test to assert scrolling works (extent > viewport) instead of no-scroll. Single outer scroll region preserved.",
    "Option B — Centralised timeout via ProcessRunner helper: Extract a reusable ProcessRunner class for timeout + stream drain + ArgumentList + tree kill. Same layout fix as Option A. More code but reusable pattern for future subprocess calls.",
    "Option C — Minimal-touch timeout only, reserve layout for later: Add timeout + stderr drain to RunNonoGroupAsync (keep Arguments as-is). Change Height to 760, add MaxHeight on SandboxPaths section (nested scroll), add text trimming. Faster but introduces a nested scroll region, partially conflicting with the 'no incoherent scrollbar' done criterion."
  ]
}

## Stage 2 - Research

{
  "findings": "The SandboxPathInspector.RunNonoGroupAsync method (lines 276-298 in src/VisualRelay.Core/Execution/SandboxPathInspector.cs) has three interlocking bugs: (a) it uses an interpolated Arguments string (`$\"profile groups {groupName} --json\"`) instead of ArgumentList, creating an injection surface and quoting ambiguity; (b) it sets RedirectStandardError=true but never reads stderr — if nono writes enough to fill the ~4KB pipe buffer (e.g., verbose diagnostics or an error trace), the child process blocks on write, can never exit, and stdout never reaches EOF, causing ReadToEndAsync to hang forever; (c) there is no timeout — the only CancellationToken is caller-supplied and LoadSandboxPathsAsync calls InspectAsync with default (none), so a hung nono leaves Settings stuck in the loading spinner state forever. RunNonoGroupAsync also never calls process.Kill(entireProcessTree:true) on any failure path. The SettingsWindow.axaml has Height=2030 (line 10), an unusable workaround forced by the test SettingsModal_AtDefaultSize_FitsWithoutScrolling_AndLiveTiersIsVisible (SettingsModalUiTests.cs:96-130) which asserts scroll.Extent.Height <= scroll.Viewport.Height. The SandboxPaths.axaml path TextBlocks (columns 1-2 of Grid with pattern '12,Auto,*,Auto') have no TextTrimming or TextWrapping, so long derived paths push the Source label off-screen. ProcessCapture.cs (src/VisualRelay.Core/Execution/ProcessCapture.cs:84-193) already implements the exact correct pattern: ArgumentList, event-based concurrent stdout/stderr reading, a timeout via Task.WhenAny/exitedTcs, and process.Kill(entireProcessTree:true) on timeout. CommandGuardEnsurer.cs (src/VisualRelay.Core/Execution/CommandGuardEnsurer.cs:58-117) shows a simpler timeout pattern with CancellationTokenSource.CancelAfter and Kill(true) for non-heavy subprocess calls.",
  "constraints": [
    "Must use ProcessStartInfo.ArgumentList over interpolated Arguments string in RunNonoGroupAsync",
    "Must drain stderr concurrently with stdout (event handlers or concurrent ReadToEndAsync tasks) — never redirect stderr without reading it",
    "Must add a bounded timeout (10 seconds per the stage-1 ideation) opening Settings; the timeout must be small enough that the user never perceives a frozen Settings dialog",
    "Must call process.Kill(entireProcessTree: true) on timeout or non-zero exit before returning null / unavailable state",
    "Must ensure IsSandboxInfoLoading always returns to false after the nono call (LoadSandboxPathsAsync already has try/catch/finally that sets it false — must preserve that contract)",
    "Must restore SettingsWindow.Height from 2030 to a reasonable default (~760, matching Option A from stage 1) so the window fits on normal screens",
    "Must add TextTrimming (CharacterEllipsis) on the Raw-path TextBlock in SandboxPaths.axaml Grid column 1 so long paths truncate and never push the Source label (column 3) off-screen",
    "Must keep exactly ONE layout scroll region in the Settings dialog — SettingsScrollViewer in SettingsPanel.axaml — no nested scrollbars (the existing LayoutScrollViewers helper in SettingsTestHelpers.cs asserts this)",
    "Must update SettingsModal_AtDefaultSize_FitsWithoutScrolling_AndLiveTiersIsVisible test: it should assert a reasonable default window height AND that the scroll region allows overflow (extent > viewport when content is long), not that everything fits without scrolling",
    "Must keep the assertion that 'Live Tiers' remains reachable/visible in the layout",
    "Must keep the existing single-scroll-region assertion (Assert.Single(layoutScrolls)) from SettingsButtonOpensModalWindow_WithVmDataContext_SingleScrollRegion_AndBoundControl test",
    "The full `./visual-relay check` gate must pass (all tests green)",
    "RunNonoGroupAsync exception handling must continue returning null (unavailable state) on any failure — never throw to the UI layer"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Three interlocking bugs cause Settings to hang or render unusably: (1) SandboxPathInspector.RunNonoGroupAsync (lines 276-298) has no timeout, redirects stderr without reading it (pipe-buffer deadlock), uses interpolated Arguments instead of ArgumentList, and never calls Kill(true); a hung nono leaves IsSandboxInfoLoading=true forever with the spinner stuck. (2) SettingsWindow.axaml sets Height=2030 (line 10) — an unusable default forced by the test assertion that extent must be <= viewport (SettingsModalUiTests.cs lines 119-121). (3) SandboxPaths.axaml path TextBlocks (lines 54-57, 85-87, 117-119) sit in Auto-sized grid columns with no TextTrimming, so long paths push the Source label off-screen.",
  "excerpts": [
    "SandboxPathInspector.cs:276-298 — RunNonoGroupAsync: no timeout, RedirectStandardError=true with no stderr read (pipe deadlock), interpolated Arguments string, no Kill(true)",
    "SandboxPathInspector.cs:284 — Arguments = $\"profile groups {groupName} --json\" (should be ArgumentList)",
    "SandboxPathInspector.cs:286 — RedirectStandardError = true, but stderr never drained (line 292 only reads stdout)",
    "MainWindowViewModel.Sandbox.cs:47-49 — InspectAsync called with default CancellationToken, so no external timeout",
    "MainWindowViewModel.Sandbox.cs:33,76 — IsSandboxInfoLoading set true at start, only cleared in finally; a hung nono leaves it true forever",
    "SettingsWindow.axaml:10 — Height=\"2030\", unusable giant window forced by the no-scroll test assertion",
    "SettingsModalUiTests.cs:119-121 — Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5) forces Height inflation",
    "SandboxPaths.axaml:51-61 — Grid ColumnDefinitions=\"12,Auto,*,Auto\", path TextBlock in Auto column with no TextTrimming",
    "ProcessCapture.cs:84-193 — reference implementation: ArgumentList, event-based concurrent stdout/stderr reads, Task.WhenAny timeout, Kill(true)"
  ],
  "repro": "On macOS/Linux with nono on PATH: open Settings from the cog button. If nono is present, RunNonoGroupAsync fires for each included group. If nono writes enough to stderr (~4KB+), the child blocks on pipe write, ReadToEndAsync on stdout never completes, and the Settings dialog shows 'Loading sandbox paths…' indefinitely — the finally block in LoadSandboxPathsAsync never executes so IsSandboxInfoLoading stays true. Separately, at Height=2030 the Settings window is taller than most laptop screens; a long sandbox path (e.g. /private/var/folders/.../T/...) in the writable/readable/blocked list pushes the Source label off the right edge of the 520px-wide window."
}

## Stage 4 - Plan

{
  "plan": "1. SandboxPathInspector.RunNonoGroupAsync (lines 276-298): replace interpolated Arguments with ArgumentList, add concurrent stderr drain, add 10s timeout via linked CancellationTokenSource.CancelAfter + Task.WhenAny, call process.Kill(entireProcessTree:true) on timeout, return null on any failure except caller cancellation. 2. SettingsWindow.axaml line 10: Height 2030→760. 3. SandboxPaths.axaml: in all three item templates change ColumnDefinitions from '12,Auto,*,Auto' to '12,*,Auto', move Source TextBlock from Grid.Column=3 to Grid.Column=2, add TextTrimming=CharacterEllipsis on Raw TextBlock. 4. SettingsModalUiTests.cs: rename test, assert Height≤800, replace extent≤viewport assertion with scroll-viewer-exists-and-is-auto-visible, keep Live Tiers visible assertion.",
  "manifest": [
    "src/VisualRelay.Core/Execution/SandboxPathInspector.cs",
    "src/VisualRelay.App/Views/SettingsWindow.axaml",
    "src/VisualRelay.App/Views/Controls/SandboxPaths.axaml",
    "tests/VisualRelay.Tests/SettingsModalUiTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/SettingsModalUiTests.cs"],
  "rationale": "Updated the third test method (renamed to SettingsModal_AtReasonableDefaultSize_HasScrollRegion_AndLiveTiersReachable) to encode the responsive-behavior requirements from the plan:\n\n1. **Height assertion (FAILS before fix)**: `Assert.True(dialog.Height <= 800)` — currently fails because SettingsWindow.Height=2030, proving the test catches the giant-window workaround.\n2. **Scroll region assertion**: `Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility)` — ensures the scroll region is configured to auto-show, not forced-hidden.\n3. **Live Tiers reachable**: kept the existing assertion that the 'Live Tiers' TextBlock is laid out (Bounds.Height > 0).\n4. **Single scroll region**: added inline `LayoutScrollViewers` + `Assert.Single` check so this test independently verifies no nested scrollbars (was previously only in the first test).\n5. **Removed**: the `extent <= viewport` assertion that forced Height=2030 — long sandbox path lists may now overflow, which is expected responsive behavior.\n\nResult: 1 failed (new test), 2 passed (existing tests). The failure is the intended 'red' state proving Height=2030 violates the ≤800 target."
}

## Stage 6 - Implement

{ "summary": "All four manifest files updated: RunNonoGroupAsync now uses ArgumentList, drains stderr concurrently, enforces a 10s timeout with process-tree kill, and returns null on failure. SettingsWindow Height restored from 2030 to 760. SandboxPaths column layout changed from 12,Auto,*,Auto to 12,*,Auto with TextTrimming=CharacterEllipsis on raw-path TextBlocks. Test updated to assert height ≤800, Auto scrollbar visibility, single scroll region, and Live Tiers reachable. All 3 SettingsModalUiTests pass." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "All four manifest files were already correctly implemented per the plan. Verified with the targeted test command: 3/3 tests pass (0 failed). SandboxPathInspector now has timeout + stderr drain + ArgumentList + process-tree kill. SettingsWindow height is 760. SandboxPaths columns are 12,*,Auto with TextTrimming. Test encodes reasonable default height, Auto scrollbar, single scroll region, and Live Tiers reachability."}

## Stage 9 - Verify

{
  "summary": "All four manifest files updated and verified. RunNonoGroupAsync uses ArgumentList, drains stderr concurrently, enforces a 10s timeout with process-tree kill, and returns null on failure. SettingsWindow Height restored to 760. SandboxPaths column layout changed to 12,*,Auto with TextTrimming=CharacterEllipsis. Test encodes reasonable default height, Auto scrollbar, single scroll region, and Live Tiers reachability. File trimmed to 299 lines so the file-size guard passes. SettingsModalUiTests: 3/3 pass. SplitGuardVerificationTests: 16/16 pass. SandboxPathInspectorTests: 15/15 pass.",
  "commitMessages": [
    "fix: bound nono subprocess with timeout, drain stderr, kill tree on hang",
    "fix: restore SettingsWindow height to 760 and trim sandbox paths with ellipsis",
    "test: assert reasonable default size, auto-scroll region, and Live Tiers reachable"
  ]
}

## Stage 10 - Fix-verify (attempt 1/3)

{
  "summary": "Ran the exact verify command (`dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 60s --blame-hang-dump-type none`) against the working directory. Result: 2512 passed, 0 failed, 22 skipped (exit 0). The earlier harness failure was from a stale temp copy with a 330-line SandboxPathInspector.cs; the working directory has the correct 299-line version. All four manifest files are verified:\n\n- **SandboxPathInspector.cs** (299 lines ≤ 300): ArgumentList, concurrent stderr drain, 10s timeout with process.Kill(entireProcessTree:true), returns null on failure.\n- **SettingsWindow.axaml**: Height restored to 760 (reasonable default).\n- **SandboxPaths.axaml**: ColumnDefinitions=\"12,*,Auto\" with TextTrimming=\"CharacterEllipsis\" on all three path-row templates, tooltip shows expanded path.\n- **SettingsModalUiTests.cs**: Asserts Height ≤800, ScrollBarVisibility.Auto, single layout scroll region, and Live Tiers laid out.\n\nDone criteria satisfied: SandboxPathInspector cannot hang Settings (timeout + stderr drain + tree kill), Settings opens at reasonable size (760px), path text truncates with ellipsis, no overlapping text, single coherent scroll region."
}

## Stage 11 - Commit

Committed by Visual Relay.

