namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// What each <c>setup-wsl</c> step runs inside the distro, as root (<c>wsl -u root</c>, which
/// needs no password). Each is one <c>sh -c</c> line like every other in-distro script here,
/// with its inputs passed as arguments rather than written into the text.
/// </summary>
internal static class WslSetupScripts
{
    /// <summary>
    /// <c>$1</c> is the user. Creates them (a home, bash, and the sudo group where the distro
    /// has one) unless they exist, then makes them the default user by APPENDING a
    /// <c>[user]</c> section to <c>/etc/wsl.conf</c>: Ubuntu's image already has
    /// <c>[boot] systemd=true</c> there, which rewriting the file would lose. The password
    /// stays locked; WSL signs the default user in without one.
    /// </summary>
    internal const string CreateUser =
        "u=$1; id -u \"$u\" >/dev/null 2>&1 || useradd -m -s /bin/bash \"$u\" || exit 1; "
        + "if getent group sudo >/dev/null; then usermod -aG sudo \"$u\" || exit 1; fi; "
        + "printf '\\n[user]\\ndefault=%s\\n' \"$u\" >> /etc/wsl.conf";

    /// <summary>
    /// Installs whichever of git, curl and the CA certificates the distro lacks, and does
    /// nothing at all, not even an index update, when it lacks none. apt waits up to five
    /// minutes for its lock, because a distro's first boot can start its own apt run.
    /// </summary>
    internal const string Packages =
        "export DEBIAN_FRONTEND=noninteractive; need=; "
        + "command -v git >/dev/null || need=\"$need git\"; "
        + "command -v curl >/dev/null || need=\"$need curl\"; "
        + "[ -e /etc/ssl/certs/ca-certificates.crt ] || need=\"$need ca-certificates\"; "
        + "[ -n \"$need\" ] || exit 0; "
        + "command -v apt-get >/dev/null || { echo \"vr-setup: this distro has no apt-get; install$need with its own package manager\" >&2; exit 3; }; "
        + "apt-get -q -o DPkg::Lock::Timeout=300 update && "
        + "apt-get -q -y -o DPkg::Lock::Timeout=300 install --no-install-recommends $need";

    /// <summary>
    /// <c>$1</c> version, <c>$2</c>/<c>$3</c> the amd64/arm64 SHA-256, <c>$4</c> the download
    /// URL up to the architecture, <c>$5</c> a local package or empty. Takes the package for
    /// the distro's architecture from <c>$5</c> (a Windows path is translated) or the release,
    /// refuses it unless its SHA-256 is the pinned one, and installs it without its recommends:
    /// they are gnome-keyring and about 80 MB of desktop packages nono does not need here.
    /// </summary>
    internal const string Nono =
        "v=$1; amd=$2; arm=$3; url=$4; src=$5; arch=$(dpkg --print-architecture) || exit 3; "
        + "case $arch in amd64) sum=$amd ;; arm64) sum=$arm ;; "
        + "*) echo \"vr-setup: nono $v publishes no Debian package for $arch\" >&2; exit 3 ;; esac; "
        + "dir=$(mktemp -d) || exit 1; trap 'rm -rf \"$dir\"' EXIT; chmod 755 \"$dir\"; "
        + "deb=$dir/nono-cli_${v}_$arch.deb; "
        + "if [ -n \"$src\" ]; then case $src in /*) ;; *) src=$(wslpath -u \"$src\") || exit 1 ;; esac; "
        + "cp \"$src\" \"$deb\" || exit 1; "
        + "else curl -fsSL --retry 3 -o \"$deb\" \"$url$arch.deb\" || exit 1; fi; "
        + "got=$(sha256sum \"$deb\" | cut -d' ' -f1); "
        + "[ \"$got\" = \"$sum\" ] || { echo \"vr-setup: refusing the nono package: its SHA-256 is $got, not the pinned $sum\" >&2; exit 4; }; "
        + "apt-get -q -y -o DPkg::Lock::Timeout=300 install --no-install-recommends \"$deb\"";
}
