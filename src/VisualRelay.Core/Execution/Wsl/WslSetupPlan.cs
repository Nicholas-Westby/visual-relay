using System.Text;
using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>One thing <c>setup-wsl</c> does to a machine, in the order it does them.</summary>
public abstract record WslSetupStep
{
    /// <summary>What the step does, in words for the plan the user sees before anything runs.</summary>
    public abstract string Description { get; }
}

/// <summary>Installs a distro WSL can download (<paramref name="Image"/>) and registers it as <paramref name="Name"/>.</summary>
public sealed record InstallDistroStep(string Image, string Name) : WslSetupStep
{
    public override string Description => Image == Name
        ? $"install the {Image} distro (a download, usually a minute or two)"
        : $"install the {Image} distro as '{Name}' (a download, usually a minute or two)";
}

/// <summary>Creates <paramref name="User"/> in a distro this setup installed and makes it the default user.</summary>
public sealed record CreateUserStep(string Distro, string User) : WslSetupStep
{
    public override string Description => $"create the Linux user '{User}' in '{Distro}' and make it the default";
}

/// <summary>Installs whichever of git, curl and the CA certificates the distro lacks.</summary>
public sealed record InstallPackagesStep(string Distro) : WslSetupStep
{
    public override string Description => $"install git, curl and CA certificates in '{Distro}' where missing (apt, as root)";
}

/// <summary>Installs the pinned nono release from its Debian package, refusing one whose checksum differs.</summary>
public sealed record InstallNonoStep(string Distro) : WslSetupStep
{
    public override string Description =>
        $"install nono {NonoRelease.Version} in '{Distro}' from its Debian package, checked against a pinned SHA-256";
}

/// <summary>
/// What <c>setup-wsl</c> would do, decided from a <see cref="WslProbe"/> alone: the steps a distro
/// still needs, or the reason it cannot proceed. Everything inside a distro can be done without
/// Windows administrator rights once WSL itself is present (measured on Windows 11 on
/// 2026-09-19: a new distro, a user, git and nono in about 130 s, with no UAC prompt, no reboot and
/// no question asked). WSL itself, a WSL1 distro, Landlock and an unreadable home are left to the
/// user, with the gate's own fix, because each needs administrator rights, a kernel, or a decision
/// that is theirs.
/// </summary>
public sealed partial record WslSetupPlan(IReadOnlyList<WslSetupStep> Steps, string? Blocker)
{
    /// <summary>The image installed when nothing names one: WSL's current Ubuntu LTS.</summary>
    private const string DefaultImage = "Ubuntu";

    private const string FallbackUser = "vr";

    [GeneratedRegex(@"^(?:Ubuntu(?:-\d{2}\.\d{2})?|Debian)$", RegexOptions.IgnoreCase)]
    private static partial Regex AptImage();

    /// <summary>The plan for <paramref name="probe"/>, creating <paramref name="linuxUser"/> in a distro it installs.</summary>
    public static WslSetupPlan For(WslProbe probe, string linuxUser) =>
        StepsFor(probe, linuxUser) is { } steps
            ? new WslSetupPlan(steps, null)
            : new WslSetupPlan([], WslGate.Decide(probe).Message);

    /// <summary>
    /// True when setup has something to do for <paramref name="probe"/> and nothing it leaves
    /// to the user stands in the way. The gate asks this to decide whether to offer
    /// <c>setup-wsl</c>, which is why the answer never goes through the gate's own message.
    /// </summary>
    internal static bool CanFinish(WslProbe probe) => StepsFor(probe, FallbackUser) is { Count: > 0 };

    /// <summary>The steps <paramref name="probe"/> calls for, or null when what it found is not setup's to fix.</summary>
    private static IReadOnlyList<WslSetupStep>? StepsFor(WslProbe probe, string linuxUser)
    {
        if (!probe.WslExeFound || probe.WslPlatformMissing)
            return null;

        if (probe.DistroName is not { } distro)
        {
            // A name that is itself an apt-based distro WSL can install is that distro; any other
            // name gets Ubuntu registered under it, next to whatever the machine already has.
            var name = probe.RequestedDistro ?? DefaultImage;
            var image = AptImage().IsMatch(name) ? name : DefaultImage;
            return
            [
                new InstallDistroStep(image, name),
                new CreateUserStep(name, linuxUser),
                new InstallPackagesStep(name),
                new InstallNonoStep(name),
            ];
        }

        if (!probe.IsWsl2)
            return null;
        // Without nono there is no Landlock verdict yet (nono's own probe gives it), so a
        // missing nono is installed first and the gate judges Landlock afterwards.
        if (probe.NonoPath is null)
            return [new InstallPackagesStep(distro), new InstallNonoStep(distro)];
        if (!probe.LandlockActive || probe.DistroHome is null)
            return null;
        return probe.GitPath is null ? [new InstallPackagesStep(distro)] : [];
    }

    /// <summary>
    /// A Linux user name for <paramref name="windowsUser"/>: accents dropped, lowercased, only the
    /// characters useradd accepts, and "vr" when nothing valid is left to start with.
    /// </summary>
    public static string LinuxUserFor(string windowsUser)
    {
        var name = new StringBuilder();
        foreach (var c in windowsUser.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            // Decomposed, an accented letter is its base letter plus a combining mark, and
            // only the letter passes this test.
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
                name.Append(c);
        }

        var user = name.Length > 32 ? name.ToString(0, 32) : name.ToString();
        return user.Length > 0 && (user[0] is >= 'a' and <= 'z' || user[0] == '_') ? user : FallbackUser;
    }
}
