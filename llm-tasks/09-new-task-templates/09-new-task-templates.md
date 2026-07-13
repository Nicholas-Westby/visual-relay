# Task: New-task templates — built-in / user / repo layers, plus a "Create Tasks to Speed Up Automated Tests" starter

The New-task form (`TaskDetailPanel` → "Task title (required)" / "Initial markdown body
(optional)") gets a **Template** dropdown. Picking a template prefills the title and body
fields; the user edits and clicks "Create task" exactly as today. Ships with two built-in
templates: **Blank** (the default — empty title/body) and **Create Tasks to Speed Up
Automated Tests** (full content in §1).

Templates are plain `.md` files resolved from three layers, most local wins:

| Layer | Where | Editable how |
|---|---|---|
| **Built-in** | embedded resources in `VisualRelay.Core` (sources in `packaging/task-templates/`) | ship with the app |
| **User** | `<config dir>/visual-relay/templates/*.md` (config dir = `XdgConfig.ResolveConfigDir`, i.e. `$XDG_CONFIG_HOME` else `~/.config`) | any editor; sandboxed task runs get a write grant (§6) |
| **Repo** | `<workspace>/llm-tasks/templates/*.md` | any editor, including during an active run (see "Why repo templates live under llm-tasks/") |

Naming note: these layer names — **built-in / user / repo templates** — are the canonical
terms; use them in code, docs, and tests. (The request called the middle layer "filesystem
templates"; "user templates" is the better name because all three layers are files — what
distinguishes this one is per-user, per-machine scope.)

Users create entirely new templates by dropping a `.md` file into the user or repo
directory — no management UI. The dropdown re-enumerates on every dialog open, so edits
and new files show up the next time "New" is clicked, with no watchers and no caching.

## Resolution contract (pin these semantics exactly)

- **Template id** = filename stem (`speed-up-automated-tests.md` → id
  `speed-up-automated-tests`). Ids match across layers with `OrdinalIgnoreCase`; on a
  match, repo beats user beats built-in — the winner fully replaces the loser (no field
  merging).
- **Ordering in the dropdown**: the template with id `blank` first (even when overridden),
  then everything else by display `Name` (`OrdinalIgnoreCase`), ties by `Id`.
- **File format**: optional frontmatter. If the first line is exactly `---`, read until
  the next line that is exactly `---`; between them, lines of the form `key: value` are
  parsed and exactly two keys are recognized — `name:` (dropdown label) and `title:`
  (prefill for the Task-title field); unknown keys are ignored. Everything after the
  closing `---` is the body, with leading newlines trimmed. No frontmatter (or an
  unclosed one) → the whole file is the body, `Name` falls back to the id, `Title` to
  empty. Normalize `\r\n` → `\n` before parsing.
- **Bodies must not start with a `# Heading`**: `CreateNewTaskAsync` (in
  `MainWindowViewModel.Authoring.cs`) prepends `# {NewTaskTitle}\n\n` to the body when
  writing the task file — a template that opens with its own H1 would produce a
  double-heading task. Both built-ins comply; document this rule in the README section
  (§9).
- **Robustness**: a template file that cannot be read (IO/permission error) is skipped;
  opening the New-task form must never throw because of a bad template file.
- Missing user/repo directories are fine — built-ins alone are returned. Nothing creates
  the repo directory; the sandbox grant (§6) creates the user directory.

## Why repo templates live under `llm-tasks/templates/`

The tasks dir is already exempt from every "don't touch the repo mid-run" mechanism, all
via prefix checks on the tasks dir, so repo templates inherit editability-during-a-run
with zero new code:

- `RelayDriver.CodeChangeGate.cs` → `IsBookkeepingPath` ends with
  `return IsPathUnderDirectory(rootPath, path, tasksDir);` — task-dir diffs don't count
  as "the run produced code".
- `WorktreeFilter.cs` / `WorktreeResetter.cs` → `IsUnderTasksDir(...)` — task-dir edits
  are kept, never reset to HEAD.
- `GitCommitter.Untracked.cs` → `FindUncommittedAuthoredFilesAsync` skips
  `IsUnderTasksDir(...)` paths — template files never flag as "missed authored files".

Do **not** modify any of those three files; they already do the right thing. The one
place that must learn about the new folder is task discovery (§4), because today *every*
non-skipped subdirectory of `llm-tasks/` is enumerated as a task.

The path is hardcoded as `Path.Combine(rootPath, "llm-tasks", "templates")` — the same
literal `"llm-tasks"` that `RelayTaskWriter.CreateAsync` hardcodes for task creation
(`var tasksDir = Path.Combine(rootPath, "llm-tasks");`). Created tasks always land there
regardless of the `tasksDir` config key, so the templates that seed them sit beside them.
Do not plumb `RelayConfig.TasksDir` into the template loader and do not "fix" the writer's
hardcoding in this task.

## 1) New template source files: `packaging/task-templates/`

Create the directory next to `packaging/nono/`. Two files, exact contents below.

`packaging/task-templates/blank.md`:

```markdown
---
name: Blank
---
```

`packaging/task-templates/speed-up-automated-tests.md` (this content is the product —
keep it verbatim, including the pinned lines the tests in this spec assert on; it is
deliberately stack-agnostic because Visual Relay runs against arbitrary repos, so never
add language-, framework-, or runner-specific instructions to it):

```markdown
---
name: Create Tasks to Speed Up Automated Tests
title: Speed up automated tests
---
Make this repository's automated test suite faster without losing any coverage. This
task has two deliverables:

1. Speed up exactly one automated test — the highest-value target found by the
   measurement step below — and commit that change with before/after timing evidence.
2. Author one follow-up LLM task per remaining opportunity (sibling task folders next to
   this one, following the existing folder-naming convention), so the rest of the suite
   gets faster incrementally. Each follow-up task must be opinionated: exactly one
   prescribed approach, never a menu of options.

## Measure first — every decision is data-informed

- Run the full suite once with this repo's own test command, capturing per-test timings
  from the runner's native timing/report output. Save the timings to a file in this
  task's folder (e.g. timings-baseline.txt), sorted slowest-first. Every slowness claim
  in this task and in every follow-up task must trace to numbers in that file.
- Expect a Pareto shape: a small fraction of tests usually accounts for most of the wall
  time. Rank three views: slowest individual tests, slowest test files/suites, and any
  serial phase the rest of the suite waits on.
- Full-suite timings are contention-inflated. A test that looks slow under a loaded
  worker pool can be fast alone — re-time the top candidates in isolation before
  choosing what to fix.
- Classify the suite: wait-bound (wall time far exceeds CPU time — threads idle in
  sleeps, polling, I/O, or child processes) or CPU-bound. The high-payoff remedies
  differ, and misclassifying wastes the whole effort.

## Coverage is non-negotiable

- Never delete, disable, skip, or weaken a test to make the suite faster. Speed must
  come from doing the same verification cheaper, not from verifying less.
- Any change that moves, merges, or replaces tests must include a written mapping:
  every original test name → where that scenario and its assertions now live. A
  scenario with no destination is lost coverage — do not proceed with that change.
- A test that genuinely must exercise a slow real boundary (real network, real child
  processes, real disk, real time) is still valuable: keep it, move it behind an opt-in
  slow/integration tag, and cover the same logic with a fast in-process equivalent in
  the default suite. Gated is fine; gone is not.
- After each change the whole suite must pass, and if the test count changed, the
  mapping above must account for every removed name.

## Strategies that are known to work (roughly in payoff order)

1. Remove real-time waits. Fixed sleeps and long-interval polling are the classic
   hidden cost. Replace them with waits on the actual condition or event, and route
   deadline/timer logic through an injectable clock so tests drive time instead of
   living through it. If the codebase has many of these, one follow-up task should add
   an automated guard that fails on new unjustified sleeps in tests.
2. Put a seam in front of expensive boundaries. Tests that spawn real child processes
   or hit real network/disk per test are usually the slowest family. Introduce an
   explicit interface at the boundary and use an in-memory fake or simulator in the
   default suite; keep a small tagged set of real-boundary integration tests per the
   coverage rules above.
3. Turn on and tune runner parallelism. Check the runner's parallelism settings first —
   suites often run far below the machine's capacity. Prefer machine-relative worker
   counts (multipliers) over hardcoded numbers so every machine scales. A wait-bound
   suite benefits from oversubscribing workers beyond the core count; a CPU-bound one
   does not.
4. Split oversized test files. Most runners parallelize across files/classes/suites,
   not within them, so one huge file serializes into a long tail. Split it into several
   smaller ones as pure moves — same tests, same assertions — so the scheduler can
   spread the load.
5. Know the parallelism pitfalls before flipping the switch. Tests sharing mutable
   state flake in parallel: global/static singletons, environment variables, current
   working directory, fixed ports, shared temp paths or fixture files, a shared
   database, and order-dependent tests. Fix isolation first (own data, unique
   ports/paths per test), and explicitly serialize the few genuinely serial tests as a
   named group instead of lowering parallelism for everyone. Budget for two companion
   effects: individual tests run slower under contention, so per-test hang/timeout
   ceilings usually must rise together with parallelism; and timing-sensitive
   assertions that pass solo can lose races under load. Prove stability with several
   consecutive green full-suite runs, and fix a flake by removing the shared state or
   serializing that test — never by retrying until green.
6. Merge near-duplicate tests into one data-driven test. When several tests share the
   same arrange/act/assert shape and differ only in inputs, convert them into a single
   parameterized test where each scenario is a data row. This cuts per-test setup
   overhead. Enumerate the originals first and map each one to a row, per the coverage
   rules.
7. Hoist expensive immutable setup. Setup every test rebuilds identically (compiled
   artifacts, seeded stores, parsed fixtures) can be built once at the widest safe
   scope and shared read-only. Never share anything a test mutates.
8. Look for product-side waste. Sometimes the suite is slow because the product wastes
   time on paths tests exercise constantly — e.g. a retry backoff triggered by a
   benign, expected condition. Fixing the product speeds up the suite and the product.

## Investigate beyond this list

The list above is generic. Spend real time in this specific codebase: read the
test-runner configuration and its parallelism/timeout settings, the CI test invocation,
custom fixtures and helpers, and the slowest files from the timings file. Anything
repo-specific you find — an expensive fixture everyone pays for, an artifact rebuilt
per test, a serialized phase — becomes its own follow-up task with its measured cost
cited.

## The one fix to make in this task

From the timings file, pick the single change with the best ratio of measured time
saved to risk, scoped to one test (or the one shared wait/fixture that dominates that
test's time). Implement it, run the full suite to green, and re-measure: record the
before and after numbers for the affected test and for the whole suite next to the
baseline file.

## Follow-up tasks to author

Create one task folder beside this one per remaining opportunity. Each follow-up task
must:

- be self-contained — executable without reading this task;
- prescribe exactly one approach, with the pitfalls above baked in as guardrails;
- cite its target tests' measured baseline numbers from the timings file and state the
  expected saving;
- restate the coverage rules: no deleted/skipped/weakened tests, and a name-by-name
  mapping for any moved or merged test;
- require its commit message to carry the time-saved bullet described below.

## Commit-message evidence (this task and every follow-up)

Every commit that changes test timing must include a bullet quantifying the measured
effect, in this exact shape:

- test time dropped from 80s to 60s, saving 20s

Use real measured numbers and say whether they are full-suite wall time or a single
test's time. This makes the payoff of every change legible straight from the log.
```

## 2) Embed the built-ins in `VisualRelay.Core.csproj`

`src/VisualRelay.Core/VisualRelay.Core.csproj` already embeds the sandbox profile:

```xml
<EmbeddedResource Include="..\..\packaging\nono\vr-guard.json" LogicalName="VisualRelay.Core.vr-guard.json" />
```

Add, in the same `<ItemGroup>`:

```xml
<EmbeddedResource Include="..\..\packaging\task-templates\blank.md" LogicalName="VisualRelay.Core.task-templates.blank.md" />
<EmbeddedResource Include="..\..\packaging\task-templates\speed-up-automated-tests.md" LogicalName="VisualRelay.Core.task-templates.speed-up-automated-tests.md" />
```

## 3) New Core service: `src/VisualRelay.Core/Tasks/TaskTemplates.cs`

Sits beside `RelayTaskWriter.cs`/`RelayTaskRepository.cs`. Public surface (exactly this —
callers are the ViewModel partial in §7, `BuildNonoPrefix` in §6, and tests):

```csharp
using VisualRelay.Core.Configuration;

namespace VisualRelay.Core.Tasks;

/// <summary>Where a resolved template came from. Higher layers override lower
/// ones by id: Repo &gt; User &gt; BuiltIn.</summary>
public enum TaskTemplateSource { BuiltIn, User, Repo }

/// <summary>One new-task template: <paramref name="Name"/> labels the dropdown,
/// <paramref name="Title"/> prefills the task-title field, <paramref name="Body"/>
/// prefills the markdown body.</summary>
public sealed record TaskTemplate(
    string Id, string Name, string Title, string Body, TaskTemplateSource Source);

public static class TaskTemplates
{
    public static string ResolveUserTemplatesDir(IEnvironmentAccessor? accessor = null) =>
        Path.Combine(XdgConfig.ResolveConfigDir(accessor), "visual-relay", "templates");

    public static IReadOnlyList<TaskTemplate> Load(string userTemplatesDir, string repoTemplatesDir);

    internal static TaskTemplate Parse(string id, string content, TaskTemplateSource source);
}
```

Implementation requirements:

- **Built-ins** are read via the `NonoProfileEnsurer.ReadEmbedded` pattern
  (`typeof(TaskTemplates).Assembly.GetManifestResourceStream(name)` + `StreamReader`,
  throw `InvalidOperationException` naming the resource when the stream is null — a
  missing built-in is a packaging bug, not a skippable file). Hardcode the two logical
  names from §2; derive each id by stripping the `VisualRelay.Core.task-templates.`
  prefix and the `.md` suffix.
- **`Load` order of operations**: seed a
  `Dictionary<string, TaskTemplate>(StringComparer.OrdinalIgnoreCase)` with built-ins,
  then overlay the user dir, then the repo dir. For each existing directory, enumerate
  top-level `*.md` files only (no recursion), in `OrdinalIgnoreCase` filename order;
  id = `Path.GetFileNameWithoutExtension`. Wrap each file read in
  `try { … } catch (Exception) { continue; /* unreadable template — skip, never break the dialog */ }`
  (the comment is required: the InspectCode gate flags bare empty catches).
- **Final ordering** (matches the contract):

```csharp
return byId.Values
    .OrderBy(t => string.Equals(t.Id, "blank", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
    .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
    .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
    .ToList();
```

- **`Parse`**: normalize `content.Replace("\r\n", "\n")`, split on `'\n'`. If
  `lines[0] == "---"`, find the next index whose line is exactly `"---"`; when found,
  each line between is checked for the first `':'` — key = trimmed, lowercased left
  part; `"name"` and `"title"` assign the trimmed right part; anything else is ignored.
  Body = the joined lines after the closing `---`, `TrimStart('\n')`. When `lines[0]`
  is not `"---"` or no closing line exists, the whole normalized content is the body.
  Fallbacks: `Name` = id, `Title` = `string.Empty`.

## 4) Task discovery must skip the repo templates folder

`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs` currently treats every non-skipped
subdirectory as a task:

```csharp
private static readonly HashSet<string> SkippedDirectories = ["completed", "_ideation"];
```

Change to:

```csharp
private static readonly HashSet<string> SkippedDirectories = ["completed", "_ideation", "templates"];
```

Without this, `llm-tasks/templates/` would appear in the queue as a task named
`templates`.

## 5) Reserve the folder names in `RelayTaskWriter.ValidateSlug`

A task titled "Templates" slugifies to `templates`; after §4 it would be written to
`llm-tasks/templates/templates.md` and be invisible to the queue. ("Completed" has the
same latent problem today — its collision check only fires once `llm-tasks/completed/`
exists.) `RelayTaskWriter.cs` is at 296/300 lines, so fold the new check into the
existing reserved-prefix block instead of adding a second block. Replace:

```csharp
        // Reserved prefixes — check before case rules since the callers
        // may pass un-normalised slugs.
        if (slug.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ||
            slug.StartsWith("IGNORE-", StringComparison.OrdinalIgnoreCase))
        {
            return $"Slug \"{slug}\" starts with a reserved prefix (DONE-/IGNORE-). Choose a different name.";
        }
```

with:

```csharp
        // Reserved prefixes and folder names — check before case rules since the
        // callers may pass un-normalised slugs. "completed" and "templates" are
        // real folders under llm-tasks/ (the archive / task templates), so a task
        // by either name would be skipped by discovery and invisible in the queue.
        if (slug.StartsWith("DONE-", StringComparison.OrdinalIgnoreCase) ||
            slug.StartsWith("IGNORE-", StringComparison.OrdinalIgnoreCase) ||
            slug is "completed" or "templates")
        {
            return $"Slug \"{slug}\" is reserved (DONE-/IGNORE- prefix, or the completed/templates folder name). Choose a different name.";
        }
```

Net +3 lines → 299/300. Do not add anything else to this file.

## 6) Sandbox write grant for the user templates dir

Sandboxed task runs must be able to create and edit **user** templates (repo templates
are already writable via `--allow-cwd`). In
`src/VisualRelay.Core/Execution/ProcessRunners.cs` → `SwivalSubagentRunner.BuildNonoPrefix`,
extend the signature with a trailing optional parameter and add the grant immediately
after the `SandboxExtraAllowPaths` block:

```csharp
    internal static IReadOnlyList<string> BuildNonoPrefix(
        RelayConfig config, bool rollback, IReadOnlyList<string>? skipDirs = null,
        bool verboseDiagnostics = false, string? userTemplatesDirOverride = null)
```

```csharp
        if (config.SandboxExtraAllowPaths is { Count: > 0 } paths)
        {
            foreach (var path in paths) { args.Add("-a"); args.Add(path); }
        }

        // Standing write grant for the user task-templates dir so sandboxed runs can
        // author and update templates. Created eagerly so the grant always resolves
        // and users can discover the folder. Kilobytes of markdown — negligible
        // against nono's rollback-preflight copy budget.
        var templatesDir = userTemplatesDirOverride ?? TaskTemplates.ResolveUserTemplatesDir();
        Directory.CreateDirectory(templatesDir);
        args.Add("-a");
        args.Add(templatesDir);
```

Notes:

- Add `using VisualRelay.Core.Tasks;` to the file's usings.
- No try/catch around the resolve: `ResolveUserTemplatesDir` can only throw when neither
  `XDG_CONFIG_HOME` nor `HOME` resolves — the very same condition under which the
  adjacent `NonoProfileEnsurer.ResolveProfilePath()` call already fails, so the failure
  mode is unchanged.
- The `userTemplatesDirOverride` parameter exists for tests (deterministic path inside a
  temp dir; no touching the real config dir). Production callers
  (`SwivalSubagentRunner`, `SandboxedTestRunner.ResolveLaunch`) pass nothing.
- This changes the launch argv, so the existing arg-assertion tests will fail until
  updated — that is expected and they must be updated, not weakened (see Tests).
- Do **not** add the path to `packaging/nono/vr-guard.json` instead: the profile's
  `$HOME`-based entries can't express the `XDG_CONFIG_HOME`-first precedence that
  `XdgConfig` gives the rest of the app; the `-a` flag uses the exact directory the
  loader reads.

## 7) ViewModel: new partial `src/VisualRelay.App/ViewModels/MainWindowViewModel.Templates.cs`

`MainWindowViewModel.cs` is at 300/300 lines — do not touch it — and
`MainWindowViewModel.Authoring.cs` is at 294/300, so it gets exactly one new line (below).
Everything else lives in the new partial:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>Display names for the new-task Template dropdown; index-aligned
    /// with <see cref="_newTaskTemplates"/>.</summary>
    public ObservableCollection<string> NewTaskTemplateNames { get; } = [];

    [ObservableProperty]
    private int _selectedNewTaskTemplateIndex = -1;

    private IReadOnlyList<TaskTemplate> _newTaskTemplates = [];

    // What the last applied template put into each field. On template change a
    // field is overwritten only while empty or still equal to this, so browsing
    // templates never clobbers user-typed text.
    private string _lastAppliedTemplateTitle = string.Empty;
    private string _lastAppliedTemplateBody = string.Empty;

    /// <summary>Re-enumerates templates and applies the default (Blank). Called on
    /// every dialog open so template-file edits show up without watchers.</summary>
    private void PrepareNewTaskTemplates()
    {
        _newTaskTemplates = TaskTemplates.Load(
            TaskTemplates.ResolveUserTemplatesDir(EnvironmentAccessor),
            Path.Combine(RootPath, "llm-tasks", "templates"));

        NewTaskTemplateNames.Clear();
        foreach (var template in _newTaskTemplates)
        {
            NewTaskTemplateNames.Add(template.Name);
        }

        _lastAppliedTemplateTitle = string.Empty;
        _lastAppliedTemplateBody = string.Empty;

        // Reset to -1 first so assigning the default index below always fires the
        // change hook, even when the previous dialog session left the same index.
        SelectedNewTaskTemplateIndex = -1;
        SelectedNewTaskTemplateIndex = _newTaskTemplates.Count == 0 ? -1 : IndexOfBlank();
    }

    private int IndexOfBlank()
    {
        for (var i = 0; i < _newTaskTemplates.Count; i++)
        {
            if (string.Equals(_newTaskTemplates[i].Id, "blank", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    partial void OnSelectedNewTaskTemplateIndexChanged(int value)
    {
        if (value < 0 || value >= _newTaskTemplates.Count)
        {
            return;
        }

        var template = _newTaskTemplates[value];
        if (NewTaskTitle.Length == 0
            || string.Equals(NewTaskTitle, _lastAppliedTemplateTitle, StringComparison.Ordinal))
        {
            NewTaskTitle = template.Title;
        }
        if (NewTaskBody.Length == 0
            || string.Equals(NewTaskBody, _lastAppliedTemplateBody, StringComparison.Ordinal))
        {
            NewTaskBody = template.Body;
        }

        _lastAppliedTemplateTitle = template.Title;
        _lastAppliedTemplateBody = template.Body;
    }
}
```

(`EnvironmentAccessor` is the existing `public IEnvironmentAccessor? EnvironmentAccessor
{ get; init; }` the constructor sets — the same seam `UiStateStore.Load(EnvironmentAccessor)`
uses, which is how tests point the user layer into a temp dir.)

The single Authoring.cs line: in `OpenNewTaskDialog()`'s open branch, insert
`PrepareNewTaskTemplates();` so the branch reads:

```csharp
        IsEditingMarkdown = false;
        NewTaskTitle = string.Empty;
        NewTaskBody = string.Empty;
        NewTaskError = null;
        PrepareNewTaskTemplates();
        SelectedTabIndex = 0;
        IsNewTaskDialogOpen = true;
```

(The early-return close branch above it is untouched.) `CanOpenNewTaskDialog()` already
guarantees `Directory.Exists(RootPath)`, and `CreateNewTaskAsync` needs no changes — it
reads `NewTaskTitle`/`NewTaskBody` as today; templates are purely a prefill.

## 8) View: Template dropdown in `TaskDetailPanel.axaml`

In the `<!-- New-task view -->` grid (275/300 lines today), change
`RowDefinitions="Auto,*,Auto"` to `RowDefinitions="Auto,Auto,*,Auto"`, insert the
template row before the title `TextBox`, and shift the existing rows: title gets
`Grid.Row="1"`, the body `TextBox` moves `Grid.Row="1"` → `"2"`, the error `TextBlock`
moves `Grid.Row="2"` → `"3"`. The new row (binding idiom matches the TopBar Run-All
`ComboBox`, but by index — display names are not guaranteed unique across layers):

```xml
                <StackPanel Orientation="Horizontal"
                            Spacing="8"
                            Margin="0,0,0,8">
                  <TextBlock Text="Template:"
                             FontSize="12"
                             Foreground="#9AA3B1"
                             VerticalAlignment="Center"/>
                  <ComboBox ItemsSource="{Binding NewTaskTemplateNames}"
                            SelectedIndex="{Binding SelectedNewTaskTemplateIndex}"
                            MinWidth="260"
                            VerticalAlignment="Center"
                            ToolTip.Tip="Prefill from a template. Add your own in ~/.config/visual-relay/templates or llm-tasks/templates."/>
                </StackPanel>
```

Nothing else in the file changes (the read-only view, edit view, toolbars, and other
tabs are untouched).

## 9) README

In `README.md`, insert a new top-level section between the end of the
`# What Visual Relay Does` section and the `# Tests` heading:

```markdown
# Task Templates

The New-task form offers a template dropdown. Templates are markdown files with an
optional `---` frontmatter block (`name:` labels the dropdown, `title:` prefills the
task title); the rest of the file prefills the body. Don't start a template body with
a `# Heading` — task creation prepends one from the title.

Three layers, most local wins (matched by filename): built-in templates ship with the
app; user templates live in `~/.config/visual-relay/templates/` (respects
`XDG_CONFIG_HOME`; sandboxed task runs may write here too); repo templates live in
`llm-tasks/templates/` and can be edited even during an active run, like task specs.
Drop a new `.md` file in either folder — the dropdown re-reads them each time the
form opens.
```

## Tests

New files are standalone or join existing partial families as noted; every test uses
temp dirs (`TestRepository` already redirects `XDG_CONFIG_HOME` at `repo.Root`, so the
user layer resolves to `<repo.Root>/visual-relay/templates` — never touch the real
`~/.config`).

**New `tests/VisualRelay.Tests/TaskTemplatesTests.cs`** (sealed class; plain `[Fact]`s;
build user/repo dirs with `Directory.CreateTempSubdirectory` and delete in `finally`):

- `Parse_Frontmatter_ExtractsNameTitleAndBody` — input
  `"---\nname: Fancy\ntitle: Do it\n---\nBody line.\n"` → Name `Fancy`, Title `Do it`,
  Body `"Body line.\n"`.
- `Parse_NoFrontmatter_WholeContentIsBody_NameFallsBackToId` — Title empty, Name == id.
- `Parse_CrLf_NormalizedBeforeParsing` — same input as the first fact with `\r\n`
  line ends parses identically.
- `Parse_UnknownFrontmatterKeys_Ignored`.
- `Parse_UnclosedFrontmatter_TreatedAsBody` — `"---\nname: X\nno close"` → Body is the
  whole (normalized) content, Name == id.
- `Load_BuiltIns_BlankFirstThenSpeedUp` — `Load` with two nonexistent dirs returns
  exactly two templates: `[0]` Id `blank`, Name `Blank`, empty Title/Body, Source
  `BuiltIn`; `[1]` Id `speed-up-automated-tests`, Name
  `Create Tasks to Speed Up Automated Tests`, Title `Speed up automated tests`.
- `Load_UserOverridesBuiltIn_RepoOverridesUser` — write `blank.md` in the user dir
  (body `USER`) and in the repo dir (body `REPO`): winner has Source `Repo`, Body
  starts with `REPO`; delete the repo copy and reload: Source `User`. Blank stays
  index 0 both times.
- `Load_NewIdsFromBothLayers_AppearSortedByName`.
- `Load_UnreadableEntrySkipped` — create a *directory* named `broken.md` inside the
  user dir (read throws), plus one good template: the good one loads, no exception.
- `SpeedUpTemplate_PinsLoadBearingContent` — the built-in speed-up Body
  `Contains("- test time dropped from 80s to 60s, saving 20s")`,
  `Contains("Never delete, disable, skip, or weaken a test")`, and
  `DoesNotContain("dotnet")` (guards the stack-agnostic rule).

**New `tests/VisualRelay.Tests/NewTaskAuthoringTests.Templates.cs`** (new partial of the
existing `public sealed partial class NewTaskAuthoringTests`; `[AvaloniaFact]`s using the
`TestRepository.Create()` + `new MainWindowViewModel(repo.Env) { RootPath = repo.Root }`
+ `LoadInitialAsync` + `OpenNewTaskDialogCommand.Execute(null)` +
`Dispatcher.UIThread.RunJobs()` idiom from `NewTaskAuthoringTests.Create.cs`):

- `OpenNewTaskDialog_ListsBuiltInsWithBlankSelected` — `NewTaskTemplateNames` is
  `["Blank", "Create Tasks to Speed Up Automated Tests"]`, selected index is the Blank
  index, title/body empty.
- `SelectingSpeedUpTemplate_PrefillsTitleAndBody` — select the speed-up index →
  `NewTaskTitle == "Speed up automated tests"`, body contains the commit-bullet pin
  line; re-select Blank → both fields empty again (untouched fields follow templates).
- `TemplateChange_NeverClobbersUserEditedField` — type a custom body, then select
  speed-up: body keeps the custom text, title gets the template title.
- `RepoTemplate_OverridesBuiltInBlank` — before opening the dialog write
  `<repo.Root>/llm-tasks/templates/blank.md` containing
  `"---\nname: Blank\n---\nrepo skeleton\n"`; open → Blank is still first and default,
  and `NewTaskBody` is `"repo skeleton\n"`.
- `UserTemplate_AppearsInDropdown` — write
  `<repo.Root>/visual-relay/templates/deploy-checklist.md` (the redirected user layer)
  with `name: Deploy Checklist`; open → names contain it, sorted after Blank
  alphabetically; selecting it prefills its body.

**New `tests/VisualRelay.Tests/RelayTaskRepositoryTemplatesDirTests.cs`** (standalone
sealed class — `RelayTaskRepositoryTests.cs` is at 300/300; mirror its first fact's
idiom):

- `ListPendingAsync_SkipsTemplatesDirectory` — `repo.WriteTask("alpha", "# Alpha\n")` +
  `repo.WriteTask("templates/skeleton", "# Skeleton\n")` →
  `ListPendingAsync` ids == `["alpha"]`.

**Existing `tests/VisualRelay.Tests/RelayTaskWriterTests.cs`** (257/300 — room): add
`ValidateSlug_RejectsReservedFolderNames` — `ValidateSlug("templates")` and
`ValidateSlug("completed")` both return an error mentioning `reserved`;
`ValidateSlug("template")` (singular) still passes.

**New `tests/VisualRelay.Tests/SwivalSubagentRunnerSandboxTests.TemplatesGrant.cs`**
(new partial of the existing sandbox-args family):

- `BuildNonoPrefix_GrantsUserTemplatesDir` — call with
  `userTemplatesDirOverride: <tempdir>/templates` → the args contain the adjacent pair
  `-a`, `<tempdir>/templates` after any `SandboxExtraAllowPaths` pairs, and the
  directory now exists on disk.

**Existing arg-assertion updates** (expected fallout of §6 — update expectations, do not
delete or weaken facts; pass `userTemplatesDirOverride` wherever a deterministic argv is
asserted): `SwivalSubagentRunnerSandboxTests.cs`,
`SwivalSubagentRunnerSandboxTests.SkipDirs.cs`, `SandboxExtraAllowPathsConfigTests.cs`,
`SandboxDiagnosticsToggleTests.cs`, `NonoLaunchDriftGuardTests.cs` (this one exists
precisely to force a deliberate update on launch-argv changes — update its pinned
expectation to include the new grant), and `SandboxedTestRunnerArgumentTests.cs`
(`ResolveLaunch` passes no override, so those facts must assert the pair as `-a`
followed by `TaskTemplates.ResolveUserTemplatesDir()` computed inside the test — same
process env, therefore the same value). All six have ≥30 lines of headroom.

## Verification

1. `dotnet build` — clean.
2. Targeted suites:
   `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --filter "FullyQualifiedName~TaskTemplatesTests|FullyQualifiedName~NewTaskAuthoringTests|FullyQualifiedName~RelayTaskRepositoryTemplatesDirTests|FullyQualifiedName~RelayTaskRepositoryTests|FullyQualifiedName~RelayTaskWriterTests|FullyQualifiedName~SwivalSubagentRunnerSandboxTests|FullyQualifiedName~SandboxExtraAllowPathsConfigTests|FullyQualifiedName~SandboxDiagnosticsToggleTests|FullyQualifiedName~NonoLaunchDriftGuardTests|FullyQualifiedName~SandboxedTestRunnerArgumentTests"`
   — all green.
3. Full suite green.
4. `./visual-relay check` passes — in particular the file-size guard (budgets below) and
   the InspectCode gate: this task's diff must introduce **zero** new findings (no bare
   empty catch blocks, no redundant usings, no dead locals).

File-size budgets (guard limit 300; verify with `wc -l` after editing):

| File | Before | After (max) |
|---|---|---|
| `MainWindowViewModel.cs` | 300 | 300 — **do not touch** |
| `MainWindowViewModel.Authoring.cs` | 294 | 295 (exactly the one line) |
| `MainWindowViewModel.Templates.cs` | new | ≤ 300 |
| `TaskDetailPanel.axaml` | 275 | ≤ 292 |
| `RelayTaskWriter.cs` | 296 | 299 |
| `RelayTaskRepository.cs` | 291 | 291 (edit one line in place) |
| `ProcessRunners.cs` | 184 | ≤ 200 |
| `TaskTemplates.cs` | new | ≤ 300 |

## Rejected approaches (do not do these)

- **Unconditional overwrite of the fields on template select** — browsing templates
  would destroy typed text; the last-applied comparison in §7 is the required rule.
- **A config key / `RelayConfig.TasksDir` plumbing for template locations** — fixed
  well-known paths are the feature; configurability adds surface with no user.
- **Repo templates under `.relay/`** — mechanically possible (also bookkeeping-exempt)
  but that tree is machine-written run artifacts and logs; authored, reviewable content
  belongs beside the tasks it seeds.
- **Template-management UI, file watchers, or caching** — files are the interface;
  re-enumerate per dialog open.
- **A control-API endpoint for templates** — the API's `new-task` action only opens the
  dialog today; keep it that way.
- **A YAML library for frontmatter** — two recognized keys, hand-parsed, like the
  repo's other line-based config parsing.
- **Embedding built-ins in `VisualRelay.App`** — the App only carries `AvaloniaResource`
  assets; the embedded-text precedent (`vr-guard.json` + `NonoProfileEnsurer`) lives in
  Core, and Core ownership keeps `TaskTemplates` testable without Avalonia.
- **Binding the dropdown by `SelectedItem` on names** — display names can collide
  across layers; index binding is collision-proof.
- **Granting the user templates dir via `vr-guard.json`** — see §6.
- **Any language/framework/runner-specific advice inside the speed-up template** —
  Visual Relay is general-purpose; the `DoesNotContain("dotnet")` pin guards this.
- **Auto-numbering (`NN-`) machinery** — task ordering is the user's convention plus
  `.relay/task-order.json`; templates don't touch it.

## Constraints

- Touch only: `packaging/task-templates/` (new), `VisualRelay.Core.csproj`,
  `TaskTemplates.cs` (new), `RelayTaskRepository.cs` (one line),
  `RelayTaskWriter.cs` (one block), `ProcessRunners.cs` (§6),
  `MainWindowViewModel.Templates.cs` (new), `MainWindowViewModel.Authoring.cs` (one
  line), `TaskDetailPanel.axaml` (§8), `README.md` (§9), and the test files named
  above.
- Do not modify `CreateNewTaskAsync`, `RelayTaskWriter.CreateAsync`,
  `RelayDriver.CodeChangeGate.cs`, `WorktreeFilter.cs`, `WorktreeResetter.cs`,
  `GitCommitter.Untracked.cs`, or `packaging/nono/vr-guard.json`.
- Do not create `llm-tasks/templates/` content in this repo as part of this task — the
  built-ins are the shipped templates; the repo layer stays empty until someone adds to
  it.
- Conventional Commits; minimal diffs; keep every file inside its budget above.
