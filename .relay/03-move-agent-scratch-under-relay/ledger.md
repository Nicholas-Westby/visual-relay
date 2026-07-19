## Stage 1 - Ideate

{ "summary": "Move agent scratch from .relay-scratch/ to .relay/scratch/: update the agent prompt's protected-paths list, retarget the screenshot tool root, add workspace-level cleanup for the legacy directory, leave exclusion lists untouched during the transition, and add tests for prompt rendering, screenshot path resolution, and cleanup behavior (present/absent/locked dir).", "options": ["A — Exact prescribed approach: minimal inline changes per step, safest/smallest diff, duplicate cleanup pattern tolerated", "B — Centralized legacy-cleanup helper: extract shared TryDeleteAsync to avoid duplicating the pattern between BackendLifecycle and workspace-open", "C — Early-removal (skip transition): drop .relay-scratch from exclusion lists and BackendLifecycle in the same commit, relying on workspace cleanup to sanitize existing dirs"] }

## Stage 2 - Research

{
  "findings": "Codebase investigation complete. The prompt file (ProcessRunners.Prompt.cs:28) contains `.relay-scratch/` in the protected-paths line — needs replacement with `.relay/scratch` guidance. Screenshot tool (Program.cs:23) hardcodes `.relay-scratch/screenshot-root` — needs retarget to `.relay/scratch/screenshot-root`. Six exclusion lists across GitCommitter.Untracked.cs:9, WorktreeFilter.cs:21, WorktreeResetter.cs:24, RelayDriver.CodeChangeGate.cs:101, RelayDriver.VerifyWorktree.cs:192, and NonoRollbackSkipDirs.cs:35 all carry `.relay-scratch` entries that must stay for transition. BackendLifecycle.Start.cs:222-240 already cleans `.relay-scratch` from the VR repo root only — workspace-level cleanup is missing. The workspace refresh path flows: MainWindowViewModel.ReloadTaskListAsync → RelayTaskRepository.ListAsync() — that's where workspace-side cleanup should hook in. The `.relay/.gitignore` (`*` with negations for `.gitignore`/`config.json`) already self-ignores everything under `.relay/`, so `.relay/scratch/` is invisible to git without any changes. Test patterns exist: BuildPromptVerifyCommandTests.cs for prompt assertions, SwivalTestHelpers for Invocation construction, and TestRepository/ScratchRepo for temp-repo test fixtures.",
  "constraints": [
    "Six exclusion lists (.relay-scratch entries) must remain unchanged to protect against stale dirs in the wild during the transition.",
    "BackendLifecycle.Start.cs VR-repo-root cleanup stays as-is — not part of this task's changes.",
    "The `.relay/.gitignore` already covers the new `.relay/scratch/` location — no gitignore changes needed.",
    "Workspace-side cleanup must be best-effort (never block), mirroring the TryDeleteDir pattern from BackendLifecycle.Start.cs.",
    "Prompt must still name the tasks dir + `.relay/` + `.swival/` as protected paths, but drop `.relay-scratch/` and add explicit scratch guidance.",
    "Screenshot tool scratch root change must be a path-only change — no Avalonia dependency for the unit test.",
    "The comment added to an exclusion list must clearly state the `.relay-scratch` entries are removable once workspace cleanup has shipped.",
    "No force-add path (GitCommitter proof files) can pick up scratch content — `.relay/` prefix is already excluded by IsInternalArtifact.",
    "Workspace cleanup test must cover: present dir removed, absent dir no-op, locked dir does not throw."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The agent prompt at ProcessRunners.Prompt.cs:28 names `.relay-scratch/` as a protected scratch area, causing agents to write throwaway artifacts there. The screenshot tool at tools/VisualRelay.Screenshots/Program.cs:23 roots at `.relay-scratch/screenshot-root`. No workspace-side cleanup exists — RelayTaskRepository.ListAsync() never deletes `.relay-scratch/`. The only cleanup is BackendLifecycle.Start.cs:234-239 which runs against the VR repo root only, never the workspace. Six exclusion lists (GitCommitter.Untracked.cs:9, WorktreeFilter.cs:21, WorktreeResetter.cs:24, RelayDriver.CodeChangeGate.cs:101, RelayDriver.VerifyWorktree.cs:192, NonoRollbackSkipDirs.cs:35) all carry `.relay-scratch` entries. `.relay/.gitignore` (RelayGitignoreWriter.cs:13-22) writes `*` with negations, so `.relay/scratch/` is auto-ignored. Proof files (RelayDriver.CommitGate.cs:177-204) only force-add under `.relay/{taskId}/`, never `.relay/scratch/`.",
  "excerpts": [
    "ProcessRunners.Prompt.cs:28: `parts.Insert(3, $\"Protected paths (queue bookkeeping — never part of this task's diff): {invocation.TasksDir}/, .relay/, .relay-scratch/, .swival/\");`",
    "Program.cs:23: `var scratchRoot = Path.GetFullPath(Path.Combine(\".relay-scratch\", \"screenshot-root\"));`",
    "BackendLifecycle.Start.cs:234-239: cleans `.relay-scratch` from VR repo root only — `var legacyScratch = Path.Combine(root, \".relay-scratch\");`",
    "RelayTaskRepository.cs:18-43: `ListAsync()` — no workspace cleanup of `.relay-scratch/`",
    "GitCommitter.Untracked.cs:9: `private static readonly string[] InternalArtifactPrefixes = [\".relay/\", \".relay-scratch/\", \".swival/\"];`",
    "WorktreeFilter.cs:21: `[\".relay/\", \".relay-scratch/\", \".swival/\"]`",
    "WorktreeResetter.cs:24: `[\".relay/\", \".relay-scratch/\", \".swival/\"]`",
    "RelayDriver.CodeChangeGate.cs:101: `private static readonly string[] BookkeepingPrefixes = [\".relay/\", \".relay-scratch/\", \".swival/\"];`",
    "RelayDriver.VerifyWorktree.cs:192: `new HashSet<string>(StringComparer.Ordinal) { \".git\", \".relay\", \".relay-scratch\", \".swival\" };`",
    "NonoRollbackSkipDirs.cs:35: `[\".git\", \".relay\", \".relay-scratch\", \".swival\"]`",
    "RelayGitignoreWriter.cs:13-22: `.relay/.gitignore` writes `*` with negations for `.gitignore`/`config.json` — `scratch/` is auto-ignored",
    "RelayDriver.CommitGate.cs:177-204: proof files only under `.relay/{taskId}/` — never `.relay/scratch/`"
  ],
  "repro": "1. Run a visual-review task against a target workspace. 2. Observe the agent writes a screenshot to `<workspace>/.relay-scratch/match_screenshot.png`. 3. `git status` in the workspace shows `.relay-scratch/match_screenshot.png` as untracked. 4. The legacy cleanup in BackendLifecycle.Start.cs only touches the VR repo root, never the workspace."
}

## Stage 4 - Plan

{
  "plan": "## Step 1: Prompt (ProcessRunners.Prompt.cs:28)\n- Change the protected-paths line to drop `.relay-scratch/` and add scratch guidance.\n- Old: `{TasksDir}/, .relay/, .relay-scratch/, .swival/`\n- New: `{TasksDir}/, .relay/, .swival/\\nWrite throwaway artifacts (screenshots, probes, temporary files) to .relay/scratch/.`\n\n## Step 2: Screenshot tool (Program.cs:23)\n- `scratchRoot` from `.relay-scratch/screenshot-root` → `.relay/scratch/screenshot-root`\n\n## Step 3: Workspace cleanup (RelayTaskRepository.cs)\n- At top of `ListAsync()`, best-effort delete `<RootPath>/.relay-scratch` (try/catch IOException, UnauthorizedAccessException).\n- Mirrors BackendLifecycle.TryDeleteDir pattern. BackendLifecycle.Start stays unchanged.\n\n## Step 4: Exclusion list comment (GitCommitter.Untracked.cs:9)\n- Add TODO comment noting `.relay-scratch/` entry becomes removable once workspace cleanup has propagated.\n- No entries removed from any exclusion list.\n\n## Step 5: Gitignore — no changes\n- `.relay/.gitignore`'s `*` already ignores `scratch/`. Proof files never touch `.relay/scratch/`.\n\n## Step 6: Tests (red first)\n- `+tests/VisualRelay.Tests/BuildPromptScratchGuidanceTests.cs`: assert prompt contains `.relay/scratch` guidance, does NOT contain `.relay-scratch`.\n- `+tests/VisualRelay.Tests/LegacyScratchCleanupTests.cs`: three tests — present dir removed, absent dir no-op, locked dir survives without throw.\n\n## Verification\n`./visual-relay check` green.",
  "manifest": [
    "src/VisualRelay.Core/Execution/ProcessRunners.Prompt.cs",
    "tools/VisualRelay.Screenshots/Program.cs",
    "src/VisualRelay.Core/Tasks/RelayTaskRepository.cs",
    "src/VisualRelay.Core/Execution/GitCommitter.Untracked.cs",
    "+tests/VisualRelay.Tests/BuildPromptScratchGuidanceTests.cs",
    "+tests/VisualRelay.Tests/LegacyScratchCleanupTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/BuildPromptScratchGuidanceTests.cs",
    "tests/VisualRelay.Tests/LegacyScratchCleanupTests.cs"
  ],
  "rationale": "Two test files added, 6 test methods total. 3 fail (red) as expected: the prompt still names .relay-scratch/ and lacks .relay/scratch/ guidance, and ListAsync performs no legacy cleanup. 3 pass (green): the no-TasksDir case correctly omits scratch references, the absent-dir case is a no-op, and the locked-dir case doesn't throw (because no cleanup code exists to throw). All failures align precisely with the 3 implementation steps needed: (1) prompt rewrite, (2) screenshot tool retarget, (3) workspace cleanup in ListAsync."
}

## Stage 6 - Implement

{ "summary": "Moved agent scratch from .relay-scratch/ to .relay/scratch/. Changed prompt (ProcessRunners.Prompt.cs:28) to drop .relay-scratch/ and add .relay/scratch/ guidance. Retargeted screenshot tool (Program.cs:23) to .relay/scratch/screenshot-root. Added workspace-side best-effort cleanup in RelayTaskRepository.ListAsync that deletes stale .relay-scratch/ on open/refresh. Added TODO comment on GitCommitter.Untracked.cs exclusion list noting .relay-scratch/ entry becomes removable post-transition. Exclusion lists unchanged. Full suite 3312 passed, 0 failed." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: All changes are backend C# source code: prompt string, screenshot tool path constant, workspace directory cleanup logic, and unit tests (string/path assertions, filesystem operations). No UI, images, charts, documents, or any rendered visual output exists to review._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Moved agent scratch from `.relay-scratch/` to `.relay/scratch/`. Updated the agent prompt (ProcessRunners.Prompt.cs:28) to drop `.relay-scratch/` from protected paths and add `.relay/scratch/` guidance. Retargeted the screenshot tool (Program.cs:23) to `.relay/scratch/screenshot-root`. Added best-effort workspace-side cleanup in `RelayTaskRepository.ListAsync()` that deletes stale `.relay-scratch/` on workspace open/refresh, mirroring the BackendLifecycle pattern. Added a TODO comment on `GitCommitter.Untracked.cs:9` noting the `.relay-scratch/` exclusion entry becomes removable once the workspace cleanup has propagated. All six exclusion lists remain unchanged for transition safety. Added two test files: `BuildPromptScratchGuidanceTests.cs` (prompt contains `.relay/scratch` guidance, omits `.relay-scratch`) and `LegacyScratchCleanupTests.cs` (present dir removed, absent dir no-op, locked dir does not throw). Full suite: 3312 passed, 0 failed, 115 skipped.",
  "commitMessages": [
    "feat: redirect agent scratch to .relay/scratch/ and drop .relay-scratch/ from prompt",
    "fix: retarget screenshot tool root to .relay/scratch/screenshot-root",
    "feat: add workspace-side cleanup of legacy .relay-scratch/ on repo open",
    "chore: annotate .relay-scratch/ exclusion as removable post-transition",
    "test: add assertions for scratch guidance, path retarget, and legacy cleanup"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

