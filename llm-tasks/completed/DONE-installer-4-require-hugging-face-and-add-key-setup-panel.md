# No in-app way to set provider keys — and nothing tells a new user they need a (free) Hugging Face token

After a brew install there is no `.env` to hand-edit — keys live in the
user-level file from `installer-1-relocate-provider-keys-to-user-config.md` — and the app
gives **no guidance about keys at all**. The backend boots even with zero keys
(`litellm-config.yaml` readiness requires none), and the only key-adjacent UI is
the backend up/down dot (`MainWindowViewModel.cs` `IsBackendReachable` /
`BackendStatusLabel`, surfaced in `TopBar.axaml`). So a new user launches, sees a
**healthy** backend, hits Run, and the first stage fails on a missing key with no
hint about which key to get or where.

Hugging Face is the one required key — free to obtain, pay-as-you-go beyond HF's
~$0.10/mo free credit — and it is the floor under every tier
(`installer-3-generate-backend-config-from-present-keys.md`). The app should require it and
guide the rest.

## Goal

An in-app provider-key panel that:

1. lists the five providers (`HF_TOKEN`, `DEEPSEEK_API_KEY`, `MOONSHOT_API_KEY`,
   `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`) with whether each key is set, a
   "Get a key" link, and a paste field that writes the user-level `.env` via the
   `KeyEnvFile` helper;
2. shows which tiers are currently **lit** given present keys (reusing the
   resolver from `installer-3-generate-backend-config-from-present-keys.md`);
3. **requires Hugging Face** — task execution (Run / Drain) is blocked with a
   clear, remediation-oriented message until `HF_TOKEN` is set, while **browsing**
   the queue / stages / markdown / traces stays fully available;
4. states honestly that HF is free to start but real runs are pay-as-you-go.

## Approach (suggested)

- New control `Views/Controls/KeySetupPanel.axaml`(+`.cs`), plus VM state (a new
  `MainWindowViewModel.Keys.cs` partial). Each provider row: name, set/unset
  indicator (read via `KeyEnvFile` + process env), a `Get a key` hyperlink, a
  password-masked paste box, and a Save action that upserts the file and
  re-evaluates lit tiers + the HF gate.
- Suggested link targets (implementer confirms current URLs): HF
  `https://huggingface.co/settings/tokens` · DeepSeek
  `https://platform.deepseek.com/api_keys` · Moonshot
  `https://platform.moonshot.ai/console/api-keys` · Anthropic
  `https://console.anthropic.com/settings/keys` · OpenAI
  `https://platform.openai.com/api-keys`.
- **HF gate**: add `IsHuggingFaceConfigured`; fold it into `RunSelectedCommand` /
  `DrainQueueCommand` `CanExecute` (`MainWindowViewModel.Commands.cs`) — or block
  in the run path with a message like *"Set a free Hugging Face token to run tasks
  — open Keys."* Mirror the existing guided-init resume pattern
  (`_pendingRunTaskId`, `MainWindowViewModel.cs:115-116`) so a blocked Run resumes
  after the token is pasted. Selection / archive toggle / markdown stay enabled.
- **Lit-tiers display**: call the same Core resolver the backend uses so the panel
  and the backend agree on which tiers are live; do not reimplement the mapping.
- **Honest copy**: one line under the HF row — *"Free to get a token; usage is
  pay-as-you-go beyond HF's ~$0.10/mo free credit (no markup)."*

## Files

- New `Views/Controls/KeySetupPanel.axaml`(+`.cs`).
- New `MainWindowViewModel.Keys.cs` partial; `MainWindowViewModel.Commands.cs`
  (gate `CanExecute`).
- `Views/Controls/TopBar.axaml` or `QueuePanel.axaml` (an entry point to open the panel).
- Reuse `KeyEnvFile` and the backend-config resolver; no second copy of either.

## Tests (write the failing tests first)

Unit: the gate flips with HF presence; Save upserts the user `.env`. Headless UI
(reuse `HeadlessTestApp` + `[AvaloniaFact]` + `TestRepository`, per
`DONE-headless-ui-tests-for-config-init-empty-state.md`):

- the panel renders all five providers with correct set/unset state from a seeded
  user `.env`;
- with **no** HF token: Run/Drain is disabled (or blocked with the message), while
  selecting tasks, viewing markdown, and toggling archive still work;
- pasting an HF token into the box + Save writes the user `.env`, flips
  `IsHuggingFaceConfigured`, and **enables** Run;
- lit-tier indicators match the present key set (HF-only vs HF+DeepSeek).

Keep input through the real controls (`KeyTextInput` / click), per the established
harness, not direct VM pokes.

## Sequencing

Depends on `installer-1-relocate-provider-keys-to-user-config.md` (storage) and
`installer-3-generate-backend-config-from-present-keys.md` (lit-tier truth). Complementary to
`DONE-preflight-model-backend-readiness.md` and the top-bar backend indicator —
reuse `RefreshBackendStatusAsync`; do not add a second probe.

## Done when

- A keyless launch surfaces the key panel / guidance.
- Run is blocked with a remediation message until a free HF token is pasted
  **in-app** (which writes the user `.env` and unblocks); browsing works throughout.
- The panel shows set/unset per provider with working "Get a key" links and live
  lit-tier indicators; the honest pay-as-you-go note is present.
- `./visual-relay check` green; `dotnet test` green headlessly; C#/XAML under 300
  lines; Conventional Commit subjects.

## Notes

Password-mask the key fields; never log key values; write only via `KeyEnvFile`
(`0600`). HF-required is a product decision from the installer brainstorm — keep
the gate on **execution**, never on browsing.
