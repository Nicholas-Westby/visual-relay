using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Costs;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly string[] TierOrder =
        ["cheap", "balanced", "frontier", "vision", "claude", "fallback"];

    [ObservableProperty]
    private ObservableCollection<ModelCostRow> _modelCostRows = [];

    /// <summary>
    /// Populate the cost-panel cards from pricing data and tier assignments.
    /// When <paramref name="tierAssignments"/> is <c>null</c>, the default
    /// tier→concrete-model resolution (<see cref="BackendConfigGenerator.DefaultTierResolution"/>)
    /// is used so the panel reflects the auto-resolution before keys are loaded.
    /// The card list is the union of <see cref="RelayPricing.Default"/> keys and
    /// the assignment values, so an override pointing at a model with no pricing
    /// entry still yields a card (marked unpriced).
    /// </summary>
    public void PopulateModelCostRows(IReadOnlyDictionary<string, string>? tierAssignments = null)
    {
        tierAssignments ??= BackendConfigGenerator.DefaultTierResolution;

        ModelCostRows.Clear();

        // Union of pricing keys and assignment values.
        var allModelKeys = new HashSet<string>(RelayPricing.Default.Keys, StringComparer.Ordinal);
        foreach (var v in tierAssignments.Values)
            allModelKeys.Add(v);

        // Build model → tier-badges mapping.
        var modelBadges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (tier, model) in tierAssignments)
        {
            if (!modelBadges.TryGetValue(model, out var badges))
            {
                badges = new List<string>();
                modelBadges[model] = badges;
            }
            badges.Add(tier);
        }

        // Sort badges per model by TierOrder.
        foreach (var (_, badges) in modelBadges)
            badges.Sort((a, b) => TierOrderIndex(a).CompareTo(TierOrderIndex(b)));

        var rows = new List<ModelCostRow>();
        foreach (var modelKey in allModelKeys)
        {
            var badges = modelBadges.TryGetValue(modelKey, out var b)
                ? b
                : new List<string>();
            var isPriced = RelayPricing.Default.TryGetValue(modelKey, out var pricing);

            var row = new ModelCostRow
            {
                ModelKey = modelKey,
                IsActive = badges.Count > 0,
                IsPriced = isPriced,
            };

            foreach (var badge in badges)
                row.TierBadges.Add(badge);

            if (isPriced && pricing is not null)
            {
                row.InputDisplay = FormatRate(pricing.Input);
                row.OutputDisplay = FormatRate(pricing.Output);
                row.CachedInputDisplay = FormatRateRelativeToInput(
                    pricing.EffectiveCachedInput, pricing.Input);
                row.CacheWriteDisplay = FormatRateRelativeToInput(
                    pricing.EffectiveCacheWrite, pricing.Input);
                row.HasWindows = pricing.Windows is { Count: > 0 };

                if (pricing.Windows is { Count: > 0 })
                {
                    foreach (var window in pricing.Windows)
                    {
                        var peakInput = pricing.Input * window.Multiplier;
                        var peakOutput = pricing.Output * window.Multiplier;
                        var peakCachedInput = pricing.EffectiveCachedInput * window.Multiplier;
                        var peakCacheWrite = pricing.EffectiveCacheWrite * window.Multiplier;

                        row.Windows.Add(new ModelCostWindowRow
                        {
                            Headline = BuildWindowHeadline(window),
                            SourceNote = BuildWindowSourceNote(window),
                            PeakInputDisplay = FormatRate(peakInput),
                            PeakOutputDisplay = FormatRate(peakOutput),
                            PeakCachedInputDisplay = FormatRateRelativeToInput(
                                peakCachedInput, peakInput),
                            PeakCacheWriteDisplay = FormatRateRelativeToInput(
                                peakCacheWrite, peakInput),
                        });
                    }
                }
            }

            rows.Add(row);
        }

        // Sort: badged cards first (by first badge's TierOrder index),
        // then unbadged cards by ordinal model name.
        rows.Sort((a, b) =>
        {
            var aHas = a.TierBadges.Count > 0;
            var bHas = b.TierBadges.Count > 0;
            if (aHas && !bHas) return -1;
            if (!aHas && bHas) return 1;
            if (aHas)
            {
                var ai = TierOrderIndex(a.TierBadges[0]);
                var bi = TierOrderIndex(b.TierBadges[0]);
                return ai.CompareTo(bi);
            }
            return string.CompareOrdinal(a.ModelKey, b.ModelKey);
        });

        foreach (var row in rows)
            ModelCostRows.Add(row);
    }

    // ── Rate formatting helpers ──────────────────────────────────────────

    private static string FormatRate(double rate) =>
        "$" + rate.ToString("0.######", CultureInfo.InvariantCulture) + " per 1M tokens";

    private static string FormatRateRelativeToInput(double effective, double input) =>
        effective == input
            ? FormatRate(effective) + " (same as input)"
            : FormatRate(effective);

    // ── Window display helpers ───────────────────────────────────────────

    private static string BuildWindowHeadline(RateWindow window)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
            var start = ConvertWindowTimeToLocalDisplay(window.StartLocal, tz);
            var end = ConvertWindowTimeToLocalDisplay(window.EndLocal, tz);
            return $"{start} – {end} {LocalTimeZoneLabel()} — {window.Multiplier.ToString("0.#", CultureInfo.InvariantCulture)}× peak pricing";
        }
        catch
        {
            var start = window.StartLocal.ToString("h:mm tt", CultureInfo.InvariantCulture);
            var end = window.EndLocal.ToString("h:mm tt", CultureInfo.InvariantCulture);
            return $"{start} – {end} in {window.TimeZoneId} — {window.Multiplier.ToString("0.#", CultureInfo.InvariantCulture)}× peak pricing";
        }
    }

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

    private static string BuildWindowSourceNote(RateWindow window)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId); // validate
            var start = window.StartLocal.ToString("h:mm tt", CultureInfo.InvariantCulture);
            var end = window.EndLocal.ToString("h:mm tt", CultureInfo.InvariantCulture);
            return $"({start} – {end} in {window.TimeZoneId})";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ConvertWindowTimeToLocalDisplay(TimeOnly sourceTime, TimeZoneInfo sourceTz)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sourceDateTime = today.ToDateTime(sourceTime, DateTimeKind.Unspecified);
        var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(sourceDateTime, sourceTz);
        var localDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, TimeZoneInfo.Local);
        return TimeOnly.FromDateTime(localDateTime).ToString("h:mm tt", CultureInfo.InvariantCulture);
    }

    private static int TierOrderIndex(string tier)
    {
        var i = Array.IndexOf(TierOrder, tier);
        return i >= 0 ? i : int.MaxValue;
    }
}
