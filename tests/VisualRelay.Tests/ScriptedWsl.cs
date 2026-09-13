using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// A scripted wsl.exe for <see cref="WslProber"/>: replies with canned output per
/// argv (joined with spaces) and records every call, so a probe's facts, argv and
/// step order are pinned without spawning anything. An unscripted argv fails.
/// </summary>
internal sealed class ScriptedWsl
{
    public const string Exe = @"C:\Windows\System32\wsl.exe";

    public const string ListOutput =
        "  NAME              STATE           VERSION\n" +
        "* Ubuntu            Running         2\n" +
        "  Debian            Stopped         1\n";

    // nono 0.75.0's own Landlock check (a syscall probe, crates/nono-cli/src/setup.rs).
    public const string SandboxCheckPassed =
        "[2/4] Testing sandbox support...\n  * Kernel version: 6.18.33.2-microsoft-standard-WSL2\n" +
        "  * Landlock enabled (syscall probe)\n  * Filesystem ruleset creation verified\n  * WSL2 environment detected\n";

    // What a login shell printed on WSL 2.7.14: bash's no-TTY complaints, then the
    // marked PATH that ~/.profile (rustup) and ~/.bashrc (nvm) built.
    public const string UserPath = "/home/alice/.nvm/versions/node/v24.21.0/bin:/home/alice/.cargo/bin:/usr/bin:/bin";

    public static string LoginPathReply(string path) =>
        "bash: cannot set terminal process group (-1): Inappropriate ioctl for device\n" +
        "bash: no job control in this shell\n" +
        $"\n{WslProber.LoginPathMarker}{path}{WslProber.LoginPathMarker}\n";

    private readonly Dictionary<string, (int ExitCode, string Output)> _replies = new(StringComparer.Ordinal);

    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>A healthy Ubuntu: every probe step answers as a usable machine would.</summary>
    public static ScriptedWsl HealthyUbuntu() => new ScriptedWsl()
        .On("-l -v", 0, ListOutput)
        .On("-d Ubuntu --exec uname -r", 0, "5.15.167.4-microsoft-standard-WSL2\n")
        .On("-d Ubuntu --exec sh -lc command -v nono", 0, "/usr/local/bin/nono\n")
        .On("-d Ubuntu --exec /usr/local/bin/nono --version", 0, "nono 0.75.0\n")
        .On("-d Ubuntu --exec env NONO_NO_UPDATE_CHECK=1 /usr/local/bin/nono setup --check-only", 0, SandboxCheckPassed)
        .On("-d Ubuntu --exec sh -lc printf %s \"$HOME\"", 0, "/home/alice")
        .On($"-d Ubuntu --exec sh -c {WslProber.LoginPathScript}", 0, LoginPathReply(UserPath));

    public ScriptedWsl On(string argv, int exitCode, string output)
    {
        _replies[argv] = (exitCode, output);
        return this;
    }

    public Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        Calls.Add(argv.ToList());
        var key = string.Join(' ', argv);
        return Task.FromResult(_replies.TryGetValue(key, out var reply)
            ? reply
            : (1, $"scripted wsl.exe: no reply for [{key}]"));
    }
}
