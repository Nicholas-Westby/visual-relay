using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The processes below a command, read from <c>ps -axo pid=,ppid=,etime=</c>, so stopping the command
/// reaches them without a process group; and, when the stop comes back to kill the ones still running,
/// a check that each pid still names the process it named, not a newer one that reused the number.
/// </summary>
public sealed class PosixProcessTreeTests
{
    // A shell running cargo, cargo running two rustc, and an unrelated process beside the shell.
    private const string Table =
        "    1     0 3-04:05:06\n"
        + "  500     1    01:00:00\n"
        + "  900   500       00:40\n"
        + "  901   900       00:39\n"
        + "  902   901       00:12\n"
        + "  903   901       00:03\n"
        + "  950   500       00:30\n";

    [Fact]
    public void DescendantsOf_ListsEveryProcessBelowTheRootButNeitherTheRootNorItsSiblings()
    {
        var members = PosixProcessTree.DescendantsOf(900, Table);

        Assert.Equal([901, 902, 903], members.Select(member => member.Pid).Order());
        Assert.Equal(12_000, members.Single(member => member.Pid == 902).ElapsedMs);
    }

    [Fact]
    public void StillRunning_KeepsAProcessThatAgedWithTheClockButNotANewerOneOnTheSamePid()
    {
        var members = PosixProcessTree.DescendantsOf(900, Table);
        // Ten seconds on: 902 is the same rustc, orphaned; 903 exited and a process started two seconds
        // ago now holds its pid; 901 is gone.
        const string later = "  902     1       00:22\n  903   777       00:02\n";

        Assert.Equal([902], PosixProcessTree.StillRunning(members, TimeSpan.FromSeconds(10), later));
    }
}
