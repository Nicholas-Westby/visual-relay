# Operations

Operational reference for Visual Relay's model routing and sandbox. For a first
encounter start with the [README](../README.md); for architecture see
[docs/DESIGN.md](DESIGN.md).

## Model Backend

Visual Relay calls the providers directly, in process, over their
OpenAI-compatible `/chat/completions` endpoints. There is nothing to start and
no local service: `./visual-relay launch` opens the app and that is the whole
setup. The only thing a stage needs is a provider key.

`SubagentRunnerFactory` builds the agent that runs a stage. It resolves the
tier's model chain, calls the first provider whose key is present, and streams
what the model produces into the Activity column as it arrives rather than when
the stage ends.

Until 2026-09-01 this worked differently: each stage was a third-party CLI
subprocess talking to a LiteLLM proxy that Visual Relay started on `127.0.0.1:4000` and
provisioned into a `uv`-built Python venv. The proxy, the venv, the subprocess
and the `uv` dependency have all been removed. If you have an old
`~/.local/share/visual-relay/backend-venv` or a stray `litellm.pid`, nothing
reads them any more and they are safe to delete.

### Provider keys

`BackendConfigGenerator` defines the model aliases each tier resolves to (`cheap`, `balanced`, `frontier`, `vision`, `hf-qwen3-coder-next`, `kimi-k2`, `glm-5.3-flash`, `hf-glm-5.3-flash`, `fallback`), and `ProviderRoutes` maps each alias to its endpoint, upstream model id and timeouts. No secrets are committed: every key is read from the environment.

The **`fallback`** tier is the always-available floor: it resolves to `hf-qwen3-coder-next` (Hugging Face Novita Qwen3-Coder-480B, ~$0.38/$1.55 per 1M tokens in/out) and requires only `HF_TOKEN`. Every other tier can fall through to it when its provider keys are absent. Override the default model via `tierProfiles.fallback` in `.relay/config.json`.

See [`.env.example`](../.env.example) for the full provider key set, both key locations, and the resolution precedence (process env > user-level `~/.config/visual-relay/.env`). The in-app key panel reads and writes the user-level path.

Key resolution happens per stage: each tier's chain is filtered to the models whose provider key is actually present, so a missing key skips that model rather than spending an auth-error retry on it. A tier with no usable model fails the stage immediately and says which key would fix it.

## Sandbox

Every agent command runs under **nono** OS-level sandboxing by default (Seatbelt on macOS,
Landlock on Linux). The sandbox confines writes and deletes to the target workspace while
leaving reads, network, and all tools — including Playwright/Chromium — unrestricted. This is
**accident containment**, not defense against a malicious agent: a stray `rm -rf` or `mv`
outside the workspace is blocked by the OS.

The sandbox is **always on** — there is no opt-out. Every agent command and every
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

Each entry is appended as `-a <path>` to both the agent and verification nono invocations.
Entries are validated at config load: `..` (path traversal) is rejected; `~` and `$HOME`
are expanded; and each path must resolve under `$HOME` or the workspace root.
