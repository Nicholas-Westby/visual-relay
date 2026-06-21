using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ArchiveDayGroupingTests
{
    [Fact]
    public void Today_FirstTaskLocalDateEqualsToday_ReturnsToday()
    {
        var today = new DateOnly(2026, 6, 20);
        // UTC noon = local 5am PDT (UTC-7) → still June 20
        var tasks = new[]
        {
            Archived("a", new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Equal("Today", heading);
    }

    [Fact]
    public void Yesterday_FirstTaskLocalDateEqualsYesterday_ReturnsYesterday()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", new DateTimeOffset(2026, 6, 19, 10, 0, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Equal("Yesterday", heading);
    }

    [Fact]
    public void FullDate_EarlierDate_ReturnsFormattedDate()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        // "dddd, MMMM d, yyyy" using CurrentCulture
        Assert.NotNull(heading);
        Assert.NotEqual("Today", heading);
        Assert.NotEqual("Yesterday", heading);
        // Must contain the weekday, month, and year.
        Assert.Contains("17", heading, StringComparison.Ordinal);
        Assert.Contains("2026", heading, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFirstOfSameDay_ReturnsNull()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero)),
            Archived("b", new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.Zero)),
        };

        var firstHeading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);
        var secondHeading = ArchiveDayGrouping.HeadingFor(tasks, 1, today);

        Assert.Equal("Today", firstHeading);
        Assert.Null(secondHeading);
    }

    [Fact]
    public void UtcEvening_MapsToCorrectLocalDay()
    {
        // UTC 2026-06-17T23:30:00Z → PDT (UTC-7) = 2026-06-17T16:30:00 → still June 17.
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("late", new DateTimeOffset(2026, 6, 17, 23, 30, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.NotNull(heading);
        Assert.NotEqual("Today", heading);
        Assert.NotEqual("Yesterday", heading);
        Assert.Contains("17", heading, StringComparison.Ordinal);
    }

    [Fact]
    public void UtcMidnightCrossesToPreviousLocalDay()
    {
        // UTC 2026-06-18T02:00:00Z → PDT (UTC-7) = 2026-06-17T19:00:00 → June 17.
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("cross", new DateTimeOffset(2026, 6, 18, 2, 0, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.NotNull(heading);
        Assert.Contains("17", heading, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCompletedAt_ReturnsNull()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new RelayTaskItem[]
        {
            new("no-time", "/tmp/a.md", "/tmp", false, [], IsArchived: true, CompletedAt: null),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Null(heading);
    }

    [Fact]
    public void FirstOfNewDayAfterGap_HeadingAppearsOnlyOnFirstOfEachDay()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            // Day 1: June 20 (today) — two tasks
            Archived("a", new DateTimeOffset(2026, 6, 20, 14, 0, 0, TimeSpan.Zero)),
            Archived("b", new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero)),
            // Day 2: June 18 — one task
            Archived("c", new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero)),
            // Day 3: June 15 — two tasks
            Archived("d", new DateTimeOffset(2026, 6, 15, 18, 0, 0, TimeSpan.Zero)),
            Archived("e", new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero)),
        };

        Assert.Equal("Today", ArchiveDayGrouping.HeadingFor(tasks, 0, today));
        Assert.Null(ArchiveDayGrouping.HeadingFor(tasks, 1, today));
        Assert.NotNull(ArchiveDayGrouping.HeadingFor(tasks, 2, today));
        Assert.NotEqual("Today", ArchiveDayGrouping.HeadingFor(tasks, 2, today));
        Assert.NotNull(ArchiveDayGrouping.HeadingFor(tasks, 3, today));
        Assert.Null(ArchiveDayGrouping.HeadingFor(tasks, 4, today));
    }

    [Fact]
    public void YesterdayBoundary_CrossesAtMidnightLocal()
    {
        // Today is June 20. A task completed at local time 2026-06-19T23:59:00
        // (UTC: 2026-06-20T06:59:00Z for PDT) should get "Yesterday".
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("y", new DateTimeOffset(2026, 6, 20, 6, 59, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Equal("Yesterday", heading);
    }

    [Fact]
    public void TodayBoundary_EarlyMorningUtcStillToday()
    {
        // Today is June 20. A task completed at UTC 2026-06-20T10:00:00Z
        // = local 3am PDT → still June 20 → "Today".
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("t", new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Equal("Today", heading);
    }

    private static RelayTaskItem Archived(string id, DateTimeOffset completedAt) =>
        new(id, $"/tmp/{id}.md", "/tmp", false, [],
            IsArchived: true, CompletedAt: completedAt);
}
