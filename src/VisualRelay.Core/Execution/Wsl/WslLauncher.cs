namespace VisualRelay.Core.Execution.Wsl;

/// <summary>A wsl.exe process to start: the file, its argv and the environment it needs.</summary>
public sealed record WslLaunch(
    string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Builds the wsl.exe invocation that runs a command under nono inside the
/// distro. The command crosses two argument encoders (the .NET ArgumentList
/// quoting and wsl.exe's CommandLineToArgvW split) and no Linux shell re-parses
/// it: with <c>--exec</c> the distro execs the argv as-is, and the only shell
/// involved is the fixed <see cref="Envelope"/>, whose positional parameters
/// receive the pid file, the workspace and the command verbatim. So every element
/// VR adds here is ONE argv element and is never quoted or joined.
/// </summary>
public static class WslLauncher
{
    /// <summary>
    /// The fixed script wsl.exe runs (<c>sh -c &lt;Envelope&gt; vr &lt;pidFile&gt;
    /// &lt;workspace&gt; &lt;command...&gt;</c>). It creates the pid file's directory
    /// (a fresh distro has none, and the echo below would fail silently); changes
    /// into the workspace and exits 127 when that fails (wsl.exe's own <c>--cd</c>
    /// failure is non-fatal and would silently run the command in the home
    /// directory); starts the command through setsid so the sandbox root leads its
    /// own session and process group (one <c>kill -- -pgid</c> from outside
    /// reaches the whole tree, which killing wsl.exe never does); records that pid
    /// in the pid file; and waits so the command's exit code is the envelope's.
    /// </summary>
    public const string Envelope =
        "p=$1; d=$2; shift 2; mkdir -p \"$(dirname \"$p\")\" || exit 127; cd \"$d\" || exit 127; "
        + "setsid \"$@\" & c=$!; echo \"$c\" > \"$p\"; wait \"$c\"";

    private const string Shell = "/bin/sh";
    private const string ScriptName = "vr";
    private const string Env = "env";

    /// <summary>
    /// <c>wsl.exe -d &lt;distro&gt; --exec /bin/sh -c &lt;Envelope&gt; vr &lt;pidFile&gt;
    /// &lt;linuxWorkspace&gt; env [-u K]... [K=V]... &lt;nonoPrefix...&gt; &lt;program&gt; &lt;args...&gt;</c>.
    /// <paramref name="nonoPrefix"/> is the absolute nono path followed by its
    /// <c>run ... --</c> arguments with Linux paths. Environment changes travel as
    /// <c>env</c> arguments because a wsl.exe child's Windows environment does
    /// not cross into Linux; removals come first so a removed name can be re-set.
    /// </summary>
    public static WslLaunch Build(
        string wslExe,
        string distro,
        string linuxWorkspace,
        string pidFile,
        IReadOnlyList<string> nonoPrefix,
        string program,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyCollection<string>? envRemove = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(wslExe);
        ArgumentException.ThrowIfNullOrEmpty(distro);
        ArgumentException.ThrowIfNullOrEmpty(program);
        if (string.IsNullOrEmpty(linuxWorkspace) || linuxWorkspace[0] != '/')
            throw new ArgumentException($"The workspace must be an absolute Linux path, got '{linuxWorkspace}'.", nameof(linuxWorkspace));
        if (string.IsNullOrEmpty(pidFile) || pidFile[0] != '/')
            throw new ArgumentException($"The pid file must be an absolute Linux path, got '{pidFile}'.", nameof(pidFile));

        var argv = new List<string>
        {
            "-d", distro, "--exec", Shell, "-c", Envelope, ScriptName, pidFile, linuxWorkspace, Env,
        };
        if (envRemove is not null)
        {
            foreach (var name in envRemove)
            {
                argv.Add("-u");
                argv.Add(ValidName(name));
            }
        }
        if (env is not null)
        {
            foreach (var (name, value) in env)
                argv.Add($"{ValidName(name)}={value}");
        }
        argv.AddRange(nonoPrefix);
        argv.Add(program);
        argv.AddRange(args);
        return new WslLaunch(wslExe, argv, WslExeEnvironment.Variables);
    }

    /// <summary><c>wsl.exe -d &lt;distro&gt; --exec &lt;argv...&gt;</c>: a plain command inside the distro, no sandbox, no envelope.</summary>
    public static WslLaunch BuildPlain(string wslExe, string distro, IReadOnlyList<string> argv)
    {
        ArgumentException.ThrowIfNullOrEmpty(wslExe);
        ArgumentException.ThrowIfNullOrEmpty(distro);
        if (argv.Count == 0)
            throw new ArgumentException("wsl.exe --exec needs a command.", nameof(argv));

        return new WslLaunch(wslExe, ["-d", distro, "--exec", .. argv], WslExeEnvironment.Variables);
    }

    /// <summary>An <c>env</c> name must be non-empty and free of '=', or the K=V shape would say something else.</summary>
    private static string ValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('='))
            throw new ArgumentException($"'{name}' is not a valid environment variable name.", nameof(name));
        return name;
    }
}
