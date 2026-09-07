# Relay Artifacts Reference

This is the authoritative list of every file Visual Relay reads or writes under a
target project's `.relay/` directory, plus the `logSources` contract. Use it to identify any file you
find in a target's `.relay/<task>/` folder.

"Written-by / read-by" tells you who owns the file. "Committed?" tells you
whether Visual Relay stages the file into the task's commit. Nothing under
`.relay/` is: it is Visual Relay's own working state, the run keeps writing it to
disk, and every staging call the commit stage makes carries a
`:(exclude).relay` pathspec so a task's diff shows only the repo's own change.

## Per-target and per-task files

| Path | What it is | Written by / read by | Committed? |
| --- | --- | --- | --- |
| `.relay/config.json` | Relay config for the target: `testCmd`, `testFileCmd`, `logSources`, tier profiles, flags. | Read by Visual Relay. | No (only if the author commits it by hand) |
| `.relay/<task>/manifest.txt` | Newline-delimited list of the in-scope files for the task (proof of scope). Write-only proof; not read back. Renamed from the bare `manifest` so the filename explains itself. | Written by Visual Relay. | No |
| `.relay/<task>/ledger.md` | Running record of each stage (one section per stage). | Written by Visual Relay. | No |
| `.relay/<task>/<task>.seals` | The provenance hash chain. The `seal` term is stamped into every task commit as the `Relay-Seal:` trailer. | Written by Visual Relay. | No |
| `.relay/<task>/stage{n}-attempt{m}.input.json` | Per-attempt stage input (system prompt, task input, metadata). | Written by Visual Relay; read by Visual Relay (UI). | No |
| `.relay/<task>/stage{n}-attempt{m}.report.json` | Per-attempt stage report (outcome, measured usage, served model, `error_message`). | Written by Visual Relay; read by Visual Relay (cost/outcome). | No |
| `.relay/<task>/stage{n}-attempt{m}/<uuid>.jsonl` | Trace session for one attempt. The UUID filename is kept from the format the archived corpus uses. | Written by Visual Relay; read by Visual Relay's trace pane. | No (gitignored) |
| `.relay/<task>/run.log` | Visual Relay's own durable, human-readable run log (one line per event). Distinct from the target's `logs/app.log`. | Written by Visual Relay. | No (gitignored) |
| `.relay/<task>/NEEDS-REVIEW` | Control marker: a runner crash or gate failure flagged this task for review so drains do not loop on it. | Written by Visual Relay. | No |
| `.relay/DRAIN-HALTED` | Control marker: repeated commit-gate rejections halted the drain. | Written by Visual Relay. | No |

## The `logSources` contract

`logSources` in `.relay/config.json` (e.g. `["logs/app.log"]`) lists the TARGET
application's own log files. Visual Relay injects their contents into stage
prompts under a `## Log sources` heading. These files are READ by the LLM and
are NEVER written by Visual Relay — `logs/app.log` is the demo app's runtime
log, not a Visual Relay log. It keeps its name because it belongs to the target.

## Names we deliberately keep and why

These names are load-bearing or externally owned; renaming them would lose
meaning or break tooling:

- **`<task>.seals`** — "seal" is the vocabulary of the provenance hash chain and
  is stamped into every commit as the `Relay-Seal:` trailer. Renaming the file
  would obscure that link.
- **`ledger.md`** — already self-describing; it is a running ledger of stages.
- **`logs/app.log`** — the target app's own log, surfaced via `logSources`. It
  is the target's file, not Visual Relay's, so we do not touch it.
- **`NEEDS-REVIEW` / `DRAIN-HALTED`** — self-describing control markers.
- **`stage{n}-attempt{m}/<uuid>.jsonl`** — the UUID trace filenames are kept so
  the archived corpus and its parsers still read.

The one bare, extensionless name (`manifest`) was the only genuinely confusing
artifact, so it is now `manifest.txt`. The stage-4 JSON contract key
`"manifest"` (`{ "plan": string, "manifest": string[] }`) is a separate thing
and is unchanged.
