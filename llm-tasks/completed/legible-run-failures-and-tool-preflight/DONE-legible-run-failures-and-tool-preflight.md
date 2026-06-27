# Make a failed run say what actually broke — preflight swival/nono and surface the real error, not the sandbox's advisory noise

When a run fails because the **swival** binary (or **nono**) isn't installed on this machine, the app
shows a *misleading* message. The "LATEST RUN FAILED" box (and the queue row) read:

```
swival exit 1: …by 'deny_shell_configs'; use --bypass-protection /Users/nicholaswestby/.envrc to allow access
```

That points at a sandbox-permission rule as if the fix were to bypass `~/.envrc`. It is a **red
herring**. The real failure is the *last* line of nono's output:

```
nono: Command execution failed: swival: cannot find binary path
```

i.e. nono set up the sandbox fine, then could not find `swival` to execute — swival simply isn't on
PATH on this host (it's installed on the VM, not here). The `deny_*` / `--bypass-protection` lines are
nono's **standard per-run advisories** — it lists every protected path it knows about (`~/.ssh`,
keychains, browser data, shell history, shell configs incl. `~/.envrc`) on *every* run, regardless of
whether anything needs them. They are noise, not the error.

Two fixes, both in code (installing swival is **out of scope** — see below):
1. **Fail fast** with a clear, actionable message when a required tool is missing, before launching a
   doomed run.
2. When a run *does* exit nonzero, **build the surfaced reason from the real error**, filtering out
   nono's advisory WARN spam so the user sees `cannot find binary path`, not a `.envrc` line.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted string, not by
> line number; if a snippet has drifted, re-read the file and adapt.

**Which binaries must exist.** `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs`,
`BuildLaunchTarget`:

```csharp
if (_config.BypassSandbox)
{
    return (_swivalBinary, swivalArguments);          // launch swival directly
}
var prefix = BuildNonoPrefix(_config, rollback: true);
var nonoArguments = new List<string>(prefix) { _swivalBinary };
nonoArguments.AddRange(swivalArguments);
return (NonoBinary, nonoArguments);                   // launch `nono … -- swival …`
```

So with the sandbox **on** (the default, `BypassSandbox == false`) the launched process is `nono`
(`NonoBinary = "nono"` in `ProcessRunners.cs`), and nono then execs `swival` (`_swivalBinary`, defaults
to `"swival"`). Required tools: **`swival` always; plus `nono` when the sandbox is on.**

**The actual failure (ground truth).** The autopsy artifact
`.relay/fix-add-attachment-not-appearing/stage1-attempt3.killed-output.txt` ends with:

```
   net  outbound allowed
  ────────────────────────────────────────────────────

nono: Command execution failed: swival: cannot find binary path
```

…preceded by ~55 advisory lines of the form
`WARN '<path>' is blocked by '<protection>'; use --bypass-protection <path> to allow access`
(`deny_credentials`, `deny_keychains_macos`, `deny_browser_data_macos`, `deny_macos_private`,
`deny_shell_history`, `deny_shell_configs`). These print every run and are **not** errors.

**Why the wrong line gets surfaced.** `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`,
the nonzero-exit branch:

```csharp
var reason = $"swival exit {result.ExitCode}: {TrimForTail(result.Output)}" +
    (killedOutputPath is not null ? $" (full output: {killedOutputPath})" : "");
return new SubagentResult(result.Output, null, false, ErrorHintClassifier.WithHint(reason));
```

`TrimForTail` (`ProcessRunners.Helpers.cs`) keeps the **last 600 characters** of the output:

```csharp
private static string TrimForTail(string value, int tailChars = 600)
{
    var text = value.Trim();
    return text.Length <= tailChars ? text : "…" + text[^tailChars..];
}
```

`ProcessCapture.cs` merges stdout and stderr into one buffer in **arrival order**
(`output.AppendLine(e.Data)` on both `OutputDataReceived`/`ErrorDataReceived`), so the ~55 advisory
WARN lines interleave with the real fatal line. The last-600-chars heuristic therefore frequently
lands on an advisory WARN (the `.envrc` one) instead of `cannot find binary path` — exactly the
observed bug.

**How the reason reaches the UI.** `SubagentResult.Error` → `RelayDriver` flags the stage → status.json
`Error` → `src/VisualRelay.App/ViewModels/MainWindowViewModel.RunHistory.cs` selects the
highest-numbered Flagged stage's error:

```csharp
SelectedTaskError = statusRecord
    .Where(e => e.Status == "Flagged")
    .OrderByDescending(e => e.Stage)
    .Select(e => e.Error)
    .FirstOrDefault();
```

…which the "LATEST RUN FAILED" box binds.

**Precedents to reuse (don't invent new infrastructure).**
- *Preflight shape #1* — `ProcessRunners.RunAsync.cs` already short-circuits with a clean result
  before doing work: `var readiness = await _probe(cancellationToken); if (!readiness.IsReady) return new SubagentResult(string.Empty, null, false, readiness.Message);`.
- *Preflight shape #2 + PATH probe* — `ResolveCommandsOnPath` (`ProcessRunners.Helpers.cs`) already
  resolves names against PATH with `pathDirs.Any(dir => File.Exists(Path.Combine(dir, name)))` and
  **refuses to run with a clear message** when nothing resolves ("refusing to run because swival treats
  an empty whitelist as unrestricted"). Mirror this for a tool-presence check.
- *Hint map* — `ErrorHintClassifier.HintFor` (`src/VisualRelay.Domain/ErrorHintClassifier.cs`) is a
  pure signature→hint function already appended via `WithHint`; its doc comment says it is "reusable by
  the pre-flight probe and any UI error surface." Add a missing-binary branch here.
- *GUI gate* — `MainWindowViewModel.Execution.cs`, `EnsureRunnableAsync` already refuses to run when
  config is missing or the HF token is unset (sets `StatusText`, returns false). Best place for a
  fail-fast, *pre-run* tool check so the user never sees the sandbox dump at all.

## What to build

TDD — write the failing tests first (test the pure seams, not a live swival).

### 1. A reusable "required tools present?" check (pure, testable)
Add a pure helper, e.g. `internal static IReadOnlyList<string> MissingRequiredTools(RelayConfig config, …)`,
that resolves the required binaries against PATH (reuse the `File.Exists(Path.Combine(dir, name))`
pattern) and returns the missing ones (empty ⇒ all present). Required set: **`swival` always; plus
`nono` when `!config.BypassSandbox`.** Make PATH/the binary names injectable so a test can simulate
missing/present without touching the real PATH. Probe the **same PATH the launch uses**
(`Environment.GetEnvironmentVariable("PATH")`).

### 2. Fail fast before launching
- **In `SwivalSubagentRunner.RunAsync`**, right after the readiness probe, call the tools check; if any
  are missing, return `new SubagentResult(string.Empty, null, false, ErrorHintClassifier.WithHint(<msg>))`
  — e.g. *"swival is not installed or not on PATH on this machine — Visual Relay can't run tasks here.
  It's set up on the VM, not this host. Install swival and retry."* This stops the doomed launch and
  avoids the entire nono advisory dump.
- **(Recommended) In the GUI `EnsureRunnableAsync`**, add the same check alongside the existing
  config/HF-token gates so Run / Run All refuse up front with a clear `StatusText` (mirror the HF gate).
  Then the user gets an actionable message instead of a failed stage. A full banner is optional; at
  minimum set `StatusText` and return false.

### 3. When a run does exit nonzero, surface the real error
- Add a pure, testable extractor that, given the merged output, **drops nono advisory lines** (those
  containing `is blocked by '` and `use --bypass-protection`) and pure banner/decoration lines, then
  returns the most relevant remaining content — preferring a line that looks like the real failure
  (`Command execution failed`, `cannot find binary path`, `error`, else the final non-empty line). Use
  it to build the `reason` instead of (or feeding) `TrimForTail`. **Keep the `(full output: <path>)`
  breadcrumb.** Net result: `swival exit 1: nono: Command execution failed: swival: cannot find binary
  path (full output: …)` plus the new hint.
- Add the missing-binary signature to `ErrorHintClassifier.HintFor` (e.g. `cannot find binary path`,
  `Command execution failed`, `command not found`, exit `127`) → the actionable hint, so it fires
  whether the failure is caught by preflight or only at process exit.

### Out of scope (state in the PR)
**Installing / provisioning swival on the host.** That's an environment action — and per project policy
global installs require explicit user consent — not a code change. This task only makes the failure
*legible* and *fail-fast*. Likewise, do **not** broaden the vr-guard sandbox to grant `~/.envrc`
(`packaging/nono/vr-guard.json`): the advisory WARNs are harmless and granting them would not fix a
missing binary.

## Tests / verification
- **Tools check (red→green):** unit-test `MissingRequiredTools` — fake PATH with neither tool ⇒
  `[swival, nono]` (sandbox on) / `[swival]` (bypass); both present ⇒ empty.
- **Preflight short-circuit:** with a ready fake `_probe` but a `swivalBinary` name guaranteed absent
  (or an injected empty PATH), `RunAsync` returns a result whose `Error` contains the "not installed /
  not on PATH" message and does **not** contain `deny_shell_configs` or `bypass-protection`.
- **Extractor (red→green):** feed output shaped like the real artifact (dozens of
  `WARN … is blocked by 'deny_…'; use --bypass-protection …` lines, a Capabilities block, then
  `nono: Command execution failed: swival: cannot find binary path`) ⇒ the built reason contains
  `cannot find binary path` and **not** `deny_shell_configs`. Fails today (TrimForTail returns the
  `.envrc` tail).
- **ErrorHintClassifier:** `HintFor("nono: Command execution failed: swival: cannot find binary path")`
  returns the missing-binary hint; existing branches (timeout, auth, connection, missing-json) still
  return their current hints (no regression).
- **GUI gate (if implemented):** tools missing ⇒ `EnsureRunnableAsync` sets a clear `StatusText` and
  returns false without launching.
- `./visual-relay check` green; changed files < 300 lines.

## Done when
- A run on a machine without swival fails (or, better, refuses up front) with a message that names the
  real cause ("swival … not installed / not on PATH") and contains **no** `.envrc` /
  `deny_shell_configs` / `--bypass-protection` noise.
- The `(full output: <path>)` breadcrumb is preserved for deep dives.
- Installing swival is documented as the actual unblock, but never attempted by code.
- TDD: the tools-check and extractor tests fail before the change, pass after.
- Conventional Commit subject, e.g. `fix(execution): preflight swival/nono and surface the real run-failure error`.

## Decisions (settled)
1. **Root cause is a missing `swival` binary (`cannot find binary path`), not a sandbox-permission
   problem.** The `deny_*` / `--bypass-protection` lines are nono's standard per-run advisories and must
   not be surfaced as the failure. *Why:* the ground-truth capture's fatal line is `cannot find binary
   path`; the WARNs print every run.
2. **Diagnosability + fail-fast only; provisioning swival is out of scope** (and needs user consent).
3. **Reuse existing seams** — `ResolveCommandsOnPath`'s PATH probe, the `_probe` preflight shape,
   `ErrorHintClassifier`, and the `EnsureRunnableAsync` gate — not new infrastructure.
4. **Keep the `(full output: <path>)` breadcrumb.**

## Notes
- The app's in-process `PATH` is what matters (the runner probes
  `Environment.GetEnvironmentVariable("PATH")`). Visual Relay is launched from the nix devshell, whose
  PATH has `nono` but not `swival` — which is exactly why the live run reached nono and then failed at
  `cannot find binary path`. Probe the same PATH the launch uses.
- **Coordination:** this task changes *what* text flows into `SelectedTaskError`;
  `fix-run-error-banner-overlap` changes *how* that text is displayed (so it stops overlapping the
  tabs). They compose and touch different files — this one is Core execution (+ optionally
  `MainWindowViewModel.Execution.cs`); the other is `TaskDetailPanel.axaml`. Either order is fine.
