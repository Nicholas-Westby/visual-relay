## Stage 1 - Ideate

{
  "summary": "Update README.md to use `git clone --depth 1` in both the macOS and Windows install sections, add a one-line shallow-clone trade-off note in each section, pin the change with a new `[Fact]` in Installer5DocsTests.cs, and commit with a `docs:` prefix. Options differ on edit strategy (two inline edits vs. one block edit) and test style (two explicit assertions vs. a future-proofed helper).",
  "options": [
    "A — Direct Red/Green with two independent section edits and a two-assertion [Fact]",
    "B — Single anchored replacement of the entire install-section block with a combined test",
    "C — Invariant-based test helper that asserts --depth on all # Install sections, plus two targeted edits"
  ]
}

## Stage 2 - Research

{
  "findings": "README.md has two install sections (macOS lines 14–32, Windows lines 33–47) each doing a full `git clone`. Installer5DocsTests.cs has 15 existing [Fact] tests, 6 of which cover install sections and all must keep passing. The ExtractSection helper extracts from heading to next \\n# heading. FileSizeGuard only checks *.cs/*.axaml under src/tests/tools. Commit messages require docs: prefix, ≤72 chars, lowercase, no trailing period, no em dashes. No changes outside README.md and Installer5DocsTests.cs.",
  "constraints": [
    "Both clone lines must become `git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git`",
    "Each section must get a one-line shallow-clone trade-off note explaining --depth 1 fetches only the latest commit and can be omitted for full history",
    "A new [Fact] must assert `--depth` appears in both `# Install (macOS)` and `# Install (Windows)` sections",
    "All 6 existing install-related [Fact] assertions must still pass unchanged",
    "All 15 existing [Fact] tests in Installer5DocsTests.cs must pass",
    "`./visual-relay test` (dotnet test) must be green",
    "`./visual-relay check` must pass",
    "Commit message: `docs: recommend shallow clone in install steps` (or similar, matching docs: prefix, ≤72 chars, lowercase, no trailing period, no em dashes)",
    "Only README.md and tests/VisualRelay.Tests/Installer5DocsTests.cs may be modified",
    "The <!-- BEGIN install section ... --> / <!-- END install section --> comment markers must not be altered",
    "Nix-related prose in macOS section and 'installed globally' prose in Windows section must be preserved",
    "Old_string in edit_file must match the exact current text (checksum-aware) to avoid mismatches"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Red→Green TDD cycle executed: (1) Added [Fact] Readme_InstallSections_RecommendShallowClone asserting --depth in both install sections. (2) Changed both git clone lines in README.md to git clone --depth 1 and added a one-line shallow-clone note in each section. All 15 existing tests plus the new test pass; BEGIN/END markers and all content outside the install-section block are unaltered.",
  "excerpts": [
    "README.md:23: git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git",
    "README.md:28-29: `--depth 1` does a shallow clone (latest commit only) for a faster, smaller download; omit it to fetch the full history.",
    "README.md:42: git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git",
    "README.md:47-48: `--depth 1` does a shallow clone (latest commit only) for a faster, smaller download; omit it to fetch the full history.",
    "Installer5DocsTests.cs:108-117: [Fact] Readme_InstallSections_RecommendShallowClone() asserts --depth in both ExtractSection calls"
  ],
  "repro": "dotnet test --filter Installer5DocsTests"
}

## Stage 4 - Plan

{
  "plan": "## Concrete plan\n\n### Step 1 — Red: add the shallow-clone guard test\n\nIn `tests/VisualRelay.Tests/Installer5DocsTests.cs`, add one `[Fact]` named\n`Readme_InstallSections_RecommendShallowClone` between the existing\n`Readme_WindowsInstallSection_DocumentsGlobalInstall` (line 106) and the\n`\\n── README: sample-reset / dev-only references removed` divider (line 120).\n\nThe test body reads the full README once via `ReadReadme()`, extracts both the\nmacOS and Windows install sections with `ExtractSection`, and asserts that each\nsection contains the literal string `--depth`:\n\n```csharp\n[Fact]\npublic void Readme_InstallSections_RecommendShallowClone()\n{\n    var content = ReadReadme();\n    var macSection = ExtractSection(content, \"# Install (macOS)\");\n    var winSection = ExtractSection(content, \"# Install (Windows)\");\n\n    // Both install sections should recommend --depth 1 for faster clones.\n    Assert.Contains(\"--depth\", macSection, StringComparison.Ordinal);\n    Assert.Contains(\"--depth\", winSection, StringComparison.Ordinal);\n}\n```\n\nRun the test suite to confirm this test fails (the current README has plain\n`git clone` without `--depth`):\n\n```bash\ndotnet test --filter Installer5DocsTests\n```\n\n### Step 2 — Green: update README.md install sections\n\nEdit `README.md` inside the `<!-- BEGIN install section … -->` /\n`<!-- END install section -->` block (lines 16–52). Two changes:\n\n**macOS** (`# Install (macOS)` block, ~line 23):\n- Change the clone line in the fenced code block from\n  `git clone https://github.com/Nicholas-Westby/visual-relay.git`\n  to\n  `git clone --depth 1 https://github.com/Nicholas-Westby/visual-relay.git`.\n- After the closing triple-backtick fence of that code block, add a one-line\n  note explaining the trade-off:\n  `` `--depth 1` does a shallow clone (latest commit only) for a faster, smaller download; omit it to fetch the full history. ``\n\n**Windows** (`# Install (Windows)` block, ~line 42):\n- Same clone-line change: insert `--depth 1` before the URL.\n- Same one-line shallow-clone note after its closing fence.\n\nBoth changes stay within the section boundaries. The `BEGIN`/`END` comment\nmarkers, the Nix prose in the macOS section, and the \"installed globally\" prose\nin the Windows section are untouched.\n\n### Step 3 — verify\n\nRun the full Installer5DocsTests suite. All 15 existing assertions must still\npass, and the new `Readme_InstallSections_RecommendShallowClone` must now pass:\n\n```bash\ndotnet test --filter Installer5DocsTests\n```\n\nThen run the broader check suite (file-size guard, format verification, build,\ntest suite, README screenshot render):\n\n```bash\n./visual-relay check\n```\n\n### Step 4 — commit\n\nCommit with a Conventional-Commit message:\n\n```\ndocs: recommend shallow clone in install steps\n```\n\nThis satisfies: `docs:` type, lowercase description, ≤72 characters, no\ntrailing period, no em dashes. The commit changes only `README.md` and\n`tests/VisualRelay.Tests/Installer5DocsTests.cs`.",
  "manifest": [
    "README.md",
    "tests/VisualRelay.Tests/Installer5DocsTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/Installer5DocsTests.cs"],
  "rationale": "TDD cycle complete: added Readme_InstallSections_RecommendShallowClone [Fact] asserting --depth in both install sections (red), then updated README.md macOS and Windows install sections to use git clone --depth 1 with a one-line trade-off note (green). All 15 tests pass."
}

## Stage 6 - Implement

{"summary": "Updated README.md: both # Install (macOS) and # Install (Windows) sections now use `git clone --depth 1 …` with a one-line note explaining the shallow-clone trade-off. The new `Readme_InstallSections_RecommendShallowClone` [Fact] passes alongside all 14 existing Installer5DocsTests. Full dotnet test suite: 2588 passed, 0 failed, 22 skipped (platform-specific). Only README.md and Installer5DocsTests.cs were modified."}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All changes already in place: README.md has `git clone --depth 1` with a one-line shallow-clone note in both macOS and Windows install sections. The new `Readme_InstallSections_RecommendShallowClone` [Fact] in Installer5DocsTests.cs passes. Full suite: 2588 passed, 0 failed, 22 skipped." }

## Stage 9 - Verify

{
  "summary": "Completed docs-only change: both Install (macOS) and Install (Windows) sections in README.md now use `git clone --depth 1 …` with a one-line shallow-clone trade-off note. A new `[Fact]` in Installer5DocsTests.cs pins the `--depth` flag in both sections. All 2588 tests pass (0 failed, 22 skipped).",
  "commitMessages": [
    "docs: recommend shallow clone in install steps",
    "docs: use --depth 1 for faster git clone in install docs",
    "docs: add shallow-clone note to install instructions",
    "docs: switch install sections to shallow clones for speed",
    "docs: speed up install steps with depth-limited clone"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

