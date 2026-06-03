# Queue progress bar shows phantom progress

Every queue card shows a partially filled progress bar — including tasks labeled "Pending /
No run history" that have never run. The bar is not bound to progress at all.

In `src/VisualRelay.App/Views/Controls/QueuePanel.axaml:110` the progress track contains a
fill `Border` with a hard-coded `Width="92"`:

```xml
<Border Grid.Row="4" Height="3" ... Background="#222833" CornerRadius="3">
  <Border Width="92" HorizontalAlignment="Left" Background="{Binding AccentBrush}" .../>
</Border>
```

So the fill is a fixed 92px regardless of how many stages have completed. `TaskRowViewModel`
already exposes `Task.CompletedStageCount` (0..11) and an unused `ProgressText`
(`TaskRowViewModel.cs:50`), but neither drives the bar.

## Recommended fix

Drive the fill from real progress. Add a `ProgressFraction` (`CompletedStageCount / 11.0`,
clamped 0..1) to `TaskRowViewModel` and bind the fill width to it — simplest is to replace
the nested fixed-width `Border` with a `ProgressBar` (`Minimum="0" Maximum="11"
Value="{Binding CompletedStageCount}"`) styled to the 3px track, or keep the two borders and
bind the inner width through a fraction-to-width converter on the track's actual width. A
task with no run history (`CompletedStageCount == 0`) then renders an empty bar.

## Done when

- Pending / no-run-history tasks show an empty bar; a task with N of 11 stages shows ~N/11
  fill.
- A `TaskRowViewModel` test asserts `ProgressFraction` is 0 with no history and scales with
  `CompletedStageCount`. Write the failing test first.
- `./visual-relay check` green; `./visual-relay screenshot` looks correct; files under 300
  lines; Conventional Commit.
