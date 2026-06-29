## Stage 1 - Ideate

{ "summary": "Audit all system prompts in RelayStages for Visual Relay-specific idioms (like `./visual-relay check`) and replace them with generic, tool-agnostic alternatives so prompts are portable across repositories. The known offender is in the review-stage prompt; a full audit across all stages is needed to catch any other VR-specific references.", "options": ["A — Targeted keyword scrub (minimal touch): search-and-replace VR-specific terms with generic equivalents, smallest diff.", "B — Abstraction layer with template placeholders: replace VR-specific commands with {{verify_command}}-style placeholders resolved at invocation time, more refactoring but future-proof.", "C — Per-stage semantic audit: read each prompt end-to-end and rewrite any framing that assumes VR-specific context, catches subtle issues keyword search misses."] }

## Stage 2 - Research

{ "findings": "Found 4 occurrences of `./visual-relay check` in RelayStages.cs system prompts (Author-tests/Implement/Review/Fix-verify lines 58/65/77/103) where a VR-specific example illustrates the full gate prohibition. Also found RewriteGuidance.cs contains VR-specific idioms on line 10 (names the tool) and lines 24-26 (VR repo conventions like C#/XAML files under 300 lines, [AvaloniaFact], XDG state). The test file RewriteGuidanceTests.cs line 24 hardcodes an assertion against `./visual-relay check`. The Fix stage, ConfirmImplementationSystemPrompt, and all other stage prompts (Ideate/Research/Diagnose/Plan/Verify) are already clean. CodingStageSystemPromptTests.cs asserts structural invariants that won't break from removing just the parenthetical examples.", "constraints": ["The RelayStages.cs prompts flow verbatim to every repo Visual Relay runs on — any change must remain universally applicable.", "The RewriteGuidance.cs prompt is consumed by the 'Rewrite with AI' feature and currently assumes the target repo is Visual Relay itself; rule #5 conflates generic spec-writing guidance with repo-specific conventions.", "RewriteGuidanceTests.cs line 24 hardcodes Assert.Contains(\"./visual-relay check\") and must be updated in lockstep with RewriteGuidance.SystemPrompt.", "The Fix stage (line 84) and ConfirmImplementationSystemPrompt (line 115) already correctly use the prohibition without a VR-specific parenthetical, confirming the pattern of what the clean version looks like.", "A full per-stage semantic audit (Option C from ideation) is warranted here since the VR-specific content in RewriteGuidance.cs goes beyond just `./visual-relay check` — it includes C#/XAML file limits, Avalonia test attributes, and XDG state storage conventions that are all VR-repo-specific."] }

## Stage 3 - Diagnose

{
  "evidence": "Three files contain Visual Relay-specific idioms in system prompts that flow verbatim to every repository VR runs on:\n\n1. RelayStages.cs (4 occurrences): The parenthetical `(e.g. ./visual-relay check)` appears in the Author-tests (line 58), Implement (line 65), Review (line 77), and Fix-verify (line 103) stage system prompts. Each illustrates the \"do NOT run the full gate\" prohibition with a VR-specific command that won't exist in target repos. The Fix stage (line 84) and ConfirmImplementationSystemPrompt (line 115) already omit the parenthetical — proving the clean pattern.\n\n2. RewriteGuidance.cs (5 issues): Line 10 says \"You rewrite a single Visual Relay task spec\" — names the tool. Rule #5 (lines 24-26) is entirely VR-repo-specific: `./visual-relay check` must pass, C#/XAML files under 300 lines, headless UI tests use `[AvaloniaFact]`, per-machine state lives in XDG. These conventions (Avalonia, XDG, file-size limits) are specific to the Visual Relay repo itself, not universal.\n\n3. RewriteGuidanceTests.cs (1 issue): Line 24 hardcodes `Assert.Contains(\"./visual-relay check\", prompt)` — must be updated in lockstep with the RewriteGuidance.SystemPrompt change.\n\nCodingStageSystemPromptTests.cs asserts only structural invariants (\"do NOT run\", \"harness\", \"diff-scoped\") and won't break from removing parenthetical examples.",
  "excerpts": [
    "RelayStages.cs:58 — \"check, lint, format, build, or screenshot gate (e.g. `./visual-relay check`) — \" (Author-tests)",
    "RelayStages.cs:65 — \"check, lint, or format gate (e.g. `./visual-relay check`) during \" (Implement)",
    "RelayStages.cs:77 — \"check, lint, format, build, or screenshot gate (e.g. `./visual-relay check`) — \" (Review)",
    "RelayStages.cs:103 — \"Do NOT run the project's broader orchestration gate (e.g. `./visual-relay check`). \" (Fix-verify)",
    "RelayStages.cs:84 — clean pattern reference: Fix stage uses same prohibition WITHOUT the VR parenthetical",
    "RelayStages.cs:115 — clean pattern reference: ConfirmImplementationSystemPrompt uses prohibition WITHOUT VR parenthetical",
    "RewriteGuidance.cs:10 — \"You rewrite a single Visual Relay task spec into a better one.\"",
    "RewriteGuidance.cs:24-26 — Rule #5: `./visual-relay check` must pass; keep changed C#/XAML files under 300 lines; headless UI tests use `[AvaloniaFact]`; per-machine state lives in XDG, never in-repo.",
    "RewriteGuidanceTests.cs:24 — Assert.Contains(\"./visual-relay check\", prompt, StringComparison.Ordinal)"
  ],
  "repro": "grep -n 'visual-relay check\\|Visual Relay task spec\\|AvaloniaFact\\|under 300 lines\\|XDG, never in-repo' src/VisualRelay.Core/Execution/RelayStages.cs src/VisualRelay.Core/Execution/RewriteGuidance.cs tests/VisualRelay.Tests/RewriteGuidanceTests.cs"
}

## Stage 4 - Plan

{
  "plan": "Remove Visual Relay-specific idioms from three system-prompt files so prompts are portable across repositories.\n\nRelayStages.cs: Drop the `(e.g. ./visual-relay check)` parenthetical from Author-tests, Implement, Review, and Fix-verify prompts — matching the already-clean Fix and ConfirmImplementationSystemPrompt patterns.\n\nRewriteGuidance.cs: (a) Change line 10 from \"You rewrite a single Visual Relay task spec\" to \"You rewrite a single LLM task spec\". (b) Replace Rule #5's VR-specific guardrails (`./visual-relay check`, C#/XAML under 300 lines, `[AvaloniaFact]`, XDG state) with a generic version: \"the project's test suite must pass; respect the repo's established file-size conventions, test-framework attributes, and state-storage policies.\"\n\nRewriteGuidanceTests.cs: Replace the line 24 assertion on `./visual-relay check` with an assertion on the new generic guardrail text (\"repo's guardrails\" or equivalent).\n\nNo other files need changes — CodingStageSystemPromptTests.cs asserts only structural invariants, and all other stage/guard files are internal implementation, not LLM-facing prompts.",
  "manifest": [
    "src/VisualRelay.Core/Execution/RelayStages.cs",
    "src/VisualRelay.Core/Execution/RewriteGuidance.cs",
    "tests/VisualRelay.Tests/RewriteGuidanceTests.cs"
  ]
}

> **Manifest merge (stage 5)**: added 1 authored test file(s).

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/CodingStageSystemPromptTests.cs",
    "tests/VisualRelay.Tests/RewriteGuidanceTests.cs"
  ],
  "rationale": "Added 2 new test methods to CodingStageSystemPromptTests.cs: a parameterized theory CodingStageSystemPrompt_DoesNotContainVisualRelayCheck covering all 5 coding stages (Author-tests, Implement, Review, Fix, Fix-verify) plus a fact for ConfirmImplementationPrompt. Added 1 new fact SystemPrompt_DoesNotContainVisualRelaySpecificIdioms to RewriteGuidanceTests.cs asserting absence of all 5 VR-specific idioms (./visual-relay check, Visual Relay task spec, [AvaloniaFact], XDG/never in-repo, C#/XAML under 300 lines). Updated the existing SystemPrompt_ContainsCoreRules assertion from requiring ./visual-relay check to requiring the generic 'test suite must pass' replacement. All 6 new assertions fail against the current code — the VR idioms are still present and the generic guardrail text is absent — confirming the tests gate the target behavior correctly."
}

## Stage 6 - Implement

{ "summary": "Removed Visual Relay-specific idioms from system prompts in two files: RelayStages.cs (dropped `(e.g. ./visual-relay check)` parenthetical from Author-tests/Implement/Review/Fix-verify prompts) and RewriteGuidance.cs (changed 'Visual Relay task spec' to 'LLM task spec' and replaced Rule #5's VR-specific guardrails with a generic version about test suite, file-size conventions, test-framework attributes, and state-storage policies). All 5 RewriteGuidanceTests pass, and grep confirms zero remaining ./visual-relay check occurrences in src/VisualRelay.Core/Execution." }

## Stage 7 - Review

{ "verdict": "changes", "issues": ["RewriteGuidance.cs L24-25: The rewrite of Rule #5 dropped the original 'a Conventional Commit subject' guardrail. Conventional Commits is a portable, non-Visual-Relay-specific standard, so per the task's 'only rewrite what is necessary' instruction it should have been preserved. The four VR-specific items were correctly abstracted (test suite must pass / file-size conventions / test-framework attributes / state-storage policies), but the portable commit-message guardrail was lost. Recommend adding it back, e.g. 'respect the repo's established commit-message conventions, file-size conventions, test-framework attributes, and state-storage policies.' No test update needed — existing assertions still hold."] }

## Stage 8 - Fix

{ "summary": "Added 'commit-message conventions' back to RewriteGuidance.cs Rule #5, restoring the portable Conventional Commit guardrail that was dropped when VR-specific idioms were abstracted. All 5 RewriteGuidanceTests pass." }

## Stage 9 - Verify

{
  "summary": "Removed Visual Relay-specific idioms from all system prompts so they are portable across repositories. In RelayStages.cs, dropped the `(e.g. ./visual-relay check)` parenthetical from 4 stage prompts (Author-tests, Implement, Review, Fix-verify). In RewriteGuidance.cs, changed 'Visual Relay task spec' to 'LLM task spec' and replaced Rule #5's VR-repo-specific conventions (./visual-relay check, C#/XAML under 300 lines, [AvaloniaFact], XDG state) with a generic version that names the repo's guardrails (test suite must pass; commit-message, file-size, test-framework, and state-storage policies). RewriteGuidanceTests.cs updated in lockstep. All 2228 tests pass.",
  "commitMessages": [
    "Purge Visual-Relay-specific idioms from system prompts for portability",
    "Remove ./visual-relay check parenthetical from 4 stage prompts and generalize RewriteGuidance rule #5",
    "Expunge VR-specific conventions from LLM-facing prompts; replace with generic guardrails",
    "Generalize RewriteGuidance rule #5 to preserve commit-message conventions alongside test/file/framework/storage guardrails",
    "Strip tool-specific examples from system prompts so they work on any repo"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

