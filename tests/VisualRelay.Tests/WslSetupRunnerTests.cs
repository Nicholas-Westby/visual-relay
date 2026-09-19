using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// How <c>setup-wsl</c> carries a plan out: each step as wsl.exe calls, everything inside
/// the distro run as root through <c>-u root</c> (which needs no password), and the run
/// stopped at the first step that fails. The wsl.exe here answers from a script, so
/// nothing is spawned.
/// </summary>
public sealed class WslSetupRunnerTests
{
    private const string User = "alice";

    private static readonly WslSetupPlan NewUbuntu = WslSetupPlan.For(WslProbeFixtures.NoDistro(), User);

    [Fact]
    public async Task ANewDistro_IsInstalledThenSetUpAsRoot_InPlanOrder()
    {
        var wsl = new AnsweringWsl();

        var outcome = await RunAsync(NewUbuntu, wsl);

        Assert.Null(outcome.Failure);
        Assert.Empty(wsl.AdministratorCalls);
        Assert.Collection(wsl.Calls,
            call => Assert.Equal(["--install", "Ubuntu", "--no-launch"], call),
            // The install's exit code does not say whether a distro arrived; the listing does.
            call => Assert.Equal(["-l", "-v"], call),
            call => Assert.Equal(AsRoot("Ubuntu", WslSetupScripts.CreateUser, User), call),
            // A new default user takes effect only once the distro starts again.
            call => Assert.Equal(["--terminate", "Ubuntu"], call),
            call => Assert.Equal(AsRoot("Ubuntu", WslSetupScripts.Packages), call),
            call => Assert.Equal(AsRoot("Ubuntu", WslSetupScripts.Nono, NonoRelease.Version,
                NonoRelease.Amd64DebSha256, NonoRelease.Arm64DebSha256, NonoRelease.DebUrlPrefix, ""), call));
    }

    [Fact]
    public async Task WslItself_IsInstalledWithAdministratorApproval_AndNothingElseRunsThatWay()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.InboxStubOnly(), User);
        var wsl = new AnsweringWsl();

        var outcome = await RunAsync(plan, wsl);

        Assert.Null(outcome.Failure);
        Assert.Equal([["--install", "--no-distribution"]], wsl.AdministratorCalls);
        Assert.Empty(wsl.Calls);
    }

    /// <summary>3010 is how a Windows installer says "done, restart to finish".</summary>
    [Fact]
    public async Task AnInstallerAskingForARestart_HasSucceeded()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.InboxStubOnly(), User);
        var wsl = new AnsweringWsl { AdministratorExitCode = 3010 };

        var outcome = await RunAsync(plan, wsl);

        Assert.Null(outcome.Failure);
    }

    [Fact]
    public async Task ADistroNamedOtherThanItsImage_IsRegisteredUnderThatName_AndSetUpThere()
    {
        var plan = WslSetupPlan.For(
            WslProbeFixtures.RequestedDistroMissing() with { RequestedDistro = "VrNoSuchDistro" }, User);
        var wsl = new AnsweringWsl();

        await RunAsync(plan, wsl);

        Assert.Equal(["--install", "Ubuntu", "--name", "VrNoSuchDistro", "--no-launch"], wsl.Calls[0]);
        var setUp = wsl.Calls.Skip(1).Where(call => call is not ["-l", "-v"]).ToList();
        Assert.All(setUp, call => Assert.Contains("VrNoSuchDistro", call));
        Assert.DoesNotContain(setUp, call => call.Contains("Ubuntu"));
    }

    [Fact]
    public async Task ALocalNonoPackage_IsHandedToTheScriptInPlaceOfTheDownload()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.NonoMissing(), User);
        var wsl = new AnsweringWsl();

        await RunAsync(plan, wsl, localNonoDeb: @"C:\Temp\nono-cli_0.75.0_amd64.deb");

        Assert.Equal(@"C:\Temp\nono-cli_0.75.0_amd64.deb", wsl.Calls[^1][^1]);
    }

    [Fact]
    public async Task EachStep_IsAnnouncedBeforeItRuns()
    {
        var lines = new List<string>();

        await WslSetupRunner.RunAsync(NewUbuntu, new AnsweringWsl().Host(null), lines.Add, CancellationToken.None);

        Assert.Equal(NewUbuntu.Steps.Select((step, i) => $"[{i + 1}/4] {step.Description}"), lines);
    }

    [Fact]
    public async Task AFailingStep_StopsTheRun_AndSaysWhichStepAndWhy()
    {
        var wsl = new AnsweringWsl { Fails = call => call.Contains(WslSetupScripts.Packages) };

        var outcome = await RunAsync(NewUbuntu, wsl);

        Assert.DoesNotContain(wsl.Calls, call => call.Contains(WslSetupScripts.Nono));
        Assert.NotNull(outcome.Failure);
        Assert.Contains($"step 3 of 4 ({NewUbuntu.Steps[2].Description})", outcome.Failure);
        Assert.Contains("exited 100", outcome.Failure);
        Assert.Contains("E: Unable to locate package git", outcome.Failure);
    }

    /// <summary>
    /// Only the end of a long log is shown: apt prints hundreds of lines before the one
    /// that says why it failed, and that one is last.
    /// </summary>
    [Fact]
    public async Task AFailedStepsOutput_IsCutToItsLastLines()
    {
        var log = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"Get:{i} http://archive.ubuntu.com")) + "\nE: the real reason\n";
        var wsl = new AnsweringWsl { Fails = call => call.Contains(WslSetupScripts.Packages), FailureOutput = log };

        var outcome = await RunAsync(NewUbuntu, wsl);

        Assert.Contains("E: the real reason", outcome.Failure);
        Assert.DoesNotContain("Get:1 ", outcome.Failure);
    }

    /// <summary>
    /// A distro this run installed holds nothing of the user's yet, so removing it and
    /// starting over is safe advice. For any other distro that advice would destroy their work.
    /// </summary>
    [Fact]
    public async Task AFailureAfterThisRunInstalledTheDistro_SaysHowToStartOver()
    {
        var wsl = new AnsweringWsl { Fails = call => call.Contains(WslSetupScripts.Nono) };

        var outcome = await RunAsync(NewUbuntu, wsl);

        Assert.Contains("wsl --unregister Ubuntu", outcome.Failure);
    }

    /// <summary>
    /// WSL answers a distro install that has to wait for a restart (it has just turned on the
    /// Virtual Machine Platform) with exit code 0 and no distro, as it did on Windows 11 25H2 on
    /// 2026-09-19. Carrying on would fail at the next step on a distro that does not exist.
    /// </summary>
    [Fact]
    public async Task AnInstallThatExitsZeroWithoutADistro_StopsThere_AndShowsWhatWslSaid()
    {
        var wsl = new AnsweringWsl { InstallRegisters = false, InstallOutput = RestartNotice };

        var outcome = await RunAsync(NewUbuntu, wsl);

        Assert.NotNull(outcome.Failure);
        Assert.Contains($"step 1 of 4 ({NewUbuntu.Steps[0].Description})", outcome.Failure);
        Assert.Contains("'Ubuntu'", outcome.Failure);
        Assert.Contains(RestartNotice.TrimEnd(), outcome.Failure);
        Assert.DoesNotContain("--unregister", outcome.Failure);
        Assert.DoesNotContain(wsl.Calls, call => call.Contains(WslSetupScripts.CreateUser));
    }

    /// <summary>WSL names are not case sensitive: <c>VR_WSL_DISTRO=ubuntu</c> installs the image WSL lists as "Ubuntu".</summary>
    [Fact]
    public async Task ADistroListedInItsOwnCapitalization_CountsAsInstalled()
    {
        var plan = WslSetupPlan.For(WslProbeFixtures.RequestedDistroMissing() with { RequestedDistro = "ubuntu" }, User);
        var wsl = new AnsweringWsl { ListAs = "Ubuntu" };

        var outcome = await RunAsync(plan, wsl);

        Assert.Null(outcome.Failure);
        Assert.Contains(wsl.Calls, call => call.Contains(WslSetupScripts.CreateUser));
    }

    [Theory]
    [InlineData("an existing distro")]
    [InlineData("the install itself")]
    public async Task AFailureWithNoDistroFromThisRun_NeverSuggestsRemovingOne(string failing)
    {
        var (plan, fails) = failing == "the install itself"
            ? (NewUbuntu, (Func<IReadOnlyList<string>, bool>)(call => call[0] == "--install"))
            : (WslSetupPlan.For(WslProbeFixtures.NonoMissing(), User), call => call.Contains(WslSetupScripts.Nono));
        var wsl = new AnsweringWsl { Fails = fails };

        var outcome = await RunAsync(plan, wsl);

        Assert.NotNull(outcome.Failure);
        Assert.DoesNotContain("--unregister", outcome.Failure);
        Assert.Contains("setup-wsl", outcome.Failure);
    }

    private static Task<WslSetupOutcome> RunAsync(WslSetupPlan plan, AnsweringWsl wsl, string? localNonoDeb = null) =>
        WslSetupRunner.RunAsync(plan, wsl.Host(localNonoDeb), _ => { }, CancellationToken.None);

    private static string[] AsRoot(string distro, string script, params string[] args) =>
        ["-d", distro, "-u", "root", "--exec", "sh", "-c", script, "vr-setup", .. args];

    /// <summary>
    /// All that WSL 2.7.14 printed, byte for byte, for a distro install that had to wait for a
    /// restart (replayed on Windows 11 25H2, 2026-09-19): no distro named, exit code 0.
    /// </summary>
    private const string RestartNotice =
        "The requested operation is successful. Changes will not be effective until the system is rebooted. \r\n";

    /// <summary>
    /// A wsl.exe that succeeds at everything except the calls <see cref="Fails"/> picks, and lists
    /// the distros its installs registered the way WSL 2.7.14 lists them.
    /// </summary>
    private sealed class AnsweringWsl
    {
        private readonly List<string> _registered = [];

        public Func<IReadOnlyList<string>, bool> Fails { get; init; } = _ => false;

        public string FailureOutput { get; init; } = "Reading package lists...\nE: Unable to locate package git\n";

        public int AdministratorExitCode { get; init; }

        /// <summary>False for an install that exits 0 without registering anything.</summary>
        public bool InstallRegisters { get; init; } = true;

        public string InstallOutput { get; init; } = "Downloading: Ubuntu\nInstalling: Ubuntu\nDistribution successfully installed.\n";

        /// <summary>The name an install is listed under, when WSL's differs from the one asked for.</summary>
        public string? ListAs { get; init; }

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public List<IReadOnlyList<string>> AdministratorCalls { get; } = [];

        /// <summary>The runner never probes, so a probe here would be a defect.</summary>
        public WslSetupHost Host(string? localNonoDeb) => new(
            _ => throw new InvalidOperationException("the runner probed"),
            RunAsync,
            (argv, _) =>
            {
                AdministratorCalls.Add(argv.ToList());
                return Task.FromResult((AdministratorExitCode, ""));
            },
            User,
            localNonoDeb);

        private Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
        {
            Calls.Add(argv.ToList());
            if (Fails(argv))
                return Task.FromResult((100, FailureOutput));
            if (argv is ["-l", "-v"])
                return Task.FromResult(Listing());
            if (argv[0] != "--install")
                return Task.FromResult((0, "ok\n"));

            if (InstallRegisters)
                _registered.Add(ListAs ?? (argv is [_, _, "--name", var name, ..] ? name : argv[1]));
            return Task.FromResult((0, InstallOutput));
        }

        private (int ExitCode, string Output) Listing() => _registered.Count == 0
            ? (-1, "Windows Subsystem for Linux has no installed distributions.\n"
                   + "You can resolve this by installing a distribution with the instructions below:\n\n"
                   + "Use 'wsl.exe --list --online' to list available distributions\n"
                   + "and 'wsl.exe --install <Distro>' to install.\n")
            : (0, "  NAME              STATE           VERSION\n"
                  + string.Concat(_registered.Select((name, i) => $"{(i == 0 ? '*' : ' ')} {name,-17} Stopped         2\n")));
    }
}
