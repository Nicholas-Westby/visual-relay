using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ArchiveDayGroupingTests
{
    [Fact]
    public void Today_FirstTaskLocalDateEqualsToday_ReturnsToday()
    {
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("a", AtLocal(2026, 6, 20, 5, 0)),
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
            Archived("a", AtLocal(2026, 6, 19, 10, 0)),
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
            Archived("a", AtLocal(2026, 6, 17, 8, 0)),
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
            Archived("a", AtLocal(2026, 6, 20, 10, 0)),
            Archived("b", AtLocal(2026, 6, 20, 14, 0)),
        };

        var firstHeading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);
        var secondHeading = ArchiveDayGrouping.HeadingFor(tasks, 1, today);

        Assert.Equal("Today", firstHeading);
        Assert.Null(secondHeading);
    }

    [Fact]
    public void LocalEvening_StaysOnSameLocalDay()
    {
        // A task completed at local 16:30 on June 17 stays on June 17
        // regardless of the machine's timezone.
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("late", AtLocal(2026, 6, 17, 16, 30)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.NotNull(heading);
        Assert.NotEqual("Today", heading);
        Assert.NotEqual("Yesterday", heading);
        Assert.Contains("17", heading, StringComparison.Ordinal);
    }

    [Fact]
    public void LateLocalEvening_StaysOnPreviousLocalDay()
    {
        // A task completed at local 19:00 on June 17 belongs to June 17,
        // not the following calendar day — independent of timezone.
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("cross", AtLocal(2026, 6, 17, 19, 0)),
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
            Archived("a", AtLocal(2026, 6, 20, 14, 0)),
            Archived("b", AtLocal(2026, 6, 20, 10, 0)),
            // Day 2: June 18 — one task
            Archived("c", AtLocal(2026, 6, 18, 9, 0)),
            // Day 3: June 15 — two tasks
            Archived("d", AtLocal(2026, 6, 15, 18, 0)),
            Archived("e", AtLocal(2026, 6, 15, 8, 0)),
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
        // Today is June 20. A task completed at local 2026-06-19T23:59:00
        // should get "Yesterday" on any machine timezone.
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("y", AtLocal(2026, 6, 19, 23, 59)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Equal("Yesterday", heading);
    }

    [Fact]
    public void TodayBoundary_EarlyMorningLocalStillToday()
    {
        // Today is June 20. A task completed at local 03:00 on June 20
        // is still "Today" regardless of timezone.
        var today = new DateOnly(2026, 6, 20);
        var tasks = new[]
        {
            Archived("t", AtLocal(2026, 6, 20, 3, 0)),
        };

        var heading = ArchiveDayGrouping.HeadingFor(tasks, 0, today);

        Assert.Equal("Today", heading);
    }

    // Builds a DateTimeOffset at the given LOCAL wall-clock time using the
    // machine's own UTC offset for that instant. HeadingFor groups by
    // .ToLocalTime().Date, so anchoring construction to local time keeps every
    // boundary assertion true on any agent timezone (UTC, Pacific, etc.).
    private static DateTimeOffset AtLocal(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    private static RelayTaskItem Archived(string id, DateTimeOffset completedAt) =>
        new(id, $"/tmp/{id}.md", "/tmp", false, [],
            IsArchived: true, CompletedAt: completedAt);
}
