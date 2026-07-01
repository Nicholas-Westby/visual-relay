# Mark the Anthropic and OpenAI Provider Keys as "(Expensive)"

In Settings → **Provider Keys**, the first three providers are labeled "(Recommended)". The Anthropic
and OpenAI keys are comparatively **expensive** to use, and users should see that at a glance before
setting them. Add a **" (Expensive)"** suffix to the Anthropic and OpenAI labels, mirroring exactly how
"(Recommended)" is presented on the other three.

## Current state

The provider list is the single source of truth:
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs`, the static `AllProviderKeys` — a list of
`record ProviderKeyRow(string DisplayName, string EnvVarName, string GetKeyUrl)` — in display order:

```csharp
public static readonly IReadOnlyList<ProviderKeyRow> AllProviderKeys =
[
    new("Hugging Face (Recommended)", "HF_TOKEN",         "https://huggingface.co/settings/tokens"),
    new("DeepSeek (Recommended)",     "DEEPSEEK_API_KEY", "https://platform.deepseek.com/api_keys"),
    new("Moonshot (Recommended)",     "MOONSHOT_API_KEY", "https://platform.moonshot.ai/console/api-keys"),
    new("Anthropic",                  "ANTHROPIC_API_KEY","https://console.anthropic.com/settings/keys"),
    new("OpenAI",                     "OPENAI_API_KEY",   "https://platform.openai.com/api-keys"),
];
```

Key facts:

- There is **no "recommended" boolean flag**. The literal string `" (Recommended)"` is simply baked
  into the `DisplayName` of the first three entries.
- The UI renders `DisplayName` **verbatim**: in `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml`
  each provider row binds `Text="{Binding KeyStates[i].Row.DisplayName}"`. So the label shown is exactly
  whatever `DisplayName` says here — no other formatting layer is involved.

## What to build

- In `AllProviderKeys`, change the **Anthropic** entry's `DisplayName` from `"Anthropic"` to
  `"Anthropic (Expensive)"`, and the **OpenAI** entry from `"OpenAI"` to `"OpenAI (Expensive)"`.
- Match the existing `" (Recommended)"` formatting exactly: a single leading space, then the word in
  parentheses.
- Keep the same baked-into-`DisplayName` approach as "(Recommended)" — **do not** introduce a new field,
  enum, or badge system for this task (a future task could replace both suffixes with a proper
  badge/flag; that is out of scope here).

## Constraints & done criteria

- Settings → Provider Keys shows **"Anthropic (Expensive)"** and **"OpenAI (Expensive)"**; the other
  three still show their "(Recommended)" labels.
- Only the two `DisplayName` strings change. The five rows' order and their positional `KeyStates[0..4]`
  bindings in `SettingsPanel.axaml` are untouched, and `EnvVarName` / `GetKeyUrl` are unchanged.
- **Update any test that pins these display names.** Search `tests/VisualRelay.Tests/` for `"Anthropic"`,
  `"OpenAI"`, `AllProviderKeys`, or `DisplayName` — a provider-list or key-state test may assert the
  exact strings and will need the new suffixes.
- Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs` — the two `DisplayName` strings in
  `AllProviderKeys`.
- `tests/VisualRelay.Tests/` — update any assertion that pins the Anthropic / OpenAI display names.
- (reference, no change) `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` (renders `DisplayName`
  verbatim).
