using System.Globalization;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// <see cref="IProcessTreeControl"/> for a tree that lives inside a WSL distro.
/// Learns the sandbox root's pid (its process group, thanks to the envelope's
/// setsid) from the pid file the envelope wrote, sums the tree's CPU from one
/// <c>ps</c> inside the distro, and signals the group from inside the distro,
/// which is the only place a signal reaches it. Every step is a plain
/// <c>wsl.exe --exec</c> launch handed to the injected runner, so the strategy is
/// exercised without wsl.exe. A pid file that is not there yet (the envelope has
/// not reached its echo) is "no signal", retried on the next call.
/// </summary>
public sealed class WslProcessTreeControl(
    WslContext context,
    string pidFile,
    Func<WslLaunch, CancellationToken, Task<(int ExitCode, string Output)>> run) : IProcessTreeControl
{
    private int? _pgid;

    public async Task<long?> SampleCpuMsAsync(CancellationToken ct)
    {
        var pgid = await ResolvePgidAsync(ct);
        if (pgid is null)
            return null;

        var (exitCode, output) = await RunAsync(WslProcessControl.SampleArgv(), ct);
        return exitCode == 0 ? ProcessTreeCpuSampler.SumTreeCpuMs(pgid.Value, output) : null;
    }

    public async Task StopAsync(bool graceful, CancellationToken ct)
    {
        var pgid = await ResolvePgidAsync(ct);
        if (pgid is null)
            return;

        await RunAsync(WslProcessControl.KillArgv(pgid.Value, graceful ? "TERM" : "KILL"), ct);

        // The forced stop is the reap step every launch ends with, and the pid file
        // outlives the envelope that wrote it: remove it here or it stays forever.
        if (!graceful)
            await RunAsync(WslProcessControl.RemovePidFileArgv(pidFile), ct);
    }

    private async Task<int?> ResolvePgidAsync(CancellationToken ct)
    {
        if (_pgid is not null)
            return _pgid;

        var (exitCode, output) = await RunAsync(WslProcessControl.ReadPidFileArgv(pidFile), ct);
        if (exitCode != 0)
            return null;

        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && l.All(char.IsAsciiDigit));
        if (line is null || !int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            return null;

        _pgid = pid;
        return pid;
    }

    private Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> argv, CancellationToken ct) =>
        run(WslLauncher.BuildPlain(context.WslExePath, context.Distro, argv), ct);
}
