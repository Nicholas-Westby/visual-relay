# Operations

Operational reference for Visual Relay's model backend and sandbox. For a first
encounter start with the [README](../README.md); for architecture see
[docs/DESIGN.md](DESIGN.md).

## Model Backend

Every Visual Relay profile targets a local OpenAI-compatible proxy (LiteLLM) at `http://127.0.0.1:4000`. Visual Relay owns this proxy's lifecycle, so `./visual-relay launch` auto-starts it before opening the app: the launch hook runs the `VisualRelay.Backend start` tool best-effort, and the app's GUI shares the same C# lifecycle for its one-click recovery. When the backend is already healthy this is a fast no-op, and if it cannot start the app still launches and the in-app pre-flight probe surfaces the down backend.

On first start the tool provisions LiteLLM itself — there is no manual install step. It uses [`uv`](https://docs.astral.sh/uv/) to create a virtualenv at `$XDG_DATA_HOME/visual-relay/backend-venv` (default `~/.local/share/visual-relay/backend-venv`) pinned to Python 3.13 (LiteLLM's `uvloop` crashes on 3.14+) and installs `litellm[proxy]` into it; `uv` fetches the pinned Python automatically. The venv lives under the user's XDG data home (never the repo tree, so host and VM each own their own) and is reused on later starts, so only the first launch pays the install cost. The single prerequisite is `uv` on `PATH` (`curl -LsSf https://astral.sh/uv/install.sh | sh`); if a `litellm` is already on `PATH` and `uv` is absent, the tool falls back to that.

Manage the proxy directly with:

```bash
VisualRelay.Backend start    # idempotent; brings the proxy up on 127.0.0.1:4000 and waits for /health/readiness
VisualRelay.Backend status   # reports up/down
VisualRelay.Backend stop     # SIGTERM then SIGKILL, and removes the PID file
```

(From a source checkout: `dotnet run --project tools/VisualRelay.Backend -- {start|stop|status}`.)

`start` is re-runnable any time: a healthy instance exits 0 with no duplicate process, a stale PID file is cleaned up automatically, and after launching it polls `/health/readiness` (up to ~30s) before returning. `stop` always removes the PID file, even after an abrupt kill, so the next `start` is never blocked by a stale pidfile. The PID and log files live under `$XDG_DATA_HOME/visual-relay/scratch/` (`litellm.pid`, `litellm.log`).

### Provider keys

The proxy config `tools/backend/litellm-config.yaml` defines the model aliases the profiles reference (`cheap`, `balanced`, `frontier`, `vision`, `claude`, `claude-opus-1m`, `claude-sonnet`, `gpt-5`, `hf-qwen3-coder-next`, `kimi-k2`, `glm-5.2`, `fallback`). No secrets are committed: every key is read from the environment via `os.environ/<KEY>`.

The **`fallback`** tier is the always-available floor: it resolves to `hf-qwen3-coder-next` (Hugging Face Novita Qwen3-Coder-480B, ~$0.38/$1.55 per 1M tokens in/out) and requires only `HF_TOKEN`. Every other tier can fall through to it when its provider keys are absent. Override the default model via `tierProfiles.fallback` in `.relay/config.json`.

See [`.env.example`](../.env.example) for the full provider key set, both key locations, and the resolution precedence (process env > user-level `~/.config/visual-relay/.env`). The in-app key panel reads and writes the user-level path.

`VisualRelay.Backend start` loads keys from the user-level file automatically. Before launching LiteLLM it **generates a key-aware config** at `$XDG_DATA_HOME/visual-relay/scratch/litellm-config.generated.yaml`: each tier alias points directly at the best model whose provider key is present, so missing keys never incur an auth-error retry on the dead primary. The static `litellm-config.yaml` remains the single source of truth for provider routes and settings — only the alias and fallback assignments are rewritten. Config generation is bounded by a timeout; on timeout or any failure it falls back to the static template so a wedged generator never blocks startup.

## Sandbox

Every Swival subagent runs under **nono** OS-level sandboxing by default (Seatbelt on macOS,
Landlock on Linux). The sandbox confines writes and deletes to the target workspace while
leaving reads, network, and all tools — including Playwright/Chromium — unrestricted. This is
**accident containment**, not defense against a malicious agent: a stray `rm -rf` or `mv`
outside the workspace is blocked by the OS.

The sandbox is **always on** — there is no opt-out. Every Swival subagent and every
verification command runs under nono with the `vr-guard` profile, and `nono` is a hard,
always-required dependency.

The `vr-guard` profile ships embedded in Visual Relay, which **owns and self-heals** it at
`${XDG_CONFIG_HOME:-$HOME/.config}/visual-relay/vr-guard.json` (beside VR's `.env`) — the file is
rewritten to match the shipped content at the start of every run, so it can never go stale, and
nono loads it by absolute path. A missing nono binary is a hard error.
The profile grants per-ecosystem toolchain cache paths (.NET, Swift, Node, Python, Go, Rust)
so package-manager writes (`dotnet restore`, `swift build`, `npm install`, `pip install`,
`go build`, `cargo build`) succeed inside the sandbox.  The destructive surface — Documents,
Desktop, Pictures, credentials, shell history, browser data — stays denied.

For exotic toolchains whose cache paths the baseline profile does not cover, add a
`sandboxExtraAllowPaths` array to `.relay/config.json`:

```json
{
  "testCmd": "dotnet test",
  "sandboxExtraAllowPaths": ["~/.cache/exotic-tool"]
}
```

Each entry is appended as `-a <path>` to both the Swival and verification nono invocations.
Entries are validated at config load: `..` (path traversal) is rejected; `~` and `$HOME`
are expanded; and each path must resolve under `$HOME` or the workspace root.
