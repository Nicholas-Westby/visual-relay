using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// At launch, when the WSL gate fails for something <c>setup-wsl</c> can finish, offers to run
/// it there and then. It follows the consent rule every install here follows: ask y/N with no
/// as the default, act only on an explicit yes, never remember the answer, and never ask when
/// nobody is at the terminal. The question names the problem and every step first.
/// </summary>
public static class WslSetupOffer
{
    /// <returns>
    /// Null when setup was not offered or was declined, so the gate's own message stands;
    /// otherwise the setup's exit code and, when it worked, the probe it ended with.
    /// </returns>
    public static async Task<(int ExitCode, WslProbe? Ready)?> OfferAsync(
        WslProbe probe, WslSetupHost host, bool interactive, TextReader input, TextWriter output, CancellationToken ct)
    {
        if (!interactive)
            return null;

        var plan = WslSetupPlan.For(probe, host.LinuxUser);
        if (plan.Blocker is not null || plan.Steps.Count == 0)
            return null;

        var problem = WslGate.Decide(probe).Message!.Split('\n')[0];
        await output.WriteLineAsync(problem);
        await output.WriteLineAsync("Visual Relay can set this up for you, with no administrator rights needed:");
        await output.WriteLineAsync(WslSetup.Describe(plan));
        await output.WriteAsync("Set it up now? [y/N] ");
        await output.FlushAsync(ct);
        var answer = await input.ReadLineAsync(ct);
        if (answer?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            return null;

        return await WslSetup.CarryOutAsync(host, plan, output.WriteLine, ct);
    }
}
