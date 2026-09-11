using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The argv VR sends into the distro to control the sandboxed tree: signal the
/// process GROUP the envelope's setsid created (negative pgid after <c>--</c>),
/// sample every process for the CPU summation, and read the pid file the envelope
/// wrote. Pure shapes; the watchdog kill proof itself needs a Windows box.
/// </summary>
public sealed class WslProcessControlTests
{
    [Fact]
    public void KillArgv_SignalsTheNegativeGroupAfterTheOptionTerminator()
    {
        Assert.Equal(["kill", "-TERM", "--", "-1234"], WslProcessControl.KillArgv(1234, "TERM"));
        Assert.Equal(["kill", "-KILL", "--", "-7"], WslProcessControl.KillArgv(7, "KILL"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-4242)]
    public void KillArgv_NeverTargetsGroupZeroOrEveryProcess(int pgid)
    {
        // kill -- -0 / -- 1 would reach the caller's own group or every process
        // the user owns; a non-positive pgid can only come from a bad pid file.
        Assert.Throws<ArgumentOutOfRangeException>(() => WslProcessControl.KillArgv(pgid, "TERM"));
    }

    [Fact]
    public void SampleArgv_IsTheSameHeaderlessPsShapeTheUnixSamplerReads()
    {
        Assert.Equal(["ps", "-axo", "pid=,ppid=,time="], WslProcessControl.SampleArgv());
    }

    [Fact]
    public void ReadPidFileArgv_CatsTheFile()
    {
        Assert.Equal(["cat", "/tmp/visual-relay/x.pid"], WslProcessControl.ReadPidFileArgv("/tmp/visual-relay/x.pid"));
    }

    /// <summary>
    /// Nothing else removes the pid file: the envelope writes it and exits, and a
    /// stray file per launch would accumulate in the distro's tmp forever.
    /// </summary>
    [Fact]
    public void RemovePidFileArgv_ForcesTheRemovalAfterTheOptionTerminator()
    {
        Assert.Equal(
            ["rm", "-f", "--", "/tmp/visual-relay/x.pid"],
            WslProcessControl.RemovePidFileArgv("/tmp/visual-relay/x.pid"));
    }

    [Fact]
    public void PidFilePath_LivesUnderTheDistrosTmpPerRunAndAttempt()
    {
        Assert.Equal("/tmp/visual-relay/run-1-attempt-2.pid", WslProcessControl.PidFilePath("run-1", "attempt-2"));
        Assert.Equal("/tmp/visual-relay", WslProcessControl.PidFileDirectory);
    }
}
