using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Costs;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private ObservableCollection<ModelCostRow> _modelCostRows = [];

    public void PopulateModelCostRows()
    {
        ModelCostRows.Clear();

        foreach (var (key, pricing) in RelayPricing.Default)
        {
            var row = new ModelCostRow
            {
                ModelKey = key,
                DisplayName = key,
                InputRate = pricing.Input,
                OutputRate = pricing.Output,
                CachedInputRate = pricing.CachedInput,
                CacheWriteRate = pricing.CacheWrite,
                CacheWriteDisplay = FormatCacheWriteDisplay(pricing.CacheWrite),
                HasWindows = pricing.Windows is { Count: > 0 },
            };

            if (pricing.Windows is { Count: > 0 })
            {
                foreach (var window in pricing.Windows)
                {
                    var peakCacheWriteRate = (pricing.CacheWrite ?? pricing.Input) * window.Multiplier;
                    row.Windows.Add(new ModelCostWindowRow
                    {
                        StartTimeDisplay = ConvertWindowTimeToLocal(window.StartLocal, window.TimeZoneId),
                        EndTimeDisplay = ConvertWindowTimeToLocal(window.EndLocal, window.TimeZoneId),
                        SourceTimezoneLabel = window.TimeZoneId,
                        DisplayTimezoneLabel = TimeZoneInfo.Local.Id,
                        Multiplier = window.Multiplier,
                        PeakInputRate = pricing.Input * window.Multiplier,
                        PeakOutputRate = pricing.Output * window.Multiplier,
                        PeakCachedInputRate = (pricing.CachedInput ?? 0) * window.Multiplier,
                        PeakCacheWriteRate = peakCacheWriteRate,
                        PeakCacheWriteDisplay = FormatCacheWriteDisplay(peakCacheWriteRate),
                    });
                }
            }

            ModelCostRows.Add(row);
        }
    }

    private static string FormatCacheWriteDisplay(double? rate)
    {
        if (rate is null)
            return "same as input";
        return $"${rate.Value} per 1M tokens";
    }

    private static string ConvertWindowTimeToLocal(TimeOnly sourceTime, string sourceTimeZoneId)
    {
        try
        {
            var sourceTz = TimeZoneInfo.FindSystemTimeZoneById(sourceTimeZoneId);
            // Use today's date — the date is irrelevant for daily recurring windows;
            // we only need to map the time-of-day from the source timezone to local.
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var sourceDateTime = today.ToDateTime(sourceTime, DateTimeKind.Unspecified);
            var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(sourceDateTime, sourceTz);
            var localDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, TimeZoneInfo.Local);
            return TimeOnly.FromDateTime(localDateTime).ToString("HH:mm");
        }
        catch
        {
            return sourceTime.ToString("HH:mm");
        }
    }
}
