# Stop the "LATEST RUN FAILED" banner from overlapping the task tabs — make it scroll inside its box

When a run fails, the red **LATEST RUN FAILED** banner in the center TASK panel renders its text *on
top of* the Markdown / Context / Attachments tab strip, the **Edit** button, and the content below —
an unreadable z-fighting overlap. It's worst with a long error (e.g. the multi-line sandbox
capabilities dump from a failed run). The banner is meant to be a fixed-height, scrollable box, but its
inner `ScrollViewer` never scrolls: long content overflows the box and, because the box doesn't clip,
paints over everything beneath it.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; if a snippet has drifted, re-read the file and adapt.

**The banner.** `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`, inside the body grid
`<Grid Grid.Row="1" RowDefinitions="Auto,*">`:

```xml
<Border Grid.Row="0"
        IsVisible="{Binding HasSelectedTaskError}"
        Background="#2A1715"
        BorderBrush="#F36F63"
        BorderThickness="1"
        CornerRadius="8"
        Margin="16,12,16,4"
        Padding="12,10"
        MaxHeight="160">
  <StackPanel Spacing="6">
    <TextBlock Text="LATEST RUN FAILED" Foreground="#F36F63" FontSize="11" FontWeight="SemiBold"/>
    <ScrollViewer HorizontalScrollBarVisibility="Disabled">
      <SelectableTextBlock Text="{Binding SelectedTaskError}"
                           Foreground="#F4B5AE" FontFamily="Menlo,Consolas,monospace"
                           FontSize="12" LineHeight="18" TextWrapping="Wrap"/>
    </ScrollViewer>
  </StackPanel>
</Border>
```

The `TabControl` (Markdown / Context / Attachments + the Edit toolbar) is the **sibling in row 1** of
that same grid: `<TabControl Grid.Row="1" Margin="16,4,16,16" …>`.

**Root cause — a `ScrollViewer` inside a vertical `StackPanel` never scrolls.** A vertical `StackPanel`
measures its children with **unbounded height** (infinite space in the stacking direction), so the
`ScrollViewer` sizes to its *full* content and never activates its scrollbar. `MaxHeight="160"` clamps
the **Border's** measured height — so the `Auto` row only reserves ~160px and the `TabControl` in row 1
is positioned just below that — but the StackPanel's overflowing content still *renders* past the
Border, and because the Border has **no `ClipToBounds`**, that overflow paints over the `TabControl`
beneath it. The overlap. (It's *latent* for short errors that fit in 160px; the long capabilities dump
makes it obvious — which is why it appeared after this particular failure.)

This is **not** a z-index / background / margin problem: the box already has an opaque
`Background="#2A1715"`, a `MaxHeight`, and a correct `Auto,*` row split. Only the inner
`StackPanel`→`ScrollViewer` measurement is wrong, and clipping is missing.

**The pattern to mirror (already correct in this same file).** The Markdown read-only view bounds a
`ScrollViewer` by putting it in a **star row** of a `Grid`, where it gets a finite height and scrolls:

```xml
<Grid RowDefinitions="Auto,*">
  …toolbar in row 0…
  <ScrollViewer Grid.Row="1" Padding="8,14,8,8" HorizontalScrollBarVisibility="Disabled" …>
    <TextBlock Text="{Binding SelectedTaskMarkdown}" TextWrapping="Wrap" …/>
  </ScrollViewer>
</Grid>
```

## What to build

XAML readability/layout isn't unit-tested in this repo — verify by inspection + the manual checks
below. Keep the change minimal and self-contained.

### 1. Make the banner's content scroll inside its box
Replace the banner's inner `<StackPanel Spacing="6">` with a `<Grid RowDefinitions="Auto,*" RowSpacing="6">`:
keep the "LATEST RUN FAILED" header in the `Auto` row (row 0) and put the `ScrollViewer` (with its
`SelectableTextBlock`) in the `*` row (row 1). Now the `ScrollViewer` is height-bounded by the Border's
`MaxHeight` and **scrolls** long content instead of overflowing. (Mirror the Markdown read-only view
above.)

### 2. Clip the banner as defense-in-depth
Add `ClipToBounds="True"` to the error `Border`, so nothing it contains can ever paint outside its
bounds even if a future child mis-measures.

### Keep as-is
The body grid's `Grid.Row` layout (it's correct), the opaque `Background`, the red border,
`TextWrapping="Wrap"`, `MaxHeight="160"` (tune to ~180–220 if you like — a judgment call, not
required), and `SelectableTextBlock` (so the user can still **select/copy** the error text).

### Out of scope
Redesigning the banner, making it dismissible, or changing *what* text it shows — that's
`legible-run-failures-and-tool-preflight`.

## Tests / verification
- Manual smoke (note in PR): trigger a failing run that produces a **long** error (or temporarily bind a
  long string to `SelectedTaskError`). Confirm: the banner is a bounded, **scrollable** box; the
  Markdown / Context / Attachments tab headers and the Edit button are fully visible and **not**
  overlapped; scrolling the banner reveals the full error; switching tabs works.
- Confirm a **short** (1-line) error still renders cleanly (not clipped, no empty scroll gap).
- `./visual-relay check` green; compiled bindings clean.

## Done when
- The LATEST RUN FAILED banner never overlaps the tabs or content, regardless of error length; long
  errors scroll within the fixed-height box; the text stays selectable/copyable.
- Conventional Commit subject, e.g. `fix(ui): keep the LATEST RUN FAILED banner from overlapping task tabs`.

## Decisions (settled)
1. **Root cause is a `ScrollViewer`-in-`StackPanel` measurement bug (no scroll ⇒ overflow) plus a
   missing `ClipToBounds`** — not z-index/background/margin. *Why:* a vertical `StackPanel` measures
   children with infinite height, defeating the `ScrollViewer`; the un-clipped Border then paints its
   overflow over row 1.
2. **Fix = swap the inner `StackPanel` for a `Grid RowDefinitions="Auto,*"` + add `ClipToBounds="True"`**,
   mirroring the Markdown view's already-correct scroller. *Why:* smallest change that makes the box
   self-contained and scrollable.
3. **Stays always-visible while an error exists** (no dismiss button) — out of scope.

## Notes
- This bug is **latent for short errors**; use a deliberately long error to verify the fix.
- **Coordination:** composes with `legible-run-failures-and-tool-preflight` (which shortens/cleans the
  error text). Different files — this task: `TaskDetailPanel.axaml`; that task: Core execution. Either
  order is fine; this layout fix is correct regardless of the text length the other task produces.
