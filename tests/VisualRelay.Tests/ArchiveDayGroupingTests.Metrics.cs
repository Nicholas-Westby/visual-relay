using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class ArchiveDayGroupingTests
{
    [Fact]
    public void Window_Includes29DaysBack_Excludes30()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            // Today (2026-06-20) — inside window
            Archived("a", AtLocal(2026, 6, 20, 5, 0), costUsd: 1.00),
            // 2026-05-22 (Friday) — 29 days back, inside window
            Archived("b", AtLocal(2026, 5, 22, 10, 0), costUsd: 2.00),
            // 2026-05-21 (Saturday) — 30 days back, outside window
            Archived("c", AtLocal(2026, 5, 21, 8, 0), costUsd: 4.00),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        // Window = 1.00 + 2.00 over 2 tasks. May 21 excluded.
        Assert.Equal("Today: $1.00, $1.50/task, $3/mo", heading);
    }

    [Fact]
    public void OlderGroupInsideWindow_GetsNoMetrics()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", AtLocal(2026, 6, 20, 5, 0), costUsd: 1.00),
            Archived("b", AtLocal(2026, 5, 22, 10, 0), costUsd: 2.00),
            Archived("c", AtLocal(2026, 5, 21, 8, 0), costUsd: 4.00),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 1, today);

        // May 22 has cost with colon but NO metrics — it is not the newest group.
        Assert.Equal("Friday, May 22, 2026: $2.00", heading);
    }

    [Fact]
    public void NewestGroupNotToday_StillGetsMetrics()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", AtLocal(2026, 6, 14, 9, 0), costUsd: 1.20),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        // June 14 (Sunday) is the newest (and only) group — gets metrics.
        Assert.Equal("Sunday, June 14, 2026: $1.20, $1.20/task, $1/mo", heading);
    }

    [Fact]
    public void MonthUnderOneDollar_ShowsCents()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", AtLocal(2026, 6, 19, 10, 0), costUsd: 0.42),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        // Under $1 total, WholeDollars falls back to Dollars.
        Assert.Equal("Yesterday: $0.42, $0.42/task, $0.42/mo", heading);
    }

    [Theory]
    [InlineData(0.0, "$0.00")]
    [InlineData(0.42, "$0.42")]
    [InlineData(1.49, "$1")]
    [InlineData(4.5, "$5")]
    [InlineData(98.4, "$98")]
    public void WholeDollars_Formats(double amount, string expected)
    {
        var result = MoneyFormatter.WholeDollars(amount);
        Assert.Equal(expected, result);
    }
}
