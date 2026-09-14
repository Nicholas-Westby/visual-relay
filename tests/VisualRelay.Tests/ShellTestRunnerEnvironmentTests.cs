using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A command bootstrap checks runs in the environment the pipeline later gives it: the user's own,
/// read from the environment snapshot, not the launcher's. Measured on the Mac through the dev
/// launcher: bootstrap checked ThreeMammals/Ocelot's <c>dotnet test Ocelot.slnx</c> under the
/// launcher's .NET SDK (10.0.400 from nix) while the pipeline would run the user's (10.0.301), rebuilt
/// the solution past the 60 s limit, and kept a command that fails at once in the user's environment.
/// </summary>
public sealed class ShellTestRunnerEnvironmentTests : IDisposable
{
    private readonly string _snapshot = Path.Combine(Path.GetTempPath(), $"vr-shell-env-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_snapshot);

    [Fact]
    public void ResolveLaunch_LocalHost_RunsInTheUsersEnvironmentWithoutTheLaunchersOwn()
    {
        File.WriteAllText(_snapshot, "PATH=/Users/alice/.dotnet:/usr/bin:/bin\0HOME=/Users/alice\0");
        var accessor = new DictionaryEnvironmentAccessor { ["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = _snapshot };
        var launcherEnvironment = new Dictionary<string, string>
        {
            ["PATH"] = "/nix/store/dotnet-sdk-10.0.400/bin:/usr/bin:/bin",
            ["DOTNET_ROOT"] = "/nix/store/dotnet-sdk-10.0.400/share/dotnet",
            ["HOME"] = "/Users/alice",
        };

        var launch = ShellTestRunner.ResolveLaunch(
            "dotnet test", "/Users/alice/repo", loginShell: false, SandboxHost.Local, accessor, launcherEnvironment);

        Assert.Equal("/Users/alice/.dotnet:/usr/bin:/bin", launch.Environment["PATH"]);
        Assert.Contains("DOTNET_ROOT", launch.EnvironmentRemove);
    }

    [Fact]
    public async Task RunAsync_LocalHost_TheCommandDoesNotSeeAVariableOnlyTheLauncherHas()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a POSIX shell parameter expansion");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("HOME") is null, "needs HOME set in this process");
        File.WriteAllText(_snapshot, "PATH=/usr/bin:/bin\0");
        var accessor = new DictionaryEnvironmentAccessor { ["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = _snapshot };
        var runner = new ShellTestRunner(TimeSpan.FromSeconds(30), loginShell: false, SandboxHost.Local, accessor);

        var result = await runner.RunAsync(Path.GetTempPath(), "echo \"[${HOME-unset}]\"");

        Assert.Equal("[unset]", result.Output.Trim());
    }
}
