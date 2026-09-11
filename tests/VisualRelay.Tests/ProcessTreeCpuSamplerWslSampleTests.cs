using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The CPU summation the WSL tree control feeds: the text of
/// <c>ps -axo pid=,ppid=,time=</c> captured through wsl.exe from a Linux distro,
/// summed for the sandbox root's tree exactly as the Unix sampler sums the host's.
/// </summary>
public sealed class ProcessTreeCpuSamplerWslSampleTests
{
    // Captured shape from a WSL2 Ubuntu distro: right-aligned columns, hh:mm:ss
    // times, a days prefix once a process passes 24 h of CPU.
    private const string Sample =
        "      1       0 00:00:02\n" +
        "    311       1 00:00:00\n" +
        "   4242     311 00:00:01\n" +   // the sandbox root the envelope started (setsid → env → nono)
        "   4250    4242 00:01:30\n" +   // the build the agent launched
        "   4260    4250 00:00:00\n" +   // a compiler child
        "   4261    4250 1-00:00:05\n" + // a child past one day of CPU
        "   5000       1 00:10:00\n";    // unrelated: a service in the distro

    [Fact]
    public void SumTreeCpuMs_ThreeLevelTree_SumsTheRootAndEveryDescendantOnly()
    {
        const long expected = 1_000 + 90_000 + 0 + (86_400_000 + 5_000);

        Assert.Equal(expected, ProcessTreeCpuSampler.SumTreeCpuMs(4242, Sample));
    }

    [Fact]
    public void SumTreeCpuMs_SubtreeRoot_CountsItsOwnBranch()
    {
        Assert.Equal(90_000 + 86_405_000, ProcessTreeCpuSampler.SumTreeCpuMs(4250, Sample));
        Assert.Equal(0, ProcessTreeCpuSampler.SumTreeCpuMs(4260, Sample));
    }

    [Fact]
    public void SumTreeCpuMs_RootNotInTheSnapshot_IsZero()
    {
        Assert.Equal(0, ProcessTreeCpuSampler.SumTreeCpuMs(9999, Sample));
    }

    [Fact]
    public void SumTreeCpuMs_CrLfAndTrailingNoise_AreTolerated()
    {
        var noisy = Sample.Replace("\n", "\r\n") + "garbage line\r\n";

        Assert.Equal(86_496_000, ProcessTreeCpuSampler.SumTreeCpuMs(4242, noisy));
    }
}
