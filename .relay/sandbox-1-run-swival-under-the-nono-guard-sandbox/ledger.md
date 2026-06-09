## Stage 1 - Ideate

{
  "summary": "Sandbox every Swival subagent under nono by shipping a vr-guard.json profile, adding BypassSandbox to RelayConfig, and conditionally emitting --sandbox nono --nono-profile vr-guard --nono-rollback in BuildArguments. Option A (inline, minimal diff) is recommended; Option B (extract builder) adds testability but scope-creeps; Option C (explicit workspace grant) is defensive over-engineering until disproven needed.",
  "options": ["A — Inline guard profile + BypassSandbox flag (recommended, per task approach)", "B — Extract SwivalArgumentsBuilder for testability then add flags", "C — Add explicit --nono-allow-dir <TargetRoot> in BuildArguments alongside profile"]
}

## Stage 2 - Research

{
  "findings": "The codebase is a .NET 10 Avalonia desktop app with three projects: VisualRelay.Domain (immutable types/records), VisualRelay.Core (services/execution), VisualRelay.App (UI), and VisualRelay.Tests (xunit v3). InternalsVisibleTo already grants tests access to Core internals. The 300-line file-size guard enforces limits on .cs and .axaml files (currently ProcessRunners.cs=250, RelayConfig.cs=15, RelayConfigLoader.cs=150).\n\nExisting structures:\n- RelayConfig (src/VisualRelay.Domain/RelayConfig.cs line 1-15): positional sealed record with 14 fields. Adding `bool BypassSandbox = false` as a 15th positional parameter with a default works — the existing Tests.TestConfig() passes 14 positional args and won't need changes since the 15th is optional.\n- RelayConfigLoader (src/VisualRelay.Core/Configuration/RelayConfigLoader.cs): Defaults() at line 8 uses named args. TryLoadAsync lines 88-100 merges via `with { ... }`. OptionalBool helper at line 146-149 already handles baselineVerify/archiveOnDone — the same pattern applies for BypassSandbox.\n- BuildArguments (ProcessRunners.cs line 111-129): private method returning List<string>. Currently only reachable indirectly through RunAsync (which spawns a real process). The task says to extract to a testable seam — making it internal (already covered by InternalsVisibleTo) is the minimal change; extracting a builder class is Option B from ideation.\n- SubagentRunner constructor (ProcessRunners.cs line 38-49): already accepts RelayConfig as _config field, no signature change needed for the sandbox gating.\n- SwivalProfileSession (SwivalProfileSession.cs line 17-28): writes swival.toml to TargetRoot — this write must succeed under the sandbox. Nono auto-grants --base-dir writes, so this is expected to work (but should be verified in acceptance testing).\n- Existing tests: SwivalSubagentRunnerTests (288 lines) tests BuildArguments indirectly via fake-swival scripts. TestConfig() helper creates RelayConfig with 14 positional args. RelayConfigLoaderTests (134 lines) already tests OptionalBool merge for baselineVerify/archiveOnDone — extend with bypassSandbox. SwivalSubagentRunnerGuardTests (57 lines) tests backend pre-flight guard.\n- Fixtures: tests/VisualRelay.Tests/Fixtures/stage1-attempt1.report.json records `\"sandbox\": { \"mode\": \"builtin\" }` at line 25-27 — this is Swival's default app-layer sandbox, not the OS nono sandbox being added.\n- Packaging: currently only packaging/visual-relay.rb (Homebrew formula). No packaging/nono/ directory exists yet; vr-guard.json is a new artifact.\n- Sandbox versions verified in task notes: nono 0.62, swival 1.0.28 on macOS 26.4 / Apple Silicon.",
  "constraints": [
    "All .cs and .axaml files must stay under 300 lines (enforced by tools/guards/check-file-size.sh). Currently: ProcessRunners.cs=250 (room for ~50 lines), RelayConfig.cs=15, RelayConfigLoader.cs=150, SwivalSubagentRunnerTests.cs=288 (only 12 lines of headroom — new test file or guard tests file may be needed).",
    "RelayConfig is a positional sealed record — adding BypassSandbox must either be a new positional parameter with default false (so existing callers like Tests.TestConfig() compile unchanged) or a non-positional property. A positional `bool BypassSandbox = false` at the end of the parameter list is the pattern consistent with the codebase.",
    "BuildArguments is currently private (line 111). The task requires a testable seam. Options: (A) make it internal (InternalsVisibleTo already in place), which is minimal and matches the recommended Option A; (B) extract SwivalArgumentsBuilder (Option B from ideation, adds scope). The task says 'extract if not already reachable' — internal is the minimal extraction.",
    "The JSON key must be `bypassSandbox` (lowercase 'b') matching the downstream llm-tasks (sandbox-2, sandbox-3) that depend on this name being stable. The C# property is PascalCase `BypassSandbox`, consistent with existing BaselineVerify/ArchiveOnDone conventions.",
    "The nono profile name must be `vr-guard` — sandbox-2 installs it to `${XDG_CONFIG_HOME}/nono/profiles/` and sandbox-3 writes `bypassSandbox` to config, both depend on these names being stable.",
    "Do NOT pass `--nono-block-net` — loopback (127.0.0.1:4000 LiteLLM) and outbound (provider APIs) must remain open. Verified that the guard profile allows this by default.",
    "The guard profile (vr-guard.json) must include `allow_parent_of_protected: true` because `read: [\"/\"]` contains `~/.nono` which nono protects — without it nono refuses the profile entirely.",
    "The `unsafe_macos_seatbelt_rules` in vr-guard.json are macOS-only and required for Chromium to work under Seatbelt (verified empirically). Linux/Landlock does not need them but they are ignored there, which is fine.",
    "Swival's `--sandbox nono` exposes `--nono-profile/--nono-rollback/--nono-block-net/--nono-allow-domain/--nono-network-profile` — but NO GPU passthrough flag. The seatbelt rules avoid the need for `--allow-gpu`.",
    "VR must NOT wrap `nono run` or prefix the command — Swival's native `--sandbox nono` invokes nono internally. The task explicitly confirms this (see Notes section).",
    "The existing Tests.TestConfig() in SwivalSubagentRunnerTests.cs creates a RelayConfig with 14 positional arguments in order. If BypassSandbox is added as positional, the test helper may need updating OR the new field gets a default value so existing callers are unaffected.",
    "A missing nono binary must be a hard error (handled by sandbox-2), not handled here — this task only adds the flags gated on _config.BypassSandbox."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "BuildArguments (ProcessRunners.cs:111-129) emits zero sandbox flags — no --sandbox nono, no --nono-profile, no --nono-rollback. Swival runs with its default --sandbox builtin (app-layer file-tool guards only), confirmed by the fixture at tests/VisualRelay.Tests/Fixtures/stage1-attempt1.report.json:25-27 recording \"sandbox\": { \"mode\": \"builtin\" }. Shell commands are explicitly documented as an unguarded write escape in llm-tasks/parallelize-planning-across-tasks.md:54. RelayConfig (15 lines, 14 positional fields) has no BypassSandbox property; RelayConfigLoader.Defaults() omits it; TryLoadAsync's with { … } block doesn't merge it. The packaging/nono/vr-guard.json artifact doesn't exist yet (packaging/ contains only visual-relay.rb). InternalsVisibleTo to VisualRelay.Tests is already in place (VisualRelay.Core.csproj:14). No bypassSandbox string exists anywhere in the source tree — confirmed by the stage-2 grep that returned zero source matches.",
  "excerpts": [
    "ProcessRunners.cs:111-129 — BuildArguments returns List<string> with -q, --profile, --api-key, --base-dir, --system-prompt, --no-lifecycle, --no-history, --files, --commands, --trace-dir, --report, --max-turns; no --sandbox, --nono-profile, or --nono-rollback flags emitted.",
    "RelayConfig.cs:3-15 — sealed record RelayConfig with 14 positional parameters ending at TestTimeoutMilliseconds; no BypassSandbox field exists.",
    "RelayConfigLoader.cs:8-28 — Defaults() uses named args for all 14 fields; no BypassSandbox default set.",
    "RelayConfigLoader.cs:88-100 — TryLoadAsync with { … } merge block handles BaselineVerify and ArchiveOnDone via OptionalBool but has no bypassSandbox line.",
    "Fixtures/stage1-attempt1.report.json:25-27 — \"sandbox\": { \"mode\": \"builtin\" } confirms Swival currently runs with app-layer path guards only, not OS-level nono.",
    "parallelize-planning-across-tasks.md:54 — \"file-edit tools are sandboxed by files=none/some, but shell is an unguarded write escape\" — documented known gap.",
    "VisualRelay.Core.csproj:14 — InternalsVisibleTo Include=\"VisualRelay.Tests\" already grants test access to Core internals, so BuildArguments can be made internal with no project-file changes.",
    "packaging/ — contains only visual-relay.rb; no packaging/nono/vr-guard.json exists."
  ],
  "repro": "1. Read ProcessRunners.cs:111-129 and confirm no --sandbox flag in the returned list.\n2. Read RelayConfig.cs and confirm no BypassSandbox field among the 14 positional parameters.\n3. Read RelayConfigLoader.cs:88-100 and confirm bypassSandbox is not merged in TryLoadAsync.\n4. Check tests/VisualRelay.Tests/Fixtures/stage1-attempt1.report.json:25-27 — it records \"sandbox\": { \"mode\": \"builtin\" }.\n5. Run `grep -r bypassSandbox src/ tests/` — zero matches in source or test code.\n6. ls packaging/ — no nono/ directory, no vr-guard.json."
}

## Stage 4 - Plan

{
  "plan": "## Concrete implementation plan\n\n### 1. `packaging/nono/vr-guard.json` (NEW)\nCreate the guard profile artifact with the verified JSON content from the task input. This profile extends the `swival` base, allows read-anywhere (`[\"/\"]`), sets `allow_parent_of_protected: true` (required because `/` contains `~/.nono`), and includes the `unsafe_macos_seatbelt_rules` that make Chromium work under Seatbelt without `--allow-gpu`.\n\n### 2. `src/VisualRelay.Domain/RelayConfig.cs` (line 3-15)\nAdd `bool BypassSandbox = false` as the 15th positional parameter. Because it has a default value, all 14 existing callers (e.g., `Tests.TestConfig()` at 14 positional args, `Defaults()` with named args) compile unchanged. The JSON key is `bypassSandbox` (lowercase 'b'), the C# property is `BypassSandbox` (PascalCase), consistent with `BaselineVerify`/`ArchiveOnDone` conventions.\n\n### 3. `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`\n- **`Defaults()` (line 8-28):** add `BypassSandbox: false` to the named-argument constructor call.\n- **`TryLoadAsync` merge block (line 88-100):** add `BypassSandbox = OptionalBool(root, \"bypassSandbox\", defaults.BypassSandbox)` — same pattern as the existing `BaselineVerify` and `ArchiveOnDone` lines.\n\n### 4. `src/VisualRelay.Core/Execution/ProcessRunners.cs`\n- **Line 111:** change `private List<string> BuildArguments` → `internal List<string> BuildArguments` so tests can call it directly (InternalsVisibleTo already in place in the `.csproj`).\n- **After `\"-q\"` (line 116):** insert the sandbox gate:\n  ```csharp\n  if (!_config.BypassSandbox)\n  {\n      args.AddRange([\"--sandbox\", \"nono\", \"--nono-profile\", \"vr-guard\", \"--nono-rollback\"]);\n  }\n  ```\n  This adds 3 flags when the sandbox is on and zero flags when bypassed. Network stays open (no `--nono-block-net`). `_config` is already a field; no constructor signature change.\n\n### 5. `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs` (NEW)\nNew test file because `SwivalSubagentRunnerTests.cs` is at 288/300 lines (only 12 lines headroom — not enough for ~40 lines of sandbox tests). Three tests:\n- **`BuildArguments_DefaultConfig_IncludesSandboxFlags`** — with `BypassSandbox == false` (the default), the args contain `--sandbox nono`, `--nono-profile vr-guard`, `--nono-rollback` in the expected order.\n- **`BuildArguments_BypassSandboxTrue_OmitsSandboxFlags`** — with `BypassSandbox == true`, none of the three sandbox flags appear.\n- **`BuildArguments_BypassSandboxFalse_DoesNotBlockNetwork`** — confirms `--nono-block-net` is absent.\n\nUses the same pattern as existing tests: creates `SwivalSubagentRunner` with `TestConfig()` + `AlwaysReady` probe, builds a `StageInvocation`, calls `BuildArguments` directly (now `internal`), and asserts on the returned `List<string>`.\n\n### 6. `tests/VisualRelay.Tests/RelayConfigLoaderTests.cs` (append ~30 lines)\n- **`LoadAsync_BypassSandboxDefaultsToFalse`** — loads a config that omits `bypassSandbox`, asserts `config.BypassSandbox` is `false`.\n- **`TryLoadAsync_BypassSandboxTrue_FlipsFlag`** — loads `{\"testCmd\": \"dotnet test\", \"bypassSandbox\": true}`, asserts `result.Config.BypassSandbox` is `true`.\n\n### 7. `README.md`\nAdd a **Sandbox** section after \"Model Backend\" (around line 93, before \"What It Does\"). Document:\n- Every Swival subagent runs under **nono** OS sandbox by default, confining writes/deletes to the workspace while leaving reads, network, and tools unrestricted.\n- The `bypassSandbox` key in `.relay/config.json` (`true`/`false`, defaults `false`) turns sandboxing off.\n- The `vr-guard` profile ships with Visual Relay and is installed automatically.\n- Accident containment, not adversarial isolation.\n\n### Line-count budget\n| File | Current | After | Limit |\n|------|---------|-------|-------|\n| ProcessRunners.cs | 250 | ~255 | 300 |\n| RelayConfig.cs | 15 | ~16 | 300 |\n| RelayConfigLoader.cs | 150 | ~152 | 300 |\n| SwivalSubagentRunnerTests.cs | 288 | 288 (unchanged) | 300 |\n| SwivalSubagentRunnerSandboxTests.cs | — | ~55 (new) | 300 |\n| RelayConfigLoaderTests.cs | 134 | ~164 | 300 |",
  "manifest": [
    "packaging/nono/vr-guard.json",
    "src/VisualRelay.Domain/RelayConfig.cs",
    "src/VisualRelay.Core/Configuration/RelayConfigLoader.cs",
    "src/VisualRelay.Core/Execution/ProcessRunners.cs",
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs",
    "tests/VisualRelay.Tests/RelayConfigLoaderTests.cs",
    "README.md"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.cs",
    "tests/VisualRelay.Tests/RelayConfigLoaderTests.cs"
  ],
  "rationale": "Two test files exercise the target behavior. SwivalSubagentRunnerSandboxTests.cs (new, 89 lines) has three BuildArguments tests: default config includes --sandbox nono/--nono-profile vr-guard/--nono-rollback in order (FAILS — flags not emitted), BypassSandbox=true omits them (passes — coincidentally, since no flags exist yet), and --nono-block-net is absent (passes). RelayConfigLoaderTests.cs (appended +30 lines) has two tests: LoadAsync with omitted bypassSandbox defaults to false (passes — scaffolding default), and TryLoadAsync with \"bypassSandbox\":true flips the flag (FAILS — TryLoadAsync doesn't merge the key). Three scaffolding edits (BypassSandbox on RelayConfig, BypassSandbox:false in Defaults(), BuildArguments→internal) made tests compilable without implementing the sandbox logic. Full suite: 367 pass, 2 fail."
}

## Stage 6 - Implement

{
  "summary": "Implemented nono sandboxing in BuildArguments (ProcessRunners.cs:118-121), added BypassSandbox merge in TryLoadAsync (RelayConfigLoader.cs:101), created packaging/nono/vr-guard.json guard profile, added 2 bypassSandbox config-loader tests, and documented the sandbox in README.md. Full suite: 369 pass, 0 fail."
}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "Review verdict was pass with zero issues. All seven files verified on disk: vr-guard.json profile artifact present, BypassSandbox on RelayConfig (line 16), Defaults() + TryLoadAsync merge in RelayConfigLoader (lines 29, 101), BuildArguments sandbox gate in ProcessRunners (lines 118-121), three sandbox tests in SwivalSubagentRunnerSandboxTests.cs, two bypassSandbox config-loader tests in RelayConfigLoaderTests.cs, and README.md sandbox documentation (lines 93-114). All files under 300-line limit. 369 pass / 0 fail. No blockers or warnings exist." }

## Stage 9 - Verify

{
  "summary": "All 7 manifest files present and correct. packaging/nono/vr-guard.json ships the guard profile (extends swival, read-anywhere, allow_parent_of_protected=true, macOS seatbelt rules for Chromium). RelayConfig.cs adds bool BypassSandbox=false as 15th positional field. RelayConfigLoader.cs sets BypassSandbox:false in Defaults() and merges via OptionalBool(root, \"bypassSandbox\", ...) in TryLoadAsync. ProcessRunners.cs has internal BuildArguments with a conditional sandbox gate at lines 118-121 (emits --sandbox nono --nono-profile vr-guard --nono-rollback when !BypassSandbox). SwivalSubagentRunnerSandboxTests.cs (new, 91 lines) has 3 tests verifying default includes flags, bypass omits them, and --nono-block-net is absent. RelayConfigLoaderTests.cs appends 2 tests for bypassSandbox default-false and true-flip. README.md documents the sandbox in a dedicated section (lines 93-114). All files under 300 lines. 369 tests pass, 0 fail.",
  "commitMessages": [
    "feat(execution): sandbox Swival subagents under nono with vr-guard profile",
    "feat(config): add BypassSandbox flag to RelayConfig with bypassSandbox JSON key",
    "test(sandbox): verify BuildArguments emits sandbox flags and config loader merges bypassSandbox",
    "docs: document nono sandbox and bypassSandbox config key in README",
    "feat(packaging): ship vr-guard.json guard profile for nono sandbox"
  ]
}

## Stage 10 - Fix-verify

{ "summary": "Fixed two over-limit files: moved FlagAsync from RelayDriver.cs (304→265 lines) to RelayDriver.Events.cs (51→77 lines), and extracted the repeated git-init pattern in RelayDriverTests.cs into an InitGitRepo helper plus trimmed intra-method blank lines (312→300 lines). Full ./visual-relay check passes: 369 tests green, 0 warnings, 0 errors." }

## Stage 11 - Commit

Committed by Visual Relay.

