# Upgrade frontier tier from Kimi K2.6 to Kimi K2.7 Code

## Background (researched 2026-06-13)

Moonshot released **Kimi K2.7 Code** on 2026-06-12 (Hugging Face + platform.kimi.ai).
Key facts confirmed from official Moonshot platform docs (platform.kimi.ai/docs/api/v1/models)
and corroborated by MarkTechPost / Codersera:

| Property | Value |
|---|---|
| **Moonshot API model id** | `kimi-k2.7-code` |
| **HF repo** | `moonshotai/Kimi-K2.7-Code` |
| **Context window** | 256 K tokens (262 144) |
| **Input price** | $0.95 / 1M tokens (cache miss); $0.19 / 1M (cache hit) |
| **Output price** | $4.00 / 1M tokens |
| **Cache-write price** | $0.95 / 1M tokens (same as input — industry-standard fallback) |
| **api_base** | `https://api.moonshot.ai/v1` (unchanged from K2.6) |
| **Sampling constraint** | temperature=1.0, top_p=0.95 are fixed; `drop_params: true` already in litellm config handles this |

The **current** Kimi model id is `moonshot/kimi-k2.6` (as seen in `litellm-config.yaml` line
`model: moonshot/kimi-k2.6`).  The alias in the proxy is `kimi-k2` (the `model_name`).

Pricing is **unchanged** ($0.95 / $4.00 sticker rates; $0.19 cache hit); the only meaningful
cost reduction comes from K2.7 using ~30% fewer thinking tokens per task — VR's cost tracking
(which records actual billed tokens from the response) will capture this automatically.
No pricing constant needs to change in `RelayPricing.cs`.

Sources:
- https://platform.kimi.ai/docs/api/v1/models
- https://www.marktechpost.com/2026/06/12/moonshot-ai-releases-kimi-k2-7-code-a-coding-model-reporting-21-8-on-kimi-code-bench-v2-over-k2-6/
- https://codersera.com/blog/kimi-k2-7-complete-guide-2026/

## Current state (researched)

All references to the current Kimi model live in four files:

### 1. `tools/backend/litellm-config.yaml`

- **model_list entry** (`model_name: kimi-k2`, line ~20):
  ```yaml
  - model_name: kimi-k2
    litellm_params:
      model: moonshot/kimi-k2.6
      api_key: os.environ/MOONSHOT_API_KEY
      stream_timeout: 600
      timeout: 480
      extra_headers:
        Connection: close
  ```
  Only the `model:` value needs updating to `moonshot/kimi-k2.7-code`.
  The `model_name: kimi-k2` alias, timeouts, and extra_headers stay unchanged.

- **router_settings.model_group_alias** (generated at runtime by `BackendConfigGenerator`
  from `Chains`, not from the static block in this file — but the static block is also
  present as the template fallback):
  `frontier: kimi-k2` — no change needed (alias stays `kimi-k2`).

- **router_settings.fallbacks** (also generated):
  `frontier: [deepseek-v4-pro, hf-qwen3-coder-next, fallback]` — no change needed.

- **Comment** on `kimi-k2` block references "K2.6" implicitly via the model id —
  update the comment to name K2.7 Code.

### 2. `.env.example` (line 20)

```
# Moonshot — backs the `frontier` tier (kimi-k2) and the kimi-k2 alias.
```
Update the comment to say `kimi-k2 (Kimi K2.7 Code)`.

### 3. `README.md` (line 90 and 110)

Line 90: `kimi-k2` listed in the alias description — no functional change needed, but update
the inline comment in line 110's example log line from `frontier→kimi-k2` (stays correct since
alias is preserved) and the surrounding prose if it calls out K2.6 by name. Quick grep shows
neither line mentions "K2.6" by name, only `kimi-k2`. No edit required to README.

### 4. `src/VisualRelay.Core/Costs/RelayPricing.cs`

```csharp
["frontier"] = new(0.95, 4.0, 0.16, 0.95),
["kimi-k2"]  = new(0.95, 4.0, 0.16, 0.95),
```
Sticker rates are **identical** for K2.7 Code ($0.95 / $4.00; $0.19 cache hit, $0.95
cache write). No numeric change required. The comment on `kimi-k2` entry should be
updated to note it now routes to K2.7 Code.

### 5. Test files

`tests/VisualRelay.Tests/BackendConfigGeneratorTests.PerModelTimeout.cs`:

- `PerModelTimeout_AllNineModelsHaveExplicitCeiling` — enumerates `"kimi-k2"` by
  `model_name` (not the upstream id). No change needed (alias stays `kimi-k2`).
- `PerModelTimeout_FrontierKimiK2Has480s` — same; checks alias name. No change needed.
- `PerModelTimeout_GeneratedYamlPreservesAllCeilings` — same.

`tests/VisualRelay.Tests/BackendConfigGeneratorTests.cs`:

- Tests reference alias string `"kimi-k2"` (not the upstream id). No change needed.

`tests/VisualRelay.Tests/BackendConfigGeneratorTests.AliasConsistency.cs`:

- No kimi model id references; checks tier names only.

`tests/VisualRelay.Tests/SwivalProfileSessionPinningTests.cs` and `.EndToEnd.cs`:
- Reference `balanced-kimi` / `balanced` / `cheap-kimi` in fixture strings — historical
  test content, not model ids. No change needed.

**Conclusion**: tests do not hardcode the upstream model id `moonshot/kimi-k2.6` anywhere;
they operate on the `kimi-k2` alias name which is preserved. No test edits required.

### 6. `SwivalProfileSession.cs` (DefaultToml)

The `[profiles.kimi]` profile block in `DefaultToml` (line ~195):
```toml
[profiles.kimi]
provider = "generic"
base_url = "{ModelBackend.BaseUrl}"
model = "kimi-k2"
max_context_tokens = 200000
```
The `model = "kimi-k2"` references the alias, not the upstream id.
K2.7 Code has a 256K context window vs the current `max_context_tokens = 200000`.
Update `max_context_tokens` to `256000` to reflect the true K2.7 context ceiling.

## What to build

1. **`tools/backend/litellm-config.yaml`** — change one line in the `kimi-k2` model entry:

   ```yaml
   # Before:
   model: moonshot/kimi-k2.6
   # After:
   model: moonshot/kimi-k2.7-code
   ```

   Also update the comment block above the entry (currently says no explicit K2.6 reference
   but may say "kimi-k2 runs the Review stage" etc.) to add a note:
   > "upstream model: kimi-k2.7-code (released 2026-06-12; replaces kimi-k2.6)"

2. **`src/VisualRelay.Core/Execution/SwivalProfileSession.cs`** — in `DefaultToml`,
   update the `[profiles.kimi]` block:

   ```toml
   # Before:
   max_context_tokens = 200000
   # After:
   max_context_tokens = 256000
   ```

3. **`.env.example`** — update the comment on the `MOONSHOT_API_KEY` line:

   ```
   # Before:
   # Moonshot — backs the `frontier` tier (kimi-k2) and the kimi-k2 alias.
   # After:
   # Moonshot — backs the `frontier` tier (kimi-k2 → Kimi K2.7 Code) and the kimi-k2 alias.
   ```

4. **`src/VisualRelay.Core/Costs/RelayPricing.cs`** — pricing numbers are unchanged;
   add a comment on the `kimi-k2` entry to document that it now routes to K2.7 Code:

   ```csharp
   // Before:
   ["kimi-k2"] = new(0.95, 4.0, 0.16, 0.95)
   // After:
   ["kimi-k2"] = new(0.95, 4.0, 0.16, 0.95)  // kimi-k2.7-code (2026-06-12); same sticker rate as k2.6
   ```

   (Rates: $0.95 in / $4.00 out / $0.19 cached-in / $0.95 cache-write — confirmed unchanged.)

5. **No changes needed** to: `BackendConfigGenerator.cs` (uses alias `kimi-k2`, not upstream
   id), `backend.sh`, `README.md`, test files, `SwivalProfileSessionPinningTests.cs`.

## Done when

- [ ] `tools/backend/litellm-config.yaml`: the `kimi-k2` model entry has
  `model: moonshot/kimi-k2.7-code`.
- [ ] `tools/backend/backend.sh start` + manual `curl http://127.0.0.1:4000/health/readiness`
  returns healthy.
- [ ] The generated config summary logged to stderr reads
  `frontier→kimi-k2` (alias preserved) pointing at the updated upstream id (visible in
  `.relay-scratch/litellm-config.generated.yaml` under `model: moonshot/kimi-k2.7-code`).
- [ ] `./visual-relay check` (or `dotnet test`) is green — all existing tests pass.
- [ ] `SwivalProfileSession.DefaultToml` `[profiles.kimi]` has `max_context_tokens = 256000`.
- [ ] No `kimi-k2.6` string remains anywhere in the repo (verify with
  `grep -r "kimi-k2.6" .`).

Conventional Commit subject: `chore(backend): upgrade frontier tier to Kimi K2.7 Code`
