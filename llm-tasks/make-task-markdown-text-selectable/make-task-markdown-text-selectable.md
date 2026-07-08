# Make the Read-Only Markdown Text Selectable (Like the Input Pane)

In the task detail panel's **Markdown** tab, the read-only task text cannot be selected — users
can't copy a command, a path, or a paragraph out of a task. The Activity pane's **Input** tab
already behaves the way we want: its accordion body text is mouse-selectable with the standard
blue highlight and Cmd+C copies the selection. Bring the read-only Markdown view up to that
same behavior.

## Verified anchors

- **The read-only view** — `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`, inside
  the `TabItem Header="Markdown"` under the `<!-- Read-only view -->` comment:

  ```xml
  <ScrollViewer Grid.Row="1" HorizontalScrollBarVisibility="Disabled">
    <!-- Inset lives on the content (not ScrollViewer.Padding) so the bottom
         gap is part of the measured extent and the last line stays reachable. -->
    <TextBlock Text="{Binding SelectedTaskMarkdown}"
               FontFamily="Menlo,Consolas,monospace" FontSize="13"
               LineHeight="21" Foreground="#DCE2EA" TextWrapping="Wrap"
               Margin="8,14,8,16"/>
  </ScrollViewer>
  ```

  A plain `TextBlock` — that's why nothing is selectable.
- **The precedent to match** — `Views/Controls/StageInputView.axaml` renders its prompt and
  accordion bodies with `SelectableTextBlock` carrying the same kind of layout properties
  (`FontFamily`/`FontSize`/`LineHeight`/`Foreground`/`TextWrapping`) and **no extra selection
  configuration** — Avalonia's default selection brush provides the blue highlight, and
  Cmd+C copy is built in. `SelectableTextBlock` is also already used in `RunLogView.axaml`,
  `StageSystemView.axaml`, `StageOutputView.axaml`, and `QueuePanel.axaml`.
- **Theme** — `Styles/VisualRelayTheme.axaml` styles only `SelectableTextBlock.logDetail`
  (Run Log detail rows). No global `SelectableTextBlock` or selection-brush styling exists or
  is needed.

## What to build

1. In the read-only view quoted above, swap `TextBlock` → `SelectableTextBlock`, preserving
   every existing property — binding, fonts, `LineHeight="21"`, `Foreground`, wrapping, and
   especially the `Margin="8,14,8,16"` content inset (the comment above it documents the
   bottom-reachability contract: the inset must stay on the content element, not become
   `ScrollViewer.Padding`).
2. Nothing else: no view-model changes, no theme changes, no custom selection brush — default
   selection visuals, exactly like the Input pane.

## Compatibility note (check, don't preemptively rewrite)

`tests/VisualRelay.Tests/TaskDetailScrollBottomReachabilityTests.cs` locates this view via
`FindTextBlockScroller`, which pattern-matches `sv.Content is TextBlock tb` and the
`LineHeight`. `SelectableTextBlock` derives from `TextBlock`, so the match should still hold —
run that test class and confirm; only touch it if it genuinely fails, and then update the
lookup without weakening what it asserts.

## Tests

- Extend the established TaskDetail headless test pattern (see how
  `TaskDetailScrollBottomReachabilityTests` constructs the panel and walks visual
  descendants) with a regression pin: the read-only Markdown view's text element **is a
  `SelectableTextBlock`** and its `Text` binds `SelectedTaskMarkdown`.
- `TaskDetailScrollBottomReachabilityTests` stays green (bottom-inset contract unchanged).

## Done when

- With a task selected and the Markdown tab in read-only mode, dragging over the text selects
  it with the standard highlight and Cmd+C copies the fragment — same feel as selecting text
  in the Input tab (verify by hand once).
- The new regression pin and the full suite pass; `./visual-relay check` passes.

## Guardrails

- Scope is exactly the Markdown tab's read-only text element: the Context tab, edit-mode
  `TextBox`es, new-task view, and all other tabs stay untouched.
- XAML + tests only; no view-model, theme, or selection-brush changes.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diff; files stay
  under the 300-line guard.
