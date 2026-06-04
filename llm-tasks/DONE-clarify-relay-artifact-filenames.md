# Relay artifact filenames are unlabeled — you can't tell what most files are

Opening a task folder, the files don't explain themselves. The clearest example: `logs/app.log`
looks like it might be Visual Relay's own log, but it's actually the **target project's** app log,
seeded by the sample generator (`tools/VisualRelay.SampleTasks/Program.cs:84`) and declared as a
context source via `.relay/config.json` → `logSources` (`:72`), which Visual Relay hands to the
agent under `## Log sources` (`src/VisualRelay.Core/Execution/ProcessRunners.cs:112-114`). It is
read by the LLM, never by Visual Relay. Nothing in the folder says any of that.

The `.relay/<task>/` artifacts are similarly unlabeled, and they fall into three groups that call
for different treatment:

- **Externally owned (cannot rename):** `swival.toml`, `.swival/`, and the trace session files
  `stage{n}-attempt{m}/<uuid>.jsonl` — Swival requires these exact names and generates the UUIDs.
  Documentation is the only lever.
- **Load-bearing vocabulary (must not rename):** `<task>.seals` is the provenance hash chain
  (`RelayDriver.Artifacts.cs:27`); the term "seal" is stamped into every commit as the
  `Relay-Seal:` trailer (`src/VisualRelay.Core/Execution/GitCommitter.cs:41`) and is the task
  hash itself. Renaming the file while keeping the trailer would make things *less* clear.
  `ledger.md` (committed running record) and `NEEDS-REVIEW` / `DRAIN-HALTED`
  (`DrainCircuitBreaker.cs:8`) markers are already self-describing.
- **Genuinely improvable:** `manifest` (`RelayDriver.Artifacts.cs:13`) is the one bare,
  extensionless name; it's a write-only proof file (the in-scope list; not read back), so it's
  safe to clarify.

The right fix is a legend plus one safe rename — not a blanket rename that would churn provenance
files and break Swival's required names.

## Recommended fix

1. **Add an authoritative artifact reference in `docs/`** (extend the partial list in
   `docs/DESIGN.md:33-40,50-51`, or add `docs/relay-artifacts.md`): for every file Visual Relay
   reads or writes under `.relay/` — `config.json`, `manifest`, `ledger.md`, `<task>.seals`,
   `stage{n}-attempt{m}.report.json`, the trace dir + `<uuid>.jsonl`, `NEEDS-REVIEW`,
   `DRAIN-HALTED` — plus the Swival-owned `swival.toml` / `.swival/` and the `logSources`
   contract, give one line: what it is, who writes/reads it, and whether it's committed.
2. **Clarify the sample's layout where the confusion happens.** Expand the generated
   `sample-tasks/README.md` (`tools/VisualRelay.SampleTasks/Program.cs:16-33`) to name each
   top-level piece: `src/` + `tests/` are the demo app under test; `logs/app.log` is **the demo
   app's runtime log, surfaced to the agent via `logSources`** (not a Visual Relay log);
   `llm-tasks/` are the pending tasks; `.relay/` holds Relay's per-task working and proof
   artifacts (point to the doc from step 1).
3. **Rename the one bare name:** `manifest` → `manifest.txt` (it's a newline-delimited list).
   Update the writer (`RelayDriver.Artifacts.cs:13`), the `proofFiles` entry
   (`RelayDriver.cs:136`), any tests asserting `"manifest"`, and the doc. Optionally append
   `.jsonl` to `<task>.seals` to signal its line-delimited JSON format **while keeping the "seal"
   term** — but only if it stays consistent with the `Relay-Seal:` trailer vocabulary.
4. **Explicitly keep** `logs/app.log`, `<task>.seals` (the term), `ledger.md`, `swival.toml`,
   `NEEDS-REVIEW`, `DRAIN-HALTED`, and the trace UUIDs — and say why in the doc, so the decision
   is recorded and not re-litigated.

## Sequencing

- Largely independent (docs + one rename). Keep it consistent with
  `persist-run-diagnostics-log.md`, which adds Visual Relay's *own* run log and relies on
  `logs/app.log` staying the target-app log it must not write to — list both in the reference so
  the Visual-Relay-log vs. target-app-log distinction is explicit. The `manifest` rename touches
  different files than the attempt/history tasks, so no conflict there.

## Done when

- A contributor can identify every file in a target's `.relay/<task>/` and the sample's
  `logs/app.log` from one documented reference, including the Swival-owned names that can't be
  renamed.
- The generated `sample-tasks/README.md` explains its own layout and `logs/app.log`'s role.
- `manifest` is written and committed as `manifest.txt`; no other proof file is renamed;
  `seal`/`ledger`/`app.log`/`swival.toml` are unchanged and the reason is documented.
- Tests covering manifest/proof-file writing are updated to the new name; the proof commit still
  succeeds. Write/adjust the failing test first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
