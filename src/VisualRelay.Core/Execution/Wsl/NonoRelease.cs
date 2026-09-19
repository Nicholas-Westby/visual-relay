namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The nono release Visual Relay pins. flake.nix builds the same tag from source for macOS
/// and Linux; on Windows <c>setup-wsl</c> installs its Debian package inside the distro and
/// refuses any file whose SHA-256 is not the one recorded here, whether it was downloaded or
/// given as a local file.
/// </summary>
public static class NonoRelease
{
    public const string Version = "0.75.0";

    /// <summary>
    /// SHA-256 of <c>nono-cli_&lt;version&gt;_&lt;arch&gt;.deb</c> per Debian architecture, copied from
    /// the release's SHA256SUMS.txt and checked against a download on 2026-09-19.
    /// </summary>
    public const string Amd64DebSha256 = "7a09d2fe454d4bf0b71f7b16c5e7740cb6884f3ddad2acc342bc81006abca04a";

    /// <inheritdoc cref="Amd64DebSha256"/>
    public const string Arm64DebSha256 = "eae9f2bb6bd8345cffb7e415c93a486a6c76371eec88c252938767f6ca9a9951";

    /// <summary>Where the release's Debian packages are downloaded from, up to the architecture.</summary>
    public const string DebUrlPrefix =
        "https://github.com/nolabs-ai/nono/releases/download/v" + Version + "/nono-cli_" + Version + "_";
}
