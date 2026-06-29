# Dev-gate: detect dead config fields (parsed-but-never-consumed) that InspectCode can't see

## Background — what the research found (don't re-litigate this)
This started as "surely some automation can detect unused code" (prompted by `MaxStageFailures`
being defined-but-unused). Evidence-based investigation **corrected the premise**:

- **General dead-code detection already exists and already works.** ReSharper InspectCode's
  unused family (`UnusedMember.Global`, `UnusedType.Global`, `NotAccessedPositionalProperty.Global`)
  is **already active** in the `./visual-relay check` gate (`tools/VisualRelay.Cli/Gates/InspectCodeGate.cs:29-37`
  runs `dotnet jb inspectcode … --severity=SUGGESTION --format=Sarif`; `InspectCodeSarifParser`
  fails the build on any result). It was proven to fire at the gate's floor: un-suppressing two
  genuinely-dead members (`RetirementResult.WasRetired`, public `RelayQueueController.MoveUp`) made
  InspectCode flag both immediately. The repo stays at **0 findings** today by *inline-suppressing*
  the handful of intentional cases (e.g. `RelayEvent.cs`, `RetirementResult.cs`, `HookInstaller.cs`,
  `RunMetrics.cs`, `RelayQueueController.cs`). **Do not add a second general unused-symbol scanner —
  it would be redundant.**
- **Why `MaxStageFailures` slips through (the one real gap):** it is **not** literally never read.
  `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:207` reads it self-referentially as its
  own fallback default:
  ```csharp
  MaxStageFailures = OptionalInt(root, "maxStageFailures", defaults.MaxStageFailures),
  ```
  That `defaults.MaxStageFailures` getter access counts as a use, so `NotAccessedPositionalProperty.Global`
  **correctly** stays silent. (Proven: deleting only that one read in a throwaway worktree made
  InspectCode immediately flag `RelayConfig.MaxStageFailures … is never accessed`.)
- **This is systemic, not a one-off.** Every field in `RelayConfigLoader` is loaded via
  `OptionalX(root, "key", defaults.<Field>)` — **~23 `defaults.<Field>` self-reads** — so **every**
  `RelayConfig` positional property has a phantom self-read and the **entire record is immune** to
  InspectCode's unused-positional check. A config field can be parsed from JSON and consumed nowhere
  in the engine, and nothing will ever flag it.

## The gap to close
Detect **config fields that are loaded but never actually consumed** — i.e. a `RelayConfig`
property whose only references are (a) the record/property declaration and (b) the
`defaults.<Name>` self-default in `RelayConfigLoader`, with **no real `.<Name>` consumer anywhere
in `src/`**. Today the live instance of this is exactly `MaxStageFailures` (default 3,
`src/VisualRelay.Domain/RelayConfig.cs:10`).

## What to do (primary recommendation — a small Roslyn guard)
A semantic, config-scoped guard is the right tool because the defeating factor is semantic (a
self-referential read) that generic unused-symbol tools cannot see through. It fits the repo's
existing precedent — there are already several hand-rolled guards in `tools/VisualRelay.Guards/*Guard.cs`
(e.g. `RealBuildSubprocessGuard`, `SyncOverAsyncGuard`), each wired into the dev gate and backed by
a guard-as-test.

- **Rule:** for each `RelayConfig` positional property, count references in `src/`, **excluding**
  (i) the record declaration and (ii) the single `defaults.<Name>` self-default in
  `RelayConfigLoader`. If the remaining consumer count is **0**, flag it as a dead config field.
- **Prefer keying on the *pattern*, not a hard-coded type name**, so it generalizes: identify the
  config record(s) populated by the loader's `OptionalX(root, "<key>", defaults.<Field>)` shape and
  treat that self-default read as a non-consumer. (Scoping to `RelayConfig` only is acceptable as a
  first cut, but the pattern-based form covers any future config record loaded the same way and
  keeps it general rather than a `MaxStageFailures` special-case.)
- **Integrate where it makes sense:** wire it into `./visual-relay check` via the existing
  `GuardRunner` in `tools/VisualRelay.Cli/Commands/CheckCommand.cs` (`RunAsync`), alongside the
  other guards, **and** add a guard-as-test mirroring the existing ones so it runs in the suite too.
- **Measured initial result: exactly 1 finding — `MaxStageFailures`. Zero false positives** across
  all 25 current `RelayConfig` fields (every other field has at least one genuine `.Field` consumer
  in `src/`). So landing this guard will require either removing `MaxStageFailures` or genuinely
  consuming it (see cross-task note).

## Complementary alternative (call out, don't necessarily do both)
Refactor `RelayConfigLoader` so `OptionalX` no longer passes `defaults.<Field>` as the fallback
(e.g. hold defaults as plain constants or a separate defaults source the getter doesn't touch).
Then InspectCode's **already-active** `NotAccessedPositionalProperty.Global` would catch dead config
fields **with no custom guard at all**. Trade-off: it touches a deliberate, pervasive pattern across
all ~23 fields, so it's a larger, riskier change than the scoped guard. Reasonable to do the guard
now and note this refactor as the "no custom code" path if the loader is ever reworked.

## False-positive surface (already well-handled — preserve it)
The InspectCode unused family runs **clean at 0 today with all of VR's dynamic patterns present**,
which is itself the proof it handles them. The proposed guard is *config-scoped* and structurally
cannot touch these — but don't regress them when wiring:
- **Avalonia compiled XAML bindings** — XAML-bound `[ObservableProperty]`/`[RelayCommand]`/VM members
  are seen as used via bindings; there is **no** `.editorconfig` carve-out silencing
  `UnusedMember`/`NotAccessedPositionalProperty`/`UnusedType`, so they are genuinely covered, not hidden.
- **ViewLocator / DI / runtime-instantiated types** — handled by a narrow
  `resharper_class_never_instantiated_global_highlighting = none` carve-out (`.editorconfig`), with
  `UnusedType.Global` still active for never-*referenced* types.
- **Source generators** (`[ObservableProperty]`) and **xUnit `[Fact]` entry points** — already treated
  as live.
The config-field guard sees a config property read through a binding as a non-issue (it's not loaded
via the `defaults.<Field>` shape), giving exactly the desired asymmetry: catch dead config fields,
never touch XAML/VM/DI members.

## Cross-task note (ordering with the escalation task)
`harness-stage-escalation-tier-and-turns` proposes using `MaxStageFailures` as the home for its
"3 runs / max-escalations" cap. If that task lands **first**, `MaxStageFailures` becomes genuinely
consumed and this guard will (correctly) report **0** dead config fields — that's fine: the guard
still exists to catch the *next* one. Don't treat "0 findings after escalation lands" as the guard
being broken; add a unit test that synthesizes a deliberately-dead config field to prove the guard
still fires.

## General-purpose note
Unlike the relay **engine** (which must stay free of VR-specifics so it works on arbitrary user
codebases), this is a **dev-gate guard over VR's OWN source**, exactly like the existing
`tools/VisualRelay.Guards/*Guard.cs`. VR-specific knowledge (the `RelayConfig`/`RelayConfigLoader`
shape) is appropriate here. Keep it out of anything that runs against user repos.

## Done when
- `./visual-relay check` (and the suite) include a guard that flags config fields loaded but consumed
  nowhere in `src/`, treating the `defaults.<Name>` self-default as a non-consumer.
- Running it on the current tree reports `MaxStageFailures` (unless escalation has since consumed it),
  with **zero false positives** on the other `RelayConfig` fields and on XAML/VM/DI/source-generated
  members.
- A unit test synthesizes a dead config field and asserts the guard fires (so it survives
  `MaxStageFailures` later becoming used).
- The complementary `RelayConfigLoader` self-default refactor is documented as the alternative
  "no custom code" path (implement it instead of the guard only if you also rework the loader).
- `./visual-relay check` green; suite green under nono; Conventional Commit.
