# Bound Sandbox Path Inspection And Settings Layout

## Problem

The Sandbox Paths settings panel added useful derived sandbox visibility, but review found two follow-up
issues:

1. `SandboxPathInspector.RunNonoGroupAsync` starts `nono profile groups <name> --json` from a UI-driven
   load path without an explicit timeout. It redirects stderr but never drains it concurrently. A slow,
   hung, or chatty `nono` process can leave Settings stuck in a loading state or deadlock on stderr.
2. A later Fix-verify pass made `SettingsWindow.Height` jump from `1270` to `2030` to satisfy a
   "fits without scrolling" test after the new Settings content made the panel taller. That is not a
   usable default window size. Settings must stay within a normal screen and use a bounded scroll
   region for long content.

## Goal

Keep the Sandbox Paths panel derived and display-only, but make its subprocess inspection bounded and
make the Settings window responsive instead of giant.

## What to build

1. Add a timeout to each `nono profile groups <name> --json` call.
   - Use a small bounded timeout suitable for opening Settings.
   - On timeout or nonzero exit, return the existing unavailable state.
   - Kill the process tree if it exceeds the timeout.
2. Drain stdout and stderr safely.
   - Do not redirect stderr without reading it.
   - Prefer `ProcessStartInfo.ArgumentList` over one interpolated `Arguments` string.
3. Keep inspection failures non-fatal to the UI.
   - `IsSandboxInfoLoading` must always return to false.
   - The unavailable panel should render instead of leaving a spinner forever.
4. Replace the `Height="2030"` workaround in `SettingsWindow.axaml`.
   - Restore a reasonable default window height.
   - Keep one clear scroll region for Settings.
   - Bound the Sandbox Paths section so long path lists scroll inside the Settings window instead of
     forcing the whole modal to grow.
   - Ensure long path text truncates, wraps, or otherwise fits without pushing the source label off
     screen.
5. Update tests to encode the intended responsive behavior.
   - Do not require all Settings content to fit without scrolling once long derived lists exist.
   - Assert the Settings window default size is reasonable.
   - Assert there is no nested/incoherent scrollbar behavior and key sections remain reachable.

## Screenshot requirement

After the fix, capture a screenshot of the Settings window showing the Sandbox Paths section. Review it
for text overlap, giant empty windows, clipped path rows, and incoherent scrollbars.

## Done criteria

- `SandboxPathInspector` cannot hang Settings indefinitely.
- Settings opens at a reasonable default size and remains usable with long sandbox path lists.
- No path row text overlaps adjacent labels or escapes its container.
- The full `./visual-relay check` gate passes.
