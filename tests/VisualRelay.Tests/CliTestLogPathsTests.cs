using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the host/VM-safe log-stem builder that moved out of
/// <c>test.sh</c>. The stem embeds a timestamp, a short hostname, and the PID so
/// concurrent runs from the host AND the VM (sharing the working folder) never
/// collide on log/TRX filenames.
/// </summary>
public sealed class CliTestLogPathsTests
{
    [Fact]
    public void Stem_EmbedsTimestampHostAndPid()
    {
        var paths = TestLogPaths.Create(
            logDir: "/tmp/logs",
            timestamp: new DateTime(2026, 6, 21, 13, 45, 7, DateTimeKind.Local),
            host: "mac",
            pid: 4242);

        Assert.Equal("20260621T134507_mac_4242", paths.Stem);
    }

    [Fact]
    public void LogAndTrxPaths_LiveUnderLogDir_WithStem()
    {
        var paths = TestLogPaths.Create(
            logDir: "/tmp/logs",
            timestamp: new DateTime(2026, 6, 21, 13, 45, 7, DateTimeKind.Local),
            host: "mac",
            pid: 4242);

        Assert.Equal(Path.Combine("/tmp/logs", "20260621T134507_mac_4242.log"), paths.LogFile);
        Assert.Equal(Path.Combine("/tmp/logs", "20260621T134507_mac_4242.trx"), paths.TrxFile);
    }

    [Fact]
    public void SanitizesHost_ToFilesystemSafeToken()
    {
        var paths = TestLogPaths.Create(
            logDir: "/tmp/logs",
            timestamp: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
            host: "weird host/name",
            pid: 1);

        Assert.DoesNotContain('/', paths.Stem);
        Assert.DoesNotContain(' ', paths.Stem);
    }
}
