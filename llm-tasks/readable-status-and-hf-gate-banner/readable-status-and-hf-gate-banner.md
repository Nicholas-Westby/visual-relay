# Make status messages fully readable, and give the "set a Hugging Face token" gate a real banner

The bottom-left status line truncates with an ellipsis and has no tooltip, so messages can't be
read in full. The worst offender is the actionable one: when tasks can't run because no Hugging Face
token is set, the remediation ("Set a free Hugging Face token to run tasks — open Settings.") is
crammed into that same one-line, ellipsized `StatusText` and gets cut off — the user can see
something is wrong but not what to do. Fix the general readability, and promote the HF-token gate to
its own persistent, fully-wrapped, actionable banner instead of a transient truncated status string.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; if a snippet has drifted, re-read the file and adapt.

**The truncating footer (bottom-left, no tooltip).**
`src/VisualRelay.App/Views/Controls/QueuePanel.axaml`:

```xml
<Border Grid.Row="2" BorderBrush="#252A33" BorderThickness="0,1,0,0" Padding="16,12">
  <TextBlock Text="{Binding StatusText}" Foreground="#8FE3A2"
             MaxLines="2" TextTrimming="CharacterEllipsis"/>
</Border>
```

`MaxLines="2"` + `CharacterEllipsis` + no `ToolTip.Tip` ⇒ long messages are unreadable here.

**The second copy (top-right chip) already has a tooltip.**
`src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml` binds the same `StatusText` with
`ToolTip.Tip="{Binding StatusText}"`, `MaxLines="1"`, `MaxWidth="260"`. So a tooltip is the
established mitigation; the footer just never got one.

**The HF message and gate flag.** `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs`:

```csharp
public string HfGateMessage => IsHuggingFaceConfigured
    ? string.Empty
    : "Set a free Hugging Face token to run tasks — open Settings.";
```

with `[ObservableProperty] private bool _isHuggingFaceConfigured;` (notifies `HfGateMessage`) and a
companion `HfPricingNote` ("Free to get a token; usage is pay-as-you-go beyond HF's ~$0.10/mo free
credit (no markup)."). `IsHuggingFaceConfigured` is set by `RefreshKeyStatesAsync` from the user
`.env` + process env.

**Why it's only a fleeting status today.** The gate message reaches the UI only when a run is
attempted and blocked — `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs`,
`EnsureRunnableAsync`: `if (!IsHuggingFaceConfigured) { _pendingHfRunTaskId = pendingTaskId; StatusText = HfGateMessage; return false; }`. It then competes with every other status write
("Refreshing", "N pending", etc.) for that one truncated line.

**Banner pattern to reuse — it already exists twice.** Bordered, wrapped, titled callouts:
- `QueuePanel.axaml`: the config-error banner `IsVisible="{Binding HasConfigDiagnostic}"` (a
  `Border` with a wrapped `TextBlock`), and the `IsVisible="{Binding NeedsInitialization}"`
  init box.
- `TaskDetailPanel.axaml`: the `IsVisible="{Binding HasSelectedTaskError}"` box with a
  `ScrollViewer` + `TextWrapping="Wrap"`.

Mirror that visual language for the HF banner — wrapped text, no ellipsis.

**Opening Settings is a flyout on the top bar.** `src/VisualRelay.App/Views/Controls/TopBar.axaml`
puts the Settings UI in a `Button.Flyout` on the ⚙ button (`Command="{Binding ToggleSettingsCommand}"`,
`<Flyout><controls:SettingsPanel …/></Flyout>`). The flyout opens from *that button's* click, so
opening it programmatically from a QueuePanel banner is view-layer and awkward — see the action
options below.

**A trivially-wireable action already exists.** `MainWindowViewModel.Keys.cs` has
`OpenGetKeyUrlCommand` (`OpenGetKeyUrlAsync(string url)`), and the HF token URL is
`https://huggingface.co/settings/tokens` (from `AllProviderKeys`). A "Get a free token →" button can
bind to it with that URL as `CommandParameter`.

## What to build

TDD where it's testable (VM state/visibility); XAML readability is verified by inspection + the
manual checks below.

### 1. Make `StatusText` readable wherever it renders
In the QueuePanel footer, add `ToolTip.Tip="{Binding StatusText}"` and let it wrap rather than
hard-truncate — e.g. `TextWrapping="Wrap"` with a slightly higher `MaxLines` (3–4) so transient
status is fully (or at least hover-) readable. Keep the top-right chip's existing tooltip. Goal: no
status message is ever fully hidden again.

### 2. Promote the HF-token gate to its own persistent, actionable banner
Add a bordered banner (reuse the `HasConfigDiagnostic` look) shown whenever the token is missing —
bind visibility to `IsHuggingFaceConfigured` being false (introduce a small `ShowHfGate`/
`HasHfGate` computed bool if a negated/loaded-guarded binding reads cleaner). It must:
- Show the **full, wrapped** remediation text (no ellipsis) plus `HfPricingNote`.
- Offer an action. Preferred: a **"Get a free token →"** button bound to `OpenGetKeyUrlCommand`
  with `CommandParameter="https://huggingface.co/settings/tokens"`, alongside text pointing to
  **Settings ⚙** in the top bar. If you can cleanly open the Settings flyout programmatically from
  here, add an "Open Settings" button too — but don't block on it; the link button + clear pointer
  is sufficient.
- **Not hide the task list** — place it so the queue stays browsable (e.g. a slim banner above the
  list or in the footer region), unlike the init/config-error boxes which intentionally replace the
  list.
- Avoid a startup flash: `IsHuggingFaceConfigured` defaults false before `RefreshKeyStatesAsync`
  runs. Gate the banner on keys having been loaded once (a `_keyStatesLoaded` flag or equivalent)
  so it doesn't blink on every launch before the real state is known.

Keep using `StatusText` for transient operational messages; the banner owns the persistent,
actionable gate so the two no longer fight over one line.

## Tests / verification
- **VM visibility test:** with no HF token configured (fake env/keys), the HF-gate visibility
  property is true and `HfGateMessage` is the full string; after a token is configured and
  `RefreshKeyStatesAsync` runs, it flips to false. Assert the no-flash guard: visibility is false
  until the first key-state load completes.
- Manual smoke (note in PR): with no token, launch → the banner is visible, fully readable, the
  "Get a free token →" button opens the HF tokens page, and the task list is still visible/browsable;
  set a token in Settings → banner disappears. Hover the bottom-left status on a long message → full
  text shows.
- `./visual-relay check` green.

## Decisions (settled)
1. **Two-part fix:** general readability for `StatusText` (tooltip + wrap) **and** a dedicated
   banner for the HF gate. *Why:* the user's literal complaint ("I can't view the whole message")
   is the truncation; the HF gate is the high-value case that deserves a persistent, actionable
   surface rather than a transient status line.
2. **Reuse the existing banner pattern** (`HasConfigDiagnostic`/`HasSelectedTaskError`), don't
   invent new styling. *Why:* visual consistency, minimal new code.
3. **No general toast / notification-center system.** *Why:* out of proportion to the need; a
   readable status line plus one actionable banner solves the reported problem. Explicitly out of
   scope — do not build a queue/toast framework.

## Notes
- **Coordination:** `readable-status-and-hf-gate-banner` and `queue-drag-drop-reorder` both edit
  `QueuePanel.axaml` in different regions (this task: the bottom status `Border` + a new HF banner;
  the other: the Up/Down `StackPanel` + the `ListBox`). Locate your anchor by the quoted string and
  re-read if the file drifted because the other task landed first.
- Root cause of why this matters in practice: the token is per-machine state at
  `~/.config/visual-relay/.env`, so a machine without it (vs. one with it) shows this gate — making
  the gate clear and actionable is the difference between "broken, no idea why" and "click here."
