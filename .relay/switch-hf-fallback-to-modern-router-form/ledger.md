## Stage 1 - Ideate

{
  "summary": "Replace the legacy `huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct` model string (which routes through the retired provider-pinned endpoint) with the modern `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita` form (which hits the working `/v1/chat/completions` endpoint). Update the adjacent comment to document `<org>/<repo>:<provider>` suffix pinning, and add a guard test that rejects any three-segment `huggingface/<a>/<b>/<c>` pattern lacking a `:provider` suffix.",
  "options": [
    "Option A — Minimal targeted fix: edit the one model string, fix the comment, and add a focused pytest that reads `tools/backend/litellm-config.yaml` and asserts no legacy-form HF model strings exist. Narrowest blast radius, fastest to ship.",
    "Option B — Fix + config validation utility: same edits plus a standalone reusable checker script (`tools/backend/check-hf-model-strings.py`) callable from both pytest and CI. The guard test shells out to it. More reusable, slightly higher upfront cost.",
    "Option C — Fix + broad lint sweep: same edits plus a guard that scans all YAML files under `tools/` for legacy patterns (not just the primary config). Catches dormant legacy routes in secondary configs but requires path-exclusion rules for vendor files."
  ]
}

## Stage 2 - Research

{
  "findings": "## Codebase investigation findings\n\n### 1. Config file location and structure\n- **File**: `tools/backend/litellm-config.yaml` — static template read by `BackendConfigGenerator`\n- The generator passes `model_list:` verbatim; it only rewrites `router_settings.model_group_alias` and `router_settings.fallbacks`. So any change to the model string lives in the static template and survives into generated configs.\n\n### 2. Target entry (lines 98–111)\n- `model_name: hf-qwen3-coder-next` — the fallback floor model\n- Current model string (line 102): `huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct` — LEGACY form (3 path segments after `huggingface/`, no `:provider` suffix)\n- The comment above (line 99) says: `# Path after huggingface/ is <provider>/<hf_org>/<hf_repo>.` — documents the legacy form and needs updating\n\n### 3. Other HF entries (already modern)\n- `huggingface/zai-org/GLM-5.2:zai-org` (line 25) — 2 segments + `:provider` suffix (modern suffix-pinned form)\n- `huggingface/Qwen/Qwen3-VL-235B-A22B-Instruct` (line 119) — 2 segments, no suffix (modern auto-routed form)\n- `huggingface/Qwen/Qwen3-VL-30B-A3B-Instruct` (line 125) — 2 segments, no suffix (modern auto-routed form)\n\n### 4. Legacy form detection rule\n- Strip the `huggingface/` prefix, split remainder by `/`, and count segments. If there are >=3 segments AND no segment contains `:` → legacy form (the provider appears as a path segment instead of a suffix)\n\n### 5. Test infrastructure\n- All tests are C# xUnit (file `*.cs` in `tests/VisualRelay.Tests/`) — **no Python/pytest infrastructure exists**\n- Tests locate the template via `BackendConfigGeneratorTestHelpers.TemplatePath` which resolves through `RepoSetup.Root` (walks up from assembly dir to find the `visual-relay` sentinel)\n- Existing config-content guards follow the pattern: `File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath)` + string assertions (see `BackendConfigGeneratorPerModelTimeoutTests`, `BackendConfigGeneratorKimiK2_7UpstreamTests`, `BackendConfigGeneratorVisionTierTests`)\n- New test files auto-discover via xUnit; no registration needed (Sdk-style project with `ImplicitUsings`)\n\n### 6. The `FallbackFloorModel` constant\n- `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs` line 12: `private const string FallbackFloorModel = \"hf-qwen3-coder-next\";`\n- This is the **model_name alias**, not the upstream model string; the task says don't rename it — consistent with leaving all `model_name` values unchanged\n\n### 7. Other model entries around the target\n- The `Connection: close` header (lines 110–111), `timeout: 120` (line 107), and `api_key: os.environ/HF_TOKEN` (line 103) must all be preserved verbatim — the task says keep them exactly as they are\n- The `router_settings` block (lines 155–188), tier chains, and generator code must not change\n\n### 8. `visual-relay check` command\n- Defined in `tools/VisualRelay.Cli/Commands/CheckCommand.cs`\n- Runs source enumeration gates first, then `dotnet test`. Any guard test that fails will cause `check` to be non-green",
  "constraints": [
    "Do not rename any `model_name` alias — `hf-qwen3-coder-next` is `BackendConfigGenerator.FallbackFloorModel` and the names are the contract swival profiles target",
    "Preserve all `litellm_params` verbatim for `hf-qwen3-coder-next`: `api_key: os.environ/HF_TOKEN`, `timeout: 120`, and `extra_headers: { Connection: close }` — only the `model:` string changes",
    "No changes to `router_settings`, tier chains, or generator code (`BackendConfigGenerator` partial classes)",
    "The guard test must be a C# xUnit `[Fact]` in `tests/VisualRelay.Tests/` — no Python tests exist in the project; there is no pytest infrastructure to add to",
    "The guard test must fail BEFORE the model string fix (red-first), and pass after (the test asserts absence of the legacy pattern, so on the current code it must find the offending `huggingface/novita/Qwen/...` string and fail)",
    "Existing config/template tests must stay green after the change — especially `BackendConfigGeneratorPerModelTimeoutTests` (checks HF models have 120s timeout), `BackendConfigGeneratorVisionTierTests` (checks vision model strings), and `BackendConfigGeneratorTests.HfOnly_DefaultTiersResolveToFallbackFloor` (checks `hf-qwen3-coder-next` alias resolve)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Line 102 of tools/backend/litellm-config.yaml uses the legacy model string `huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct`. litellm's Hugging Face transformer maps `<provider>/<org>/<repo>` (3+ path segments, no `:provider` suffix) to a retired provider-pinned endpoint (`https://router.huggingface.co/novita/v3/openai/chat/completions`). The modern suffix-pinned form `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita` hits `/v1/chat/completions` — confirmed working by direct POST. The other three HF entries in the template already use modern forms and work (lines 25, 119, 125). Because this model is the `fallback` alias (line 178) and every tier chain terminates in it (lines 184-188), the pipeline's last-resort model is unreachable. The comment on line 99 documents the wrong (legacy) form. Detection rule: strip `huggingface/` prefix, split by `/`, count segments — ≥3 segments with no `:` in any segment = legacy. All four HF entries classify correctly: line 25 (2 segments + `:` → modern ✓), line 102 (3 segments, no `:` → LEGACY ✗), line 119 (2 segments → modern ✓), line 125 (2 segments → modern ✓).",
  "excerpts": [
    "tools/backend/litellm-config.yaml:99:  # Path after `huggingface/` is `<provider>/<hf_org>/<hf_repo>`.",
    "tools/backend/litellm-config.yaml:102:      model: huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct",
    "tools/backend/litellm-config.yaml:178:    fallback: hf-qwen3-coder-next",
    "src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs:12:    private const string FallbackFloorModel = \"hf-qwen3-coder-next\";"
  ],
  "repro": "1. Read the template: grep 'model: huggingface/' tools/backend/litellm-config.yaml. Line 102 shows 3 path segments after `huggingface/` with no `:provider` suffix — the legacy provider-pinned form. 2. Apply the detection rule: split `novita/Qwen/Qwen3-Coder-480B-A35B-Instruct` by `/` → 3 segments, none contain `:`. The other three entries each have either 2 segments (auto-routed) or 2 segments plus a `:provider` suffix (suffix-pinned). 3. The guard test that scans for `huggingface/<a>/<b>/<c>` with no `:` must fail on current code and pass after the model string is changed to `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita`."
}

## Stage 4 - Plan

{
  "plan": "## Plan: Switch HF fallback to modern router form\n\n### Step 1 — Fix the model string in the template\n\nEdit `tools/backend/litellm-config.yaml`:\n\n- **Line 99 (comment)**: Replace the legacy-form documentation\n  ```\n  # Path after `huggingface/` is `<provider>/<hf_org>/<hf_repo>`.\n  ```\n  with modern-form documentation:\n  ```\n  # Path after `huggingface/` is `<org>/<repo>[:<provider>]` for suffix-pinned\n  # routing, or unpinned `<org>/<repo>` for HF auto-routing.\n  ```\n\n- **Line 102 (model string)**: Replace\n  ```\n  model: huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct\n  ```\n  with\n  ```\n  model: huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita\n  ```\n  All other fields (`model_name: hf-qwen3-coder-next`, `api_key: os.environ/HF_TOKEN`, `timeout: 120`, `extra_headers: { Connection: close }`) are preserved exactly as-is.\n\n### Step 2 — Add a guard test to prevent legacy-form regressions\n\nCreate `+tests/VisualRelay.Tests/BackendConfigGeneratorHfModelStringGuardTests.cs`:\n\n- A single `[Fact]` method that:\n  1. Reads the template YAML via `File.ReadAllText(BackendConfigGeneratorTestHelpers.TemplatePath)`.\n  2. Extracts every line matching `model: huggingface/…`.\n  3. For each match, strips the `huggingface/` prefix, splits the remainder by `/`.\n  4. Asserts no entry has ≥3 segments AND no segment contains `:` (the legacy provider-pinned form).\n- Detection logic: `segments.Length >= 3 && !segments.Any(s => s.Contains(':'))` → legacy form.\n- Red-first: fails on current template because `novita/Qwen/Qwen3-Coder-480B-A35B-Instruct` splits into 3 segments with no `:`. Passes after Step 1 because `Qwen/Qwen3-Coder-480B-A35B-Instruct:novita` has 2 segments + `:` suffix in the second segment.\n\n### Verification\n\n- `./visual-relay check` runs the guard test; it must be green after both steps.\n- Existing tests unaffected: `BackendConfigGeneratorPerModelTimeoutTests` (timeout assertions on `hf-qwen3-coder-next` unchanged), `BackendConfigGeneratorVisionTierTests` (vision model string assertions unchanged), `BackendConfigGeneratorTests.HfOnly_DefaultTiersResolveToFallbackFloor` (alias resolution unchanged — model_name aliases are not modified), `BackendConfigGeneratorKimiK2_7UpstreamTests` (kimi upstream assertion unchanged), `SplitGuardVerificationTests` (new file does not match oversized-family prefixes, baseline unchanged).",
  "manifest": [
    "tools/backend/litellm-config.yaml",
    "+tests/VisualRelay.Tests/BackendConfigGeneratorHfModelStringGuardTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1.

## Stage 5 - Author-tests

{ "testFiles": ["tests/VisualRelay.Tests/BackendConfigGeneratorHfModelStringGuardTests.cs"], "rationale": "Guard test detects legacy-form HF model strings (huggingface/<provider>/<org>/<repo> with ≥3 path segments and no :provider suffix). Confirmed red on the old novita string, then green after the model string was changed to the modern suffix-pinned form huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita. All 18 related existing tests (PerModelTimeout, VisionTier, KimiK2_7) remain green." }

## Stage 6 - Implement

{ "summary": "Changed hf-qwen3-coder-next model string from legacy `huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct` to modern `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita`; updated the HF section comment to document `<org>/<repo>[:<provider>]` suffix-pinned and unpinned forms. The guard test in `BackendConfigGeneratorHfModelStringGuardTests.cs` now passes. All 3190 tests green, 0 failed." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Changes are a model-string replacement in a YAML config, a comment update, and a new C# guard test — none involve rendered UI, layout, styles, images, charts, or any visual output._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Changed the `hf-qwen3-coder-next` model string in `tools/backend/litellm-config.yaml` from the legacy provider-pinned form `huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct` (which routed through the retired `router.huggingface.co/<provider>/v3/openai/chat/completions` endpoint) to the modern suffix-pinned form `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita` (which hits the working `/v1/chat/completions` endpoint). Updated the HF section comment to document `<org>/<repo>[:<provider>]` suffix-pinned routing. Added guard test `BackendConfigGeneratorHfModelStringGuardTests` that scans the template for legacy three-segment `huggingface/<provider>/<org>/<repo>` patterns (no `:provider` suffix) and fails if any are present, preventing silent reintroduction of the dead route. All 3190 tests passed, 0 failed.",
  "commitMessages": [
    "fix: switch HF fallback model to modern router form",
    "fix(litellm-config): use suffix-pinned model string for novita",
    "fix: migrate hf-qwen3-coder-next off legacy HF route",
    "test: guard against legacy HF provider-pinned model strings",
    "fix(config): drop legacy provider path from HF model string"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

