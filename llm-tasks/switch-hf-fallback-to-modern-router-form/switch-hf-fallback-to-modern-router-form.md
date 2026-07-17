## Task: Switch the HF fallback model off litellm's retired legacy router route

`hf-qwen3-coder-next` in `tools/backend/litellm-config.yaml` uses the model
string `huggingface/novita/Qwen/Qwen3-Coder-480B-A35B-Instruct`. litellm's
Hugging Face transformer maps a `<provider>/<org>/<repo>` path onto the
LEGACY provider-pinned endpoint — for novita:
`https://router.huggingface.co/novita/v3/openai/chat/completions` — which
Hugging Face has retired. The model itself is alive; only the route is
dead. Because `hf-qwen3-coder-next` is the `fallback` floor that every
tier's chain terminates in, the pipeline's last-resort model is currently
unreachable.

### Evidence (verified 2026-07-17)

- The legacy route served requests through the morning (drains sealed
  commits until ~11:37Z on a config routing everything to this model) and
  was broken by 21:07Z: stage-1 attempts received an HTML page (HF's
  generic landing page) that litellm surfaces as `AuthenticationError:
  HuggingfaceException`, which in turn triggers Visual Relay's misleading
  "provider key missing or invalid" hint. A direct POST to the legacy
  route at ~21:38Z returned HTTP 400. The HF token is valid (whoami 200).
- The MODERN router form works: a direct POST to
  `https://router.huggingface.co/v1/chat/completions` with model
  `Qwen/Qwen3-Coder-480B-A35B-Instruct:novita` returned a real completion.
  litellm builds exactly this request when the config model string is
  `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita` (no provider
  path segment → it falls through to the `/v1/chat/completions` default;
  see `llms/huggingface/chat/transformation.py`, the provider-prefix
  branch vs. the default branch).
- The other HF entries already use modern forms and work:
  `huggingface/zai-org/GLM-5.2:zai-org` (suffix-pinned, verified with a
  live completion) and the two unpinned `huggingface/Qwen/Qwen3-VL-*`
  vision models.

### What to build

1. In `tools/backend/litellm-config.yaml`, change the
   `hf-qwen3-coder-next` model string to
   `huggingface/Qwen/Qwen3-Coder-480B-A35B-Instruct:novita`. Keep the
   `model_name` alias, timeouts, and `Connection: close` header exactly as
   they are.
2. Fix the now-wrong comment above the HF section ("Path after
   `huggingface/` is `<provider>/<hf_org>/<hf_repo>`") to document the
   modern form: `<org>/<repo>:<provider>` suffix pin, or unpinned
   `<org>/<repo>` for HF auto-routing.
3. Add a guard test that scans the template for legacy-form HF model
   strings (`huggingface/<segment>/<segment>/<segment>` with three or more
   path segments and no `:provider` suffix) and fails when one is present,
   so a future model swap cannot silently reintroduce the dead route.

### Constraints

- Do not rename any `model_name` alias: `hf-qwen3-coder-next` is the
  generator's fallback floor (`BackendConfigGenerator.FallbackFloorModel`)
  and the names are the contract the generated swival profiles target.
- No changes to `router_settings`, tier chains, or generator code.

### Tests (red first)

- The legacy-form guard test above — fails today on the novita path
  string.
- Existing config/template tests stay green.

### Verification

- `./visual-relay check` fully green.
- Manual: regenerate the backend config and restart the proxy, then POST a
  chat completion for model `fallback` through `127.0.0.1:4000` — a real
  completion comes back (fails before the change with the HTML-wrapped
  provider error).
