# Harness: validate that manifest entries exist on disk at Plan acceptance

On task `10-adopt-inspectcode-standards-repo-wide` (2026-06-13), the stage-4 Plan manifest
contained paths like `src/MainWindowViewModel.cs` that do not exist in the target repo.
Stage-5 and stage-6 agents consumed the manifest at face value and wasted multiple turns
calling `list_files` to recover the correct paths. The error surfaced only inside the
agent's reasoning loop — the harness itself accepted the manifest silently.

## Current state (researched)

### Existing validation: gitignore check

`src/VisualRelay.Core/Execution/ProcessRunners.ManifestValidation.cs:14`,
`CheckManifestAgainstGitignoreAsync`: runs `git check-ignore` on manifest paths at stage-4
acceptance. Returns a corrective error string (triggering a contract retry) when any path
is gitignored. This is the direct extension point: existence validation belongs alongside
the gitignore check, not as a separate mechanism.

The method is called from the contract-retry loop inside `SwivalSubagentRunner` (`RunAsync`).
The gitignore check already follows the corrective-retry contract: return null on success,
return an error string on failure, and the retry machinery handles the rest.

### Stage-10 amendManifest also needs the check

The same method checks `amendManifest` (stage-10, `stageNumber == 10`, key `"amendManifest"`)
in addition to `manifest` (stage 4). Any existence check added for stage 4 must also apply
to stage-10 amendManifest entries.

### Files the plan intends to CREATE vs. files that must already EXIST

A plan manifest legitimately lists files-to-be-created (new source files, new test files).
These do not exist on disk yet and must not be rejected. A clean escape hatch is required.

Two options:
1. **`+`-prefix convention**: any entry beginning with `+` signals "new file; do not check
   existence." The driver strips the prefix when writing `manifest.txt`.
2. **Separate `newFiles` key**: the Plan contract gains an optional `"newFiles": string[]`
   field for files to be created, separate from `"manifest"` (existing files to edit).

Option 1 is simpler and requires no contract schema change — the existing `"manifest"`
field accommodates both. Option 2 is semantically cleaner but requires updating the stage-4
contract string in `RelayStages.cs` and every agent that produces it. Prefer option 1 for
minimal blast radius; document the convention in the system prompt or contract hint.

### Overlap with existing DONE tasks

`DONE-validate-manifest-against-gitignore-at-acceptance.md` introduced
`CheckManifestAgainstGitignoreAsync`. This task extends that method to also check existence —
it does NOT duplicate the gitignore check. Both checks run in the same method on the same
list of paths.

## What to build

Write the failing tests first. All changes are in the VR harness; no language assumptions.

### 1. Extend `CheckManifestAgainstGitignoreAsync` with an existence check

In `src/VisualRelay.Core/Execution/ProcessRunners.ManifestValidation.cs`, after extracting
the path list and before calling `git check-ignore`:

```csharp
// Existence check: manifest entries for existing files must be present on disk.
// Entries prefixed with '+' signal "new file to be created" and are exempt.
var existingEntries = paths.Where(p => !p.StartsWith("+", StringComparison.Ordinal)).ToList();
var newEntries = paths.Where(p => p.StartsWith("+", StringComparison.Ordinal)).ToList();

var missing = existingEntries
    .Where(p => !File.Exists(Path.Combine(targetRoot, p)))
    .ToList();

if (missing.Count > 0)
{
    var quoted = string.Join(", ", missing.Select(p => $"`{p}`"));
    var plural = missing.Count == 1;
    return plural
        ? $"manifest rejected: {quoted} does not exist in the target repo. " +
          "Verify the exact path with list_files or find before including it. " +
          "If this is a NEW file to be created, prefix it with '+' (e.g. '+src/NewFile.cs')."
        : $"manifest rejected: {quoted} do not exist in the target repo. " +
          "Verify the exact paths with list_files or find before including them. " +
          "If any are NEW files to be created, prefix them with '+'.";
}
```

Continue to the gitignore check for `existingEntries` (skip `newEntries` — they won't be
found by `git check-ignore` and don't need the check). The two checks compose: a path that
doesn't exist AND would be gitignored returns the existence error first (it's the more
actionable signal).

Strip the `+` prefix when writing `manifest.txt` (in `RelayDriver.cs:133`,
`WriteManifestAsync`): the driver already holds the clean list after parsing. The `+`
prefix is only meaningful in the agent's JSON output, not in the persisted manifest.

### 2. Update the stage-4 system prompt / contract hint

In `src/VisualRelay.Core/Execution/RelayStages.cs:44`, the `"Plan"` system prompt currently
reads:

```
"Write a concrete plan and exact impacted code and test files. The manifest must list
only code files — never files under the tasks directory (e.g. llm-tasks/)."
```

Extend it to explain the `+` prefix:

```
"Write a concrete plan and exact impacted code and test files. The manifest must list
only code files — never files under the tasks directory (e.g. llm-tasks/). For files
that already exist, use their exact repo-relative path. For files that do not yet exist
and will be created, prefix the path with '+' (e.g. '+src/NewFeature.cs')."
```

The contract hint (fourth `Stage(...)` argument) for stage 4 in `RelayStages.cs:12` is
`{ "plan": string, "manifest": string[] }` — no change needed; the `+` prefix is a
value-level convention, not a schema change.

### 3. Tests (TDD — write first)

All in `tests/VisualRelay.Tests/`. The existing test class for manifest validation is
`CommitTestRunners.GitignoredManifest.cs`. Add a new file or extend:

**`SwivalSubagentRunnerManifestExistenceTests.cs`** (or extend existing manifest tests):

- `ManifestExistenceCheck_ExistingFilesPresent_ReturnsNull` — all manifest entries have
  corresponding files on disk → null (no error).
- `ManifestExistenceCheck_MissingFile_ReturnsCorrectionMessage` — one path absent from
  disk → error string naming the missing path.
- `ManifestExistenceCheck_NewFilePrefixed_IsExempt` — entry `+src/NewFile.cs` has no
  file on disk → no error (new-file convention respected).
- `ManifestExistenceCheck_MixedExistingAndMissing_ReportsOnlyMissing` — two present, one
  absent (no `+`) → error names only the absent one.
- `ManifestExistenceCheck_MissingAndGitignored_ReturnsExistenceErrorFirst` — path that is
  both absent and would be gitignored → existence error returned (gitignore check short-
  circuits).
- `ManifestExistenceCheck_AmendManifest_Stage10_SameCheck` — stage 10 with `amendManifest`
  containing a missing path → same corrective error.

**Driver integration** (extend `RelayDriverGitCommitTests.GitignoredBackstop.cs` or add a
new partial):

- `Stage4_ManifestWithMissingFile_TriggersContractRetry` — via `ScriptedSubagentRunner`,
  first stage-4 attempt returns a manifest with a missing path; the runner sees a retry
  request with the corrective message; second attempt returns a correct manifest.

## Done when

- **Missing-path rejection fires at stage 4:** a manifest entry for a non-existent file
  triggers a corrective contract retry with a message naming the missing path. Asserted by
  `SwivalSubagentRunnerManifestExistenceTests` and the driver integration test.
- **New-file prefix exempt:** a `+`-prefixed entry for a file that does not exist yet is
  accepted; the `+` is stripped before `manifest.txt` is written. Asserted by unit tests.
- **Gitignore check still runs:** the existing gitignore check is unchanged and its tests
  continue to pass; existence and gitignore checks compose (existence first).
- **Stage-10 amendManifest also checked:** existence validation applies to `amendManifest`
  entries at stage 10.
- **System prompt updated:** the Plan stage system prompt explains the `+` convention.
- **`./visual-relay check` green** after all changes.
- **Files under 300 lines each:**
  - `src/VisualRelay.Core/Execution/ProcessRunners.ManifestValidation.cs` (extended by ~25 lines; currently 56 lines)
  - `src/VisualRelay.Core/Execution/RelayStages.cs` (system prompt string extended by ~2 lines)
  - `tests/VisualRelay.Tests/SwivalSubagentRunnerManifestExistenceTests.cs` (new, <120 lines)
- **Conventional Commit subject candidates:**
  - `feat(harness): reject manifest entries for non-existent files at Plan acceptance`
  - `fix(manifest): validate path existence at stage-4 acceptance, allow + prefix for new files`
