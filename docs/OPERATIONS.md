# Operations

Operational reference for Visual Relay's model routing and sandbox. For a first
encounter start with the [README](../README.md); for architecture see
[docs/DESIGN.md](DESIGN.md).

## Model Routing

Visual Relay calls the providers directly, in process, over their
OpenAI-compatible `/chat/completions` endpoints. There is nothing to start and
no local service: `./visual-relay launch` opens the app and that is the whole
setup. The only thing a stage needs is a provider key.

`SubagentRunnerFactory` builds the agent that runs a stage. It resolves the
tier's model chain, calls the first provider whose key is present, and streams
what the model produces into the Activity column as it arrives rather than when
the stage ends.

Until 2026-09-01 this worked differently: each stage was a third-party CLI
subprocess talking to a local model gateway that Visual Relay started and
provisioned into a `uv`-built Python venv. The gateway, the venv, the subprocess
and the `uv` dependency have all been removed. If you have an old
`~/.local/share/visual-relay/backend-venv` or a stray `litellm.pid`, nothing
reads them any more and they are safe to delete.

### Provider keys

`ModelCatalog` defines the model aliases each tier resolves to (`cheap`, `balanced`, `frontier`, `vision`, `deepseek-flash`, `hf-qwen3-coder-next`, `kimi-k2`, `glm-5.3-flash`, `hf-glm-5.3-flash`, `fallback`), and `ProviderRoutes` maps each alias to its endpoint, upstream model id and timeouts. No secrets are committed: every key is read from the environment.

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

## Author-test gating

Whether a test can be told from an implementation by its path is a property of the
language, not of the repository: Rust and Zig keep unit tests inside the file under
test, while Go, Python and most others keep them in files of their own. Bootstrap reads
the repository's tracked files (`git ls-files` — never a filesystem walk, so vendored,
generated and build trees never vote), works out which languages it is written in, and
writes what it found into `.relay/config.json`:

```json
{
  "testCmd": "cargo test",
  "authorTests": {
    "detectedLanguages": ["rust"],
    "inlineTestExtensions": [".rs"],
    "diffAudit": "auto"
  },
  "testPaths": []
}
```

- `detectedLanguages` — informational, what init detected, so the defaults below can be
  read back to a reason. Re-running bootstrap refreshes it.
- `inlineTestExtensions` — the extensions whose files may legitimately carry tests next
  to the implementation. Files with these extensions are never reverted for being "not a
  test". Empty for a repository whose languages all keep tests in separate files, which
  is also the right answer for a static site or an infrastructure repository. **Opt a
  language in by adding its extension** (`.ml` for OCaml's `ppx_inline_test`, `.erl` for
  EUnit behind `-ifdef(TEST)`, `.ts` for in-source Vitest); the key is per extension, so
  a Rust and Python repository can treat `.rs` as inline-capable while `.py` stays gated
  by path. Re-running bootstrap refreshes it.
- `diffAudit` — `auto` (default) audits the Author-tests diff only when an
  inline-capable or unrecognized file was edited, `always` audits every one, `off`
  disables the audit. Yours to set: bootstrap seeds it once and never overwrites it.
  The audit is one cheap-tier call that reads the stage's diff (plus any test file
  git does not track yet, up to 60,000 characters) and reports the hunks that change
  behavior rather than assert it. A reported hunk buys the stage the same single
  re-ask a green gate buys it, and nothing more: the audit never flags a task and
  never records a check, so a failed call is logged and the run carries on. What it
  answered lands in `run.log` as `author_test_audit`.
- `testPaths` — repo-specific globs (`"spec/**"`, `"examples/*_example.go"`) that count
  as test paths on top of the built-in filename and directory heuristics. Also yours:
  bootstrap only creates the key when it is missing.
