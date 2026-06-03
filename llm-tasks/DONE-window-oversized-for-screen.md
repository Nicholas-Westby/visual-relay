# Window opens larger than the screen, clipping the right side

On a constrained display (e.g. a VM at the largest available size) the right edge of the app
is cut off: the "Run All" button in the top bar, the RUN LOG "full" chip and count badge,
and the right edge of the RUN LOG / LLM COMMANDS text all fall off-screen, and the window's
right margin disappears so the layout looks asymmetric. The columns and margins are actually
symmetric in code (`MainWindow.axaml:29` `ColumnDefinitions="280,*,340"`, `:31`
`Margin="10,16,10,10"`) — the clipping is purely because the window is bigger than the
display.

Cause: the window is hard-sized and has a large minimum:

```xml
<!-- src/VisualRelay.App/Views/MainWindow.axaml:10 -->
Width="1440" Height="900" MinWidth="1060" MinHeight="720"
```

It opens at 1440×900 regardless of the screen, and `MinWidth=1060` means a smaller screen can
never shrink it to fit — so the right portion is permanently off-screen.

## Recommended fix

Stop hard-coding the size; fit the window to the available screen on startup. Remove the
fixed `Width`/`Height`, and in `MainWindow.axaml.cs` size and position the window to the
current screen's working area (clamp to `Screens.ScreenFromWindow(...).WorkingArea`, or start
maximized when the work area is smaller than the preferred size), centering with
`WindowStartupLocation="CenterScreen"`. Lower `MinWidth`/`MinHeight` to a value the smallest
supported display can satisfy so the three columns always remain fully visible.

## Done when

- At the smallest supported screen size, the entire top bar (including "Run All"), both right
  panels' right edges, and a symmetric right margin are fully visible — nothing is clipped.
- The window never opens wider/taller than the screen working area.
- Verify with `./visual-relay screenshot` (renders at desktop and compact widths) and a
  manual run on a small display.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
