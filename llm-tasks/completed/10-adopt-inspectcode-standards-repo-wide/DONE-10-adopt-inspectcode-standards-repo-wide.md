# Adopt InspectCode standards repo-wide and gate `check` on zero findings

The compiler and existing analyzers catch real defects, but an entire class of
issues slips past them — most visibly the reflection-hop binding escapes fixed in
task 09, where a binding to a non-existent member compiles clean yet is wrong.
Rather than bolt on a narrow guard for that one pattern, this project is **adopting
JetBrains InspectCode (ReSharper) standards across the whole codebase**: drive the
repo to **zero InspectCode findings**, then gate `./visual-relay check` so any new
finding fails the build. A small, **documented carve-out** suppresses the genuinely
untenable inspections (false positives on source-generated members and on members
used only from XAML/reflection bindings); everything else is brought into
compliance.

This is a deliberate, eyes-open tradeoff. The upside is consistent, analyzer-grade
quality enforced locally. The cost the team has accepted: a one-time cleanup of the
backlog, and an ongoing discipline where a future InspectCode **version bump** may
surface new inspections that must be triaged (fix or carve out). The tool version
is therefore **pinned** and bumped deliberately, never floated.

> **Sequencing — do this last.** Final task of a three-task group (08 → 09 → 10).
> Land **08 (harden the test suite)** first so `./visual-relay check` runs to
> completion and is bounded — this gate wires into that `check`. Land **09
> (eliminate reflection-hop bindings)** before this: those binding findings are
> **real defects to fix, not carve-outs** (the StageBoard one is error-level and
> cannot be suppressed away). 09 fixes them properly; this task then cleans up the
> remaining backlog and locks zero in.

## Current state (researched)

- **Scope of findings (measured).** InspectCode 2026.1.2 reports **98** findings on
  `VisualRelay.App` (1 error / 67 warning / 30 note) and **592** across the whole
  solution (1 / 265 / 326). The repo-wide total is dominated by a few **style
  families that are config-level decisions, not per-site fixes** —
  `MethodHasAsyncOverload` (~125), `Xaml.MissingGridIndex` (25),
  `ReplaceWithFieldKeyword` (8), `RedundantSuppressNullableWarningExpression` (14),
  `EmptyGeneralCatchClause` (9, review), `ChangeFieldTypeToSystemThreadingLock` (5).
  The App's 98 bucket to ≈33 SAFE WIN / ≈46 OPINION-STYLE / ≈13 UNTENABLE (and most
  of those untenable are the binding findings that **task 09 fixes**, not carve-outs).
  See *Carve-out* below for the authoritative list.
- **Toolchain is Nix-provisioned.** `flake.nix:20-30` exposes a `mkShell` with
  `dotnet-sdk_10` (+ git, icu, openssl, zlib, nono, uv, python). There is **no
  dotnet tool manifest** (`.config/dotnet-tools.json`) yet and no shellHook. The
  `visual-relay` wrapper auto-re-enters `nix develop` when tools are missing
  (`visual-relay:108-149`).
- **`check` runs guards + format + build + test + screenshots** with no inspection
  step (`visual-relay:350-359`); guard scripts live in `tools/guards/` and are
  invoked from the `check)` branch.
- **InspectCode facts (empirically verified):** package `JetBrains.ReSharper.GlobalTools`
  (validated **2026.1.2**), dispatched via `jb` (`dotnet jb inspectcode <target>`);
  installs as a `--local`/path tool with no global footprint (does **not** touch
  `~/.dotnet/tools`); needs only the .NET SDK (no mono); **no nixpkgs package** (SDK
  from the devshell + `dotnet tool restore`). It **always exits 0 even with
  findings** — gating requires parsing the SARIF. Default output SARIF; `--no-build`
  skips the rebuild; `--caches-home`/`--source`/`NUGET_PACKAGES` redirect caches.
  ReSharper reads repo `.editorconfig` (incl. `resharper_*_highlighting` /
  `dotnet_diagnostic.*.severity` keys) — that is the mechanism for both style
  decisions and the carve-out.
- **Avalonia analysis fires headlessly, no IDE divergence** (confirmed): it resolves
  XAML→code references for *compiled* bindings, so unused-member findings on
  XAML-bound members are mostly avoided — **except** in `x:CompileBindings="False"`
  / reflection-binding regions, where usage is invisible and false "unused"
  findings concentrate (a primary source of carve-outs).
- **Timing:** App-only warm run with `--no-build` + persistent `--caches-home` is
  **~9.4s**; a whole-repo run will be longer. Measure it (below) and choose the gate's
  home accordingly.

## What to build

### 1. Local tool manifest

Add `.config/dotnet-tools.json` pinning `JetBrains.ReSharper.GlobalTools` to a
specific version (command `jb`). Local/manifest install only — no `-g`.

### 2. Provision through the devshell

Restore via `dotnet tool restore` (idempotent) from the guard script so it works
inside `nix develop` using only the flake's `dotnet-sdk_10`. No global install, no
nixpkgs derivation (none exists).

### 3. Establish the standards baseline in `.editorconfig`

A repo-root `.editorconfig` is the single source of truth for what "compliant"
means:

- **Style decisions** for the OPINION-STYLE inspections the team accepts (var usage,
  expression bodies, etc.) — encode the chosen convention so the cleanup pass and
  future code agree with the analyzer rather than fighting it.
- **The carve-out**: set the UNTENABLE inspections to `severity = none`
  (`resharper_<rule>_highlighting = none` / `dotnet_diagnostic.<id>.severity = none`),
  **each with a comment naming why** (e.g. "false positive on `[ObservableProperty]`
  backing fields", "member used only via reflection binding in TaskDetailPanel"). A
  carve-out must scope as narrowly as the rule allows. Prefer `.editorconfig` over
  scattered `// ReSharper disable` comments; use inline suppression only where a
  rule can't be scoped in config, and annotate the reason there too.
- **Never carve out a real defect.** The task-09 binding escapes (error-level
  `.XAMLErrors`, `Xaml.BindingWithContextNotResolved`) are fixed in code, not
  suppressed.

### 4. One-time compliance cleanup

Drive the SAFE WIN + OPINION-STYLE buckets to zero across the repo, by category:

- **SAFE WINs** (≈33 in App; e.g. `RedundantUsingDirective` ×11, `RedundantNameQualifier`
  ×6, dead private fields, the missing `hyperlink` style ×5) are minutes each — apply
  them (`jb cleanupcode` can do many mechanically; review the rest).
- **OPINION-STYLE families are a comply-or-silence decision, not 592 hand-edits.** The
  big ones (`MethodHasAsyncOverload` ~125, `Xaml.MissingGridIndex` 25,
  `ReplaceWithFieldKeyword` 8, primary-constructor 5) should be decided **per family**:
  either adopt the convention (bulk "Fix in scope") or set the rule to `none`/`suggestion`
  in `.editorconfig` with a one-line rationale. Reaching zero is mostly rule-curation.
- Do **not** blindly auto-apply across generated or XAML-bound members — verify against
  the carve-out so a "fix" never deletes or narrows a member that is live via a binding
  or a generator (the cleanup ran *before* the gate, so a bad auto-fix won't be caught
  for you). Keep changed files under 300 lines; split commits by category for review.

### 5. Guard script + zero-findings gate `tools/guards/inspect-code.sh`

- `dotnet tool restore`, then `dotnet jb inspectcode --no-build` over the **whole
  repo** — target `VisualRelay.slnx` directly (verified: InspectCode 2026.1.2 reads
  `.slnx` and covers all 10 projects + tests in one pass; no per-csproj fallback
  needed). Use a persistent `--caches-home` under XDG/temp; emit SARIF there.
- **Gate on zero:** because the carve-out lives in `.editorconfig` (suppressed
  findings simply don't appear) and the backlog is cleaned, steady state is zero
  reported findings at the configured floor. The script **exits non-zero if the
  SARIF contains *any* result** at/above that floor — no ruleId allowlist needed.
  Do not rely on InspectCode's exit code (always 0). Pick the floor deliberately
  (default SUGGESTION matches "full compliance"; document the choice).
- **Isolation:** caches, downloads, and SARIF go to `${XDG_CACHE_HOME:-$HOME/.cache}/visual-relay/inspectcode/…`,
  never the repo tree or a global location.

### 6. Wire it in (local — there is no quality CI)

All gating is local via `./visual-relay check` (the only workflow, `release.yml`, is
tag-triggered packaging). Expose `./visual-relay inspect` calling the guard and add
it to the usage string (`visual-relay:369`). Then **measure the whole-repo run
time** and decide: if it stays within an acceptable inner-loop budget, add it to the
`check)` branch after the build step; if it's too slow for every `check`, run it on
**pre-push** (`.githooks/`) instead so it's still enforced without taxing every
inner loop. State the choice and the measured time in the commit body. Do **not**
add a CI job.

## Carve-out (from the categorization investigation)

This was captured on the **pre-08/09 tree** — it is the planning input. **Finalize
the carve-out by re-running InspectCode after 08 + 09 land**, because several
"untenable" findings are *fixed* by 09, not suppressed, and must not be carved out.

**Do NOT carve out — fixed by task 09 (they vanish once the bindings are fixed):**

- `.XAMLErrors` (1, **error**, `StageBoard.axaml:40`), `Xaml.BindingWithContextNotResolved`
  (2, `TaskDetailPanel.axaml:260/267`), and the three matching
  `Xaml.PossibleNullReferenceException` (same bindings). These are the `$parent.DataContext`
  → `RelayCommand` hops 09 removes.
- `UnusedMember.Global` on `TaskRowViewModel.SiblingPaths` — flagged only because it's
  bound in the `x:CompileBindings="False"` attachment list (`TaskDetailPanel.axaml:245`),
  which 09 rewrites; expect it to clear once 09 reads it through a typed path. **It is a
  false positive — never delete it.** If it still trips after 09, suppress *member-scoped*
  (`// ReSharper disable once UnusedMember.Global` on that property), not the whole rule —
  `UnusedMember.Global` catches real dead code elsewhere (`ProgressText`,
  `RestoreRunningTaskState`).

**Durable carve-outs (real false positives on this stack, independent of 09)** — add to
repo `.editorconfig` (`root = true` already), each annotated:

```ini
[*.cs]
# CommunityToolkit [ObservableProperty] backing-field initializer wrongly flagged "ignored".
resharper_member_initializer_value_ignored_highlighting = none
# Avalonia codegen supplies the 2nd partial half of *.axaml.cs classes at build (×5 repo-wide).
resharper_partial_type_with_single_part_highlighting = none
# Entry point / runtime-instantiated types (Program, etc.) — not constructed in visible C#.
resharper_class_never_instantiated_global_highlighting = none
# Interface-contract params (e.g. cancellationToken) unused in some implementations (×3 repo-wide).
resharper_unused_parameter_global_highlighting = none
```

**One unavoidable non-`.editorconfig` case:** the single `.XAMLErrors` **error** has no
stable `resharper_*` editorconfig key. Task 09's fix should clear it outright; if it ever
recurs without a 09-style fix, it needs an inline ReSharper region or a `.DotSettings`
hint — flag to the team rather than silently widening a rule.

**Genuinely real findings the cleanup should FIX (not carve out), verified:** the missing
`Button.hyperlink` style (×5 — no such selector exists), dead private state
(`_runningTask`/`_runningStageNumber`/`_runningStageName`), a redundant `.Where(p is not
null)` after a non-nullable projection, and the truly-dead members above. `MemberCanBePrivate.Global`
(×7) is a safe win **except** the `MainWindowViewModel` ctor and `TestCommandFinder`, which
are DI/`{ get; init; }`-constructed — keep those public.

## Done when

- **Baseline documented:** `.editorconfig` encodes the accepted style conventions and
  the carve-out, every suppressed inspection annotated with why; no real defect
  (the 09 binding errors) is suppressed.
- **Repo is compliant:** a whole-repo InspectCode run reports **zero** findings at
  the configured floor — the SAFE/OPINION backlog fixed, the UNTENABLE set carved
  out in config, the 09 binding escapes fixed in code. No member that is live via a
  binding or a source generator was broken by a "fix" (spot-checked by launching the
  app / `./visual-relay screenshot`).
- **Gate works:** `tools/guards/inspect-code.sh` exits non-zero on *any* finding at
  the floor (verified by temporarily introducing one) and zero on the clean tree;
  InspectCode's always-zero exit code is not relied upon; all caches stay in
  XDG/temp.
- **Enforced locally:** the guard is reachable as `./visual-relay inspect` and wired
  into either `check` (after build) or pre-push per the measured-time decision; usage
  text updated. No CI job added. Tool version pinned in the manifest.
- `./visual-relay check` green; shell/C#/XAML files stay under 300 lines;
  Conventional Commit subjects.
