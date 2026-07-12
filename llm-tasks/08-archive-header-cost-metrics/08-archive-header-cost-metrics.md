# Archive day headers: 30-day quick metrics on the newest group + wider left panel

## Problem

In the archive view, day group headers currently read `Today ($1.35)` /
`Yesterday ($0.68)`. Two changes:

1. **Format** switches from parentheses to a colon for every header with cost:
   `Today: $1.35`, `Yesterday: $0.68`, `Thursday, June 18, 2026: $0.50`.
2. **Only the most recent group header** additionally shows two rolling quick metrics,
   both computed over the last 30 days: average cost per task and total spend
   presented as a monthly rate — e.g. `Today: $1.35, $0.18/task, $98/mo`.
   Older group headers show only their day cost.

Additionally the left (queue/archive) panel is slightly too narrow — the collapse
chevron in its header crowds the edge and the longer header text needs room — so its
width goes from 280 to 320.

## Where everything lives

- The header text is built by the pure helper
  `src/VisualRelay.Core/Tasks/ArchiveDayGrouping.cs` → `HeadingFor(IReadOnlyList<RelayTaskItem> orderedTasks, int index, DateOnly today)`.
  Its current cost suffix is:

  ```csharp
  if (dayTotal > 0)
      heading = $"{heading} ({MoneyFormatter.Dollars(dayTotal)})";
  ```

  The list is ordered newest-completion-first; `RelayTaskItem` carries
  `double CostUsd` and `DateTimeOffset? CompletedAt`; days are grouped by
  `CompletedAt.ToLocalTime().Date`.
- The single caller is `MainWindowViewModel.ReloadTaskListAsync`
  (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs`):
  `row.DayHeader = ArchiveDayGrouping.HeadingFor(tasks, i, today) ?? string.Empty;`
  — **the signature does not change and this file is not touched.**
- Money formatting: `src/VisualRelay.Domain/MoneyFormatter.cs` → `Dollars(double)`
  (2-decimal dollars; sub-cent amounts keep 2 significant figures; `<= 0` → `"$0.00"`).
- Panel width: `src/VisualRelay.App/Views/MainWindow.axaml`, under the comment
  `<!-- Left column: Queue or collapsed rail (content-swap in Auto track) -->`:
  `<controls:QueuePanel Width="280" ...`.
- Design-time sample: `src/VisualRelay.App/DesignTime/DesignData.cs` sets
  `{ DayHeader = "Today ($0.42)" }` on the archived sample row.
- Tests: `tests/VisualRelay.Tests/ArchiveDayGroupingTests.cs` (currently 293 lines —
  right under the 300-line guard, which is why new tests go in a new partial file, see
  Tests below).

## Fix

### 1. `MoneyFormatter.WholeDollars` (new helper in `src/VisualRelay.Domain/MoneyFormatter.cs`)

The `/mo` figure rounds to the nearest whole dollar — unless the amount is under $1,
in which case it falls back to cents so small spend never collapses to `$0`:

```csharp
/// <summary>Whole-dollar display for rate-style figures (e.g. "$98"): rounds
/// to the nearest dollar away from zero. Amounts under $1 fall back to
/// <see cref="Dollars"/> so small spend never collapses to "$0".</summary>
public static string WholeDollars(double usd)
{
    if (usd < 1)
        return Dollars(usd);
    var rounded = Math.Round(usd, 0, MidpointRounding.AwayFromZero);
    return $"${rounded.ToString("0", CultureInfo.InvariantCulture)}";
}
```

### 2. `ArchiveDayGrouping.HeadingFor` — replace the cost-suffix block

Keep the method's existing null/label logic (null `CompletedAt` → null; same-day
non-first row → null; Today/Yesterday/`"dddd, MMMM d, yyyy"` label) byte-for-byte.
Replace everything from the `// Sum CostUsd across all tasks...` comment down to the
`return heading;` with a single pass that also accumulates the rolling window and
finds the newest completed day:

```csharp
// One pass: this day's total, the rolling-window aggregates, and the newest
// completed local day (the list arrives newest-first, but scanning is
// order-independent and free at archive sizes).
var dayTotal = 0.0;
var windowTotal = 0.0;
var windowCount = 0;
DateOnly? newestDay = null;
var windowStart = today.AddDays(-(MetricsWindowDays - 1));
foreach (var t in orderedTasks)
{
    if (t.CompletedAt is not { } ct)
        continue;
    var d = DateOnly.FromDateTime(ct.ToLocalTime().Date);
    if (newestDay is null || d > newestDay.Value)
        newestDay = d;
    if (d == localDay)
        dayTotal += t.CostUsd;
    if (d >= windowStart)
    {
        windowTotal += t.CostUsd;
        windowCount++;
    }
}

if (dayTotal > 0)
{
    heading = $"{heading}: {MoneyFormatter.Dollars(dayTotal)}";

    // Quick metrics ride ONLY the newest group's header: average cost per
    // task and total spend over the rolling window, shown as a monthly rate.
    if (localDay == newestDay && windowCount > 0 && windowTotal > 0)
    {
        var perTask = MoneyFormatter.Dollars(windowTotal / windowCount);
        var perMonth = MoneyFormatter.WholeDollars(windowTotal);
        heading = $"{heading}, {perTask}/task, {perMonth}/mo";
    }
}

return heading;
```

with this constant added to the class:

```csharp
/// <summary>Rolling window (local calendar days, ending at and including
/// <c>today</c>) feeding the newest header's per-task and per-month metrics.</summary>
private const int MetricsWindowDays = 30;
```

Also update the class XML doc summary to mention that the newest completed day's
heading carries rolling-30-day quick metrics.

Pinned semantics (the tests below encode all of these):

- **Window** = local calendar days `d` with `d >= today.AddDays(-29)` — the 30 days
  ending at and including today. The window count includes zero-cost completed tasks
  (they dilute the average deliberately: "average cost per task" means all tasks).
- **Newest group** = the group of the newest completed local day in the list — not
  necessarily "Today". If the newest archive entry is from last week, that header
  gets the metrics.
- **Per-task** = `Dollars(windowTotal / windowCount)` — cents always (e.g. `$0.18/task`).
- **Per-month** = `WholeDollars(windowTotal)` — whole dollars, cents only under $1.
- **Zero-cost day** → bare label with no colon and no metrics (existing behavior,
  already pinned by `ZeroCost_OmitsCostSuffix` and
  `MultipleTasksSameDayZeroTotal_OmitsCostSuffix` — those tests stay green unmodified).
- If the newest group's day total is 0, or the window is empty/zero, no metrics are
  appended anywhere.

### 3. Panel width

In `src/VisualRelay.App/Views/MainWindow.axaml`, change
`<controls:QueuePanel Width="280"` to `<controls:QueuePanel Width="320"`. Leave the
collapsed-rail `<Border Width="36"` and every other width alone. (The panel sets its
own width inside an `Auto` grid track — do not touch the ColumnDefinitions.)

### 4. Design-time sample

In `src/VisualRelay.App/DesignTime/DesignData.cs`, update the archived sample row's
header to the new shape so the previewer shows the real format:

```csharp
{ DayHeader = "Today: $0.42, $0.21/task, $13/mo" });
```

(`DesignDataTests` only asserts a non-empty `DayHeader`; it stays green.)

## Tests

`ArchiveDayGroupingTests.cs` is at 293 lines; adding tests there would breach the
300-line `FileSizeGuard`. Follow the repo's existing partial-test-class precedent
(`CostPerModelTests` + `CostPerModelTests.Display.cs`):

1. In `tests/VisualRelay.Tests/ArchiveDayGroupingTests.cs`: change
   `public sealed class ArchiveDayGroupingTests` to
   `public sealed partial class ArchiveDayGroupingTests`, and update the five
   existing cost-format pins in place (the `AtLocal`/`Archived` helpers stay where
   they are — the new partial can use them):
   - `Today_WithCost_IncludesTotalCost` (single task today, 1.54; window = that task):
     expect `"Today: $1.54, $1.54/task, $2/mo"`.
   - `Yesterday_WithMultipleTasks_SumsCosts` (0.10 + 0.11 yesterday; yesterday IS the
     newest group): expect `"Yesterday: $0.21, $0.11/task, $0.21/mo"` at index 0,
     null at index 1.
   - `FullDate_WithCost_IncludesTotalCost` (single task June 17, 5.00 — newest group):
     replace `Assert.Contains("($5.00)", ...)` with
     `Assert.Contains(": $5.00, $5.00/task, $5/mo", heading, StringComparison.Ordinal);`
     (keep the existing "17"/"2026" Contains asserts).
   - `FirstOfNewDay_CostOnlyOnHeadingRow` (today 3.00 total; June 18 0.50; June 15
     1.00; all within the window → windowTotal 4.50 over 5 tasks):
     index 0 → `"Today: $3.00, $0.90/task, $5/mo"`;
     the `Assert.Contains("($0.50)", ...)` → `Assert.Contains(": $0.50", ...)`;
     `"Thursday, June 18, 2026 ($0.50)"` → `"Thursday, June 18, 2026: $0.50"`;
     `"Monday, June 15, 2026 ($1.00)"` → `"Monday, June 15, 2026: $1.00"`.
     Note the older headers get NO metrics — that is the point of this test now.
   - All no-cost and grouping tests (`Today_FirstTaskLocalDateEqualsToday_ReturnsToday`,
     `ZeroCost_OmitsCostSuffix`, etc.) stay byte-for-byte.

2. New file `tests/VisualRelay.Tests/ArchiveDayGroupingTests.Metrics.cs`
   (`public sealed partial class ArchiveDayGroupingTests`, same namespace) with:

   - `Window_Includes29DaysBack_Excludes30` — today = 2026-06-20; tasks: today 1.00,
     `AtLocal(2026, 5, 22, …)` 2.00 (29 days back — inside), `AtLocal(2026, 5, 21, …)`
     4.00 (30 days back — outside). Index 0 must equal
     `"Today: $1.00, $1.50/task, $3/mo"` (window = 1.00 + 2.00 over 2 tasks).
   - `OlderGroupInsideWindow_GetsNoMetrics` — reuse the three-task list above and
     assert the 2026-05-22 header equals `"Friday, May 22, 2026: $2.00"` (cost, colon,
     no `/task`, no `/mo`).
   - `NewestGroupNotToday_StillGetsMetrics` — today = 2026-06-20; single task
     `AtLocal(2026, 6, 14, …)` 1.20 (six days ago, newest and only group):
     expect `"Sunday, June 14, 2026: $1.20, $1.20/task, $1/mo"`.
   - `MonthUnderOneDollar_ShowsCents` — single task yesterday 0.42:
     expect `"Yesterday: $0.42, $0.42/task, $0.42/mo"`.
   - `WholeDollars_Formats` — `[Theory]` directly on `MoneyFormatter.WholeDollars`
     (no MoneyFormatter test file exists; this pins the new helper):
     `(0.0, "$0.00")`, `(0.42, "$0.42")`, `(1.49, "$1")`, `(4.5, "$5")`,
     `(98.4, "$98")`.

   Date sanity for the pins: 2026-06-20 is a Saturday (the existing suite already pins
   Thursday June 18 and Monday June 15, 2026), so 2026-05-22 is a Friday and
   2026-06-14 is a Sunday.

Both test files must stay under 300 lines (the main file's edits are same-line
replacements; the new file is ~120 lines).

## Rejected approaches — do not do these

- **Computing the metrics in `MainWindowViewModel`** — the grouping helper is pure and
  unit-tested precisely so this logic never needs an Avalonia session; the caller and
  the `HeadingFor` signature stay untouched.
- **Calendar-month totals** ("spend in June") for `/mo` — the requirement is a rolling
  last-30-days figure presented as a monthly rate.
- **Rounding `$/task` to whole dollars** — only the `/mo` figure rounds; per-task
  keeps cents.
- **Metrics on every day header** — newest group only; older headers show just their
  day cost.
- **Widening via the Grid ColumnDefinitions or the collapsed rail** — only the
  `QueuePanel Width` attribute changes.
- **Caching/precomputing the window aggregates across `HeadingFor` calls** — the per-call
  scan is O(n) with n ≈ tens; keep the helper stateless and pure.

## Verification

1. `dotnet build` — green.
2. `dotnet test tests/VisualRelay.Tests --filter "FullyQualifiedName~ArchiveDayGroupingTests|FullyQualifiedName~DesignDataTests"` — green.
3. `./visual-relay check` — must pass; in particular the diff of THIS task must not
   introduce any new InspectCode finding (no unused usings, no unreferenced locals)
   and every touched `.cs`/`.axaml` file must stay under the 300-line guard.
4. Launch the app, open the archive: the newest day header reads like
   `Today: $1.35, $0.18/task, $98/mo`, older ones like `Yesterday: $0.68`, and the
   header row (title, count chip, New/Queue buttons, collapse chevron) fits without
   crowding at the new 320px width.

## Constraints

- Touch ONLY: `src/VisualRelay.Core/Tasks/ArchiveDayGrouping.cs`,
  `src/VisualRelay.Domain/MoneyFormatter.cs`, `src/VisualRelay.App/Views/MainWindow.axaml`
  (one attribute), `src/VisualRelay.App/DesignTime/DesignData.cs` (one string),
  `tests/VisualRelay.Tests/ArchiveDayGroupingTests.cs`, and the new
  `tests/VisualRelay.Tests/ArchiveDayGroupingTests.Metrics.cs`.
- Do NOT touch `MainWindowViewModel.Helpers.cs`, `TaskRowViewModel`, `TaskCard.axaml`,
  `QueuePanel.axaml`, or `MoneyFormatter.Dollars`.
- No new packages. `WholeDollars` uses `CultureInfo.InvariantCulture` like the rest of
  `MoneyFormatter`; the date label keeps `CultureInfo.CurrentCulture` as today.
