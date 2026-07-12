# Replace "your time" with the actual local time zone in peak-pricing headlines

## Problem

The Settings → "Cost Per LLM Model" panel shows time-of-day peak-pricing windows
converted to the viewer's local clock, e.g.:

> **6:00 PM – 9:00 PM your time — 2× peak pricing**
> (9:00 AM – 12:00 PM in Asia/Shanghai)

The phrase "your time" should instead name the time zone the machine is actually
in (something like "Pacific Time (Los Angeles)"), so the headline is
self-describing in screenshots, support threads, and on machines whose zone the
viewer doesn't expect.

The string is built in `src/VisualRelay.App/ViewModels/MainWindowViewModel.CostPerModel.cs`,
method `BuildWindowHeadline`:

```csharp
return $"{start} – {end} your time — {window.Multiplier.ToString("0.#", CultureInfo.InvariantCulture)}× peak pricing";
```

## Why `TimeZoneInfo.Local.DisplayName` (stripped) is the required source

This was researched empirically on the development host (macOS, .NET 10) before
authoring; the executor must not re-open this decision. The candidates and the
actual values observed:

| Zone in effect | `TimeZoneInfo.Local.Id` | `TimeZoneInfo.Local.DisplayName` |
|---|---|---|
| Host default (no `TZ` set) | `PST8PDT` | `(UTC-08:00) Pacific Time (Los Angeles)` |
| `TZ=America/New_York` | `America/New_York` | `(UTC-05:00) Eastern Time (New York)` |
| `TZ=UTC` | `UTC` | `(UTC) Coordinated Universal Time` |
| `TZ=Asia/Kolkata` | `Asia/Kolkata` | `(UTC+05:30) India Standard Time (Kolkata)` |
| `TZ=Australia/Adelaide` | `Australia/Adelaide` | `(UTC+09:30) Australian Central Time (Adelaide)` |
| `TZ=Etc/GMT+5` | `Etc/GMT+5` | `(UTC-05:00) GMT-05:00` |
| `TZ=America/Argentina/Buenos_Aires` | `America/Argentina/Buenos_Aires` | `(UTC-03:00) Argentina Standard Time (Buenos Aires)` |

Key facts that dictate the choice:

- **The dev host's own system zone is reported to .NET as the POSIX alias
  `PST8PDT`** — with `$TZ` unset. Any label derived from `Id` (raw, split on
  `/`, or converted) prints "PST8PDT" on this very machine. This is exactly why
  the existing test `PopulateModelCostRows_WindowHeadline_NeverContainsPlatformTimezoneId`
  asserts the literal `"PST8PDT"` never appears.
- `DisplayName` is ICU-backed and resolves the alias to a proper human name
  (`Pacific Time (Los Angeles)`) in every probed case, including the alias case.
- `DisplayName` uses the zone's *generic* name ("Pacific Time", not "Pacific
  Standard Time"), so it is never seasonally wrong — no standard-vs-daylight
  selection logic is needed.
- It is one BCL property read plus one prefix strip: no package, no P/Invoke,
  no lookup table.

The label to display is `DisplayName` with the leading `(UTC±HH:MM) ` /
`(UTC) ` chunk removed: **`Pacific Time (Los Angeles)`**. The new headline on
the dev host reads:

> **6:00 PM – 9:00 PM Pacific Time (Los Angeles) — 2× peak pricing**

## Fix

### 1. Label helpers in `MainWindowViewModel.CostPerModel.cs`

Add two small `internal static` methods to the same partial class file that
holds `BuildWindowHeadline` (the file is 187 lines; this keeps it well under
the repo's 300-line guard). They are `internal`, not `private`, so the test
assembly can exercise them directly — `VisualRelay.App` grants
`InternalsVisibleTo("VisualRelay.Tests")` in
`src/VisualRelay.App/Properties/AssemblyInfo.cs`.

```csharp
/// <summary>Human-readable local time zone for the peak-window headline,
/// e.g. "Pacific Time (Los Angeles)". Derived from
/// <see cref="TimeZoneInfo.Local"/>.DisplayName rather than .Id because the
/// Id can be a POSIX alias like "PST8PDT" (observed as the macOS system
/// zone), while the ICU-backed DisplayName resolves the alias to a proper
/// generic name.</summary>
internal static string LocalTimeZoneLabel() =>
    StripUtcOffsetPrefix(TimeZoneInfo.Local.DisplayName);

/// <summary>Removes the leading "(UTC±HH:MM) " / "(UTC) " chunk from a
/// TimeZoneInfo.DisplayName. Returns the input unchanged when the prefix is
/// absent or nothing would remain after stripping.</summary>
internal static string StripUtcOffsetPrefix(string displayName)
{
    if (displayName.StartsWith("(UTC", StringComparison.Ordinal))
    {
        var close = displayName.IndexOf(')');
        if (close >= 0)
        {
            var stripped = displayName[(close + 1)..].TrimStart();
            if (stripped.Length > 0)
                return stripped;
        }
    }
    return displayName;
}
```

### 2. Use the label in `BuildWindowHeadline`

In the `try` branch, replace the literal `your time` with the helper result
(no "in" before it — "9:00 PM Pacific Time (Los Angeles)" is how times are
written in English):

```csharp
return $"{start} – {end} {LocalTimeZoneLabel()} — {window.Multiplier.ToString("0.#", CultureInfo.InvariantCulture)}× peak pricing";
```

Everything else in the method stays byte-for-byte, in particular:

- The `catch` fallback (used when `TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId)`
  throws) keeps its existing `{start} – {end} in {window.TimeZoneId} — …` shape —
  it shows the *source* zone's times, so the local label does not belong there.
- `BuildWindowSourceNote` is untouched: the sub-line
  `(9:00 AM – 12:00 PM in Asia/Shanghai)` is about the provider's zone and is
  already correct.

The label is intentionally read fresh on each `BuildWindowHeadline` call (a
handful of calls per `PopulateModelCostRows`) — do not memoize it in a static
field; `TimeZoneInfo.Local` is already cached by the BCL.

Note on localization: `DisplayName` is localized by ICU to the OS/UI culture.
That is desired for a human-facing label — do **not** add `CultureInfo`
gymnastics to force English. The invariant-culture formatting already present
for the times and the multiplier stays as is.

### 3. Let the headline wrap in `CostPerModel.axaml`

The label makes the headline ~17 characters longer than "your time". The
headline `TextBlock` in `src/VisualRelay.App/Views/Controls/CostPerModel.axaml`
currently cannot wrap and would clip in a narrow settings pane. Add
`TextWrapping="Wrap"` to exactly this element (anchor — inside the
`ModelCostWindowRow` DataTemplate):

```xml
<TextBlock Text="{Binding Headline}"
           FontSize="11" Foreground="#E0A458"/>
```

becomes

```xml
<TextBlock Text="{Binding Headline}"
           FontSize="11" Foreground="#E0A458"
           TextWrapping="Wrap"/>
```

No other XAML changes: `Headline` is a plain bound string, so the view model
change flows through automatically.

## Rejected approaches — do not implement these

- **Any label derived from `TimeZoneInfo.Local.Id`** (raw, last-`/`-segment
  city split, or `TryConvertWindowsIdToIanaId` round-trips): prints `PST8PDT`
  on the development host itself (see the probe table). This is the bug the
  existing "NeverContainsPlatformTimezoneId" test was written against.
- **Abbreviations like "PST"/"PDT"**: the BCL does not expose them. Deriving
  initials from `StandardName`/`DaylightName` produces factually wrong results
  (e.g. "Central European Standard Time" → "CEST", which actually denotes
  European *summer* time), and correct abbreviations would require NodaTime or
  ICU P/Invoke — no new dependencies for a label.
- **`StandardName`/`DaylightName` selected by `IsDaylightSavingTime`**: adds
  seasonal-selection logic and reads wrong near transitions; the generic
  `DisplayName` name avoids the problem entirely.
- **Showing the full `DisplayName` including the `(UTC-08:00)` prefix**: the
  offset restates what the converted clock times already communicate and
  pushes the headline to ~40 extra characters.
- **A regex for the prefix strip**: the `StartsWith`/`IndexOf` slice above is
  the prescribed implementation; do not introduce `System.Text.RegularExpressions`
  into the view model for this.
- **Changing process/test time zones (`TZ` + `TimeZoneInfo.ClearCachedData`)
  to make headline tests deterministic**: `TimeZoneInfo.Local` is process-wide
  and the suite runs tests in parallel — mutating it can flake every other
  test that converts times (including the existing 12-hour-pattern test).
  Determinism comes from testing the pure `StripUtcOffsetPrefix` function
  instead.

## Tests

All changes go in `tests/VisualRelay.Tests/CostPerModelTests.Display.cs`
(currently 128 lines; stays far under the 300-line guard). The class is a
plain (non-headless) xunit fact class — keep it that way.

### Update: `PopulateModelCostRows_WindowHeadline_Matches12HourPattern`

The current pattern pins `your time`:

```csharp
@"^\d{1,2}:\d{2} [AP]M – \d{1,2}:\d{2} [AP]M your time — 2× peak pricing$",
```

Replace the pattern so the zone label (whatever the host's zone is) sits
between the times and the em-dash:

```csharp
@"^\d{1,2}:\d{2} [AP]M – \d{1,2}:\d{2} [AP]M .+ — 2× peak pricing$",
```

### Replace: `PopulateModelCostRows_WindowHeadline_NeverContainsPlatformTimezoneId`

Its blanket "no zone name in the headline" intent is now obsolete — the
headline deliberately contains one. Replace the whole fact with one that pins
the new contract (composition with the helper, no leftover literal, and the
raw-alias tripwire kept):

```csharp
[Fact]
public void PopulateModelCostRows_WindowHeadline_UsesLocalZoneLabel()
{
    var vm = new MainWindowViewModel();
    vm.PopulateModelCostRows();

    var flash = vm.ModelCostRows.Single(r => r.ModelKey == "deepseek-v4-flash");
    foreach (var w in flash.Windows)
    {
        Assert.Contains(MainWindowViewModel.LocalTimeZoneLabel(), w.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("your time", w.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("PST8PDT", w.Headline, StringComparison.Ordinal);
    }
}
```

(`LocalTimeZoneLabel` can never yield `PST8PDT`: `DisplayName` resolves the
alias, so the tripwire assert is consistent with the first assert.)

### New: pure-function facts for `StripUtcOffsetPrefix`

Add facts covering the observed `DisplayName` shapes and the degenerate cases
— these are the deterministic tests; they take strings, not the host's zone:

```csharp
[Theory]
[InlineData("(UTC-08:00) Pacific Time (Los Angeles)", "Pacific Time (Los Angeles)")]
[InlineData("(UTC) Coordinated Universal Time", "Coordinated Universal Time")]
[InlineData("(UTC+05:30) India Standard Time (Kolkata)", "India Standard Time (Kolkata)")]
[InlineData("(UTC-05:00) GMT-05:00", "GMT-05:00")]
[InlineData("Pacific Time", "Pacific Time")]          // no prefix → pass-through
[InlineData("(UTC-08:00)", "(UTC-08:00)")]            // nothing after prefix → pass-through
public void StripUtcOffsetPrefix_StripsLeadingOffsetChunk(string input, string expected)
{
    Assert.Equal(expected, MainWindowViewModel.StripUtcOffsetPrefix(input));
}
```

### Untouched tests that must still pass

`PopulateModelCostRows_WindowSourceNote_IsCorrect` (pins
`"(9:00 AM – 12:00 PM in Asia/Shanghai)"`) and every other fact in
`CostPerModelTests.cs` / `CostPerModelTests.Display.cs` must pass **without
modification** — the source note and all rate formatting are out of scope.

## Verification

- `dotnet test tests/VisualRelay.Tests --filter FullyQualifiedName~CostPerModelTests`
- `./visual-relay check` (runs the file-size guard among others; all touched
  files stay under 300 lines).

## Constraints

- Touch only: `MainWindowViewModel.CostPerModel.cs`,
  `Views/Controls/CostPerModel.axaml`, `CostPerModelTests.Display.cs`.
- No new packages, no new files, no changes to `RelayPricing`,
  `ModelCostRow`/`ModelCostWindowRow`, or `BuildWindowSourceNote`.
- Do not reformat or restructure unrelated code in the touched files.
