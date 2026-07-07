# Fix the Vision Tier's Backend Routing — Wrong HF Providers, Silent Blind Fallback

The `vision` tier has plausibly never worked. Evidence gathered 2026-07-07:

- A live probe through the backend (8×8 solid-red PNG sent to `POST
  http://127.0.0.1:4000/v1/chat/completions` with `model: "vision"` and an `image_url` content
  part) returned an **empty answer** from **`moonshot/kimi-k2.7-code`** — a text model. The
  request never reached a vision model.
- `~/.local/share/visual-relay/scratch/litellm.log` shows the chain failing:
  `litellm.APIError: HuggingfaceException - Model Qwen/Qwen3-VL-30B-A3B-Instruct is not
  supported for provider deepinfra`, after the primary `hf-qwen3-vl-235b` attempt also failed,
  then falling through to kimi.
- The Hugging Face inference-provider dashboard shows `Qwen/Qwen3-VL-235B-A22B-Instruct` with
  **4 requests ever, last one 10 days ago** — consistent with "broken since setup".

Two defects compound:

1. **Pinned HF providers don't serve these models.** The generated litellm config
   (`~/.local/share/visual-relay/scratch/litellm-config.generated.yaml`, produced from
   `src/VisualRelay.Core/Configuration/BackendConfigGenerator.cs`) maps
   `hf-qwen3-vl-235b` → `huggingface/novita/Qwen/Qwen3-VL-235B-A22B-Instruct` and
   `hf-qwen3-vl-30b` → `huggingface/deepinfra/Qwen/Qwen3-VL-30B-A3B-Instruct`. The deepinfra
   pin is confirmed wrong by the error above; the novita pin fails too (capture its exact error
   from the litellm log while fixing).
2. **The vision fallback chain degrades to models that cannot see.** In
   `BackendConfigGenerator.cs`, `["vision"]` falls back `hf-qwen3-vl-235b` → `hf-qwen3-vl-30b`
   → `kimi-k2` → `fallback` (a text coder model), and
   `BackendConfigGenerator.Selectable.cs` also offers `kimi-k2` as a UI-selectable "vision"
   choice. So a broken vision route produces quiet, blind, empty-ish answers instead of an
   error anyone would notice.

## What to build

1. **Fix the model routes.** For each of the two VL entries, pick a provider that actually
   serves the model — check the model's Hugging Face page provider list
   (`huggingface.co/Qwen/Qwen3-VL-235B-A22B-Instruct`, `…/Qwen3-VL-30B-A3B-Instruct`) — or drop
   the pinned provider segment entirely (`huggingface/Qwen/Qwen3-VL-235B-A22B-Instruct`) and
   let the HF router auto-select. Prefer auto-routing unless there's a concrete reason to pin;
   add a comment stating the choice and date.
2. **Make vision failures loud.** Remove non-vision models from the `["vision"]` fallback chain
   in `BackendConfigGenerator.cs` (keep 235b → 30b; drop `kimi-k2` and `fallback`) and remove
   `kimi-k2` from the `["vision"]` list in `BackendConfigGenerator.Selectable.cs`. A vision
   request with no working vision model must surface as an API error, never as a silent answer
   from a text model.
3. **Tests.** Extend the generator's existing tests (or add a focused test file) to pin: the
   two VL `litellm_params.model` strings, and the invariant that every model in the vision
   tier's chain and selectable list is vision-capable (assert exact membership).
4. **Verification, layered — the running backend must NOT be restarted by this task** (the live
   pipeline, including the stage running this task, is served by it):
   - **Mandatory:** the unit tests above, against freshly generated config content.
   - **Best-effort runtime probe:** spawn an ephemeral litellm on a spare port (e.g. 4001)
     using the backend venv (`~/.local/share/visual-relay/backend-venv`) with the regenerated
     config, then run the image probe below against it and assert the reply names the color
     red and the responding model is one of the VL entries. If the sandbox blocks spawning or
     binding, record that and rely on the unit tests.
   - **Post-merge human step (document in the final summary):** restart the backend (Settings →
     start-backend or app restart regenerates the config), run the probe against :4000, and
     confirm the HF dashboard's request counter for the VL model increments.

   Probe (self-contained; also usable by the human afterwards):

   ```python
   import zlib, struct, base64, json, urllib.request
   def chunk(t,d): return struct.pack(">I",len(d))+t+d+struct.pack(">I",zlib.crc32(t+d)&0xffffffff)
   raw=b"".join(b"\x00"+b"\xff\x00\x00"*8 for _ in range(8))
   png=b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",8,8,8,2,0,0,0))+chunk(b"IDAT",zlib.compress(raw))+chunk(b"IEND",b"")
   payload={"model":"vision","max_tokens":20,"messages":[{"role":"user","content":[
     {"type":"text","text":"What solid color fills this image? One word."},
     {"type":"image_url","image_url":{"url":"data:image/png;base64,"+base64.b64encode(png).decode()}}]}]}
   req=urllib.request.Request("http://127.0.0.1:4001/v1/chat/completions",
       data=json.dumps(payload).encode(), headers={"Content-Type":"application/json"})
   d=json.loads(urllib.request.urlopen(req,timeout=90).read())
   print(d.get("model"), "→", d["choices"][0]["message"]["content"])
   ```

## Done when

- Generator tests pin the corrected routes and the all-vision-capable invariant for the tier.
- The ephemeral-backend probe answers "red" from a VL model (or the sandbox limitation is
  explicitly recorded with the unit tests green).
- The final summary tells the human the one restart-and-probe step to activate and confirm the
  fix, and notes the HF dashboard counter as the independent witness.
- `./visual-relay check` passes.

## Guardrails

- Do not restart, stop, or reconfigure the **running** backend on port 4000 — it serves this
  very run. Config changes take effect on the next backend start.
- Do not touch other tiers' model chains, and never print or log `HF_TOKEN` or other keys (the
  generated YAML contains secrets — grep it with masking, don't cat it into logs or reports).
- Keep the change scoped to the two generator files and tests; Conventional Commits
  (`docs/commit-messages.md`, `AGENTS.md`); every touched file stays under the 300-line guard.
