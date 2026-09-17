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
Landlock on Linux and, on Windows, Landlock inside a WSL2 distro). The sandbox confines writes
and deletes to the target workspace while leaving network and all tools, including
Playwright/Chromium, unrestricted. This is **accident containment**, not defense against a
malicious agent: a stray `rm -rf` or `mv` outside the workspace is blocked by the OS.

Reads differ by platform. On macOS they are unrestricted. On Linux, and so inside the WSL
distro, Landlock can only allow, never deny beneath an allow, so nono refuses a grant that
covers its own state or a denied path such as `~/.ssh`; reading `/` would be both. There the
profile reads the system roots (`/usr`, `/etc`, `/opt`, `/var`) and the toolchain homes
(`~/.cargo`, `~/.rustup`, `~/.nvm`, `~/go`, caches) instead, and the rest of your home, including
the Windows drives under `/mnt`, stays unreadable.

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
are expanded; and each path must resolve under `$HOME` or the workspace root. On Windows
`~` and `$HOME` resolve against the WSL distro user's home, not the Windows profile,
because that is where the commands run.

### Windows

All three platforms run the **same profile through the same binary**. On Windows that binary
lives inside a WSL2 distro: every command is started as `wsl.exe -d <distro> --exec …` and the
distro's kernel enforces it, so the credential, browser-data and shell-history denials are
enforced by the kernel here too — there is no "may be readable" caveat anywhere in the UI.
The profile is placed inside the distro (`~/.config/visual-relay/vr-guard.json`, written
through the `\\wsl.localhost` share) with the same overwrite-always behaviour.

Landlock is active in the stock WSL2 kernel without any configuration: Microsoft's kernel
config `arch/x86/configs/config-wsl` sets `CONFIG_SECURITY_LANDLOCK=y` and lists `landlock`
first in `CONFIG_LSM` on the `linux-msft-wsl-5.15.y`,
[`6.6`](https://raw.githubusercontent.com/microsoft/WSL2-Linux-Kernel/linux-msft-wsl-6.6.y/arch/x86/configs/config-wsl)
and [`6.18`](https://raw.githubusercontent.com/microsoft/WSL2-Linux-Kernel/linux-msft-wsl-6.18.y/arch/x86/configs/config-wsl)
branches, and the [WSL kernel release notes](https://learn.microsoft.com/en-us/windows/wsl/kernel-release-notes)
record it as enabled since 5.15.57.1 (August 2022). The launch gate still checks it at
runtime, because a custom `kernel=` in `.wslconfig` can take it away. It asks nono
(`nono setup --check-only`, a Landlock syscall probe) rather than reading
`/sys/kernel/security/lsm`: a systemd distro such as Ubuntu 26.04 does not mount
securityfs under WSL, so that file is missing even while Landlock is up.

Two consequences worth stating plainly: the workspace must live on the distro's own
filesystem (a `/mnt/<letter>` workspace is refused), and the repository's build and test
commands run inside the distro, so a Windows-only toolchain is not supported. See
[TROUBLESHOOTING.md](../TROUBLESHOOTING.md) for the gate's messages and the runtime
verification checklist.

## How bootstrap finds the test command

Three steps, in order, and nothing is written until something passes a real run:

1. **Built-in candidates.** A table of marker files (`package.json`, `Cargo.toml`,
   `*.sln` …) yields candidate commands. Each is run once in your checkout and the
   first that passes is kept.
2. **The proposer.** When every candidate is rejected — or the table recognises no
   marker in a folder that *does* hold tracked source files — a small agent run is
   asked for the command. It gets the commands already tried with their exit codes and
   output, and it has the normal tool catalog, so it can read the CI configuration, the
   README and the scripts they point at, and try its answer before giving it. Its
   answer is then checked exactly as a built-in candidate is, under the sandbox the
   pipeline will run the command through anyway. At most two rounds; a repeated answer
   ends it.
3. **The placeholder.** If nothing passes, `testCmd` is the no-op placeholder and the
   status says what was tried rather than offering scaffolding advice. A genuinely
   greenfield folder (no tracked source files) skips step 2 entirely and keeps that
   advice, because there it is the right advice.

The proposer costs one cheap-tier agent run of at most 30 turns, and only on a
repository the table could not handle. Its trace and report land in
`.relay/bootstrap/`, and every rejection — built-in and proposed alike — is written to
`.relay/setup-check.log`. A machine with no provider key skips step 2 rather than
failing at it. A command that came from step 2 says so in the status, so you can look
at it once: `testCmd: <command> (proposed by the model and checked; review it in
.relay/config.json)`.

## Per-file test commands

`testFileCmd` is what the author-tests gate runs, with `{files}` replaced by the
space-joined paths of the test files that stage just wrote. It exists so the gate runs
those files rather than the whole suite.

Bootstrap proves the command before writing it. It substitutes up to two of the
repository's own test files — the smallest, so the proof is quick — and requires a
clean run: the command must contain `{files}`, must actually run tests, and must exit
0. Visual Relay is for repositories whose suite is green, so an existing test file run
alone passes; anything else means the form is wrong. The runner table's form is tried
first, then a model is asked for one, and each answer goes through the same proof.

`testFileCmd: null` means nothing passed its proof. That is not a failure: the gate
falls back to `testCmd` and runs the whole suite, which is slower and still correct,
and the run log says so. A repository with no test file at all gets the table's form
written unproven, because there is nothing to prove it with.

A `testFileCmd` you wrote yourself is never proven, replaced or removed. The proof
applies only to what bootstrap itself is about to write.

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
  to the implementation. Every file the model lists as a test file is kept whatever its
  extension; this key only changes how the gate judges one, and an entry here says a
  file may be both. Empty for a repository whose languages all keep tests in separate files, which
  is also the right answer for a static site or an infrastructure repository. **Opt a
  language in by adding its extension** (`.ml` for OCaml's `ppx_inline_test`, `.erl` for
  EUnit behind `-ifdef(TEST)`, `.ts` for in-source Vitest); the key is per extension, so
  a Rust and Python repository can treat `.rs` as inline-capable while `.py` stays gated
  by path. Re-running bootstrap refreshes it.
- `diffAudit` — `auto` (default) audits the Author-tests diff only when an
  inline-capable or unrecognized file was edited, `always` audits every one, `off`
  disables the audit. Yours to set: bootstrap seeds it once and never overwrites it.
  The audit is one cheap-tier call per Stage 5 run: it reads the stage's diff (plus any
  test file git does not track yet, up to 60,000 characters) and reports the hunks that
  change behavior rather than assert it. A re-asked stage is not audited again, because
  the single re-ask its answer could buy is already spent. A reported hunk buys the stage the same single
  re-ask a green gate buys it, and nothing more: the audit never flags a task and
  never records a check, so a failed call is logged and the run carries on. What it
  answered lands in `run.log` as `author_test_audit`.
- `testPaths` — repo-specific globs (`"spec/**"`, `"examples/*_example.go"`) that count
  as test paths on top of the built-in filename and directory heuristics. Also yours:
  bootstrap only creates the key when it is missing.
