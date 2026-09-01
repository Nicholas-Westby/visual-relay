using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Drives the built <c>VisualRelay.Cli</c> the way the launcher does after the
/// bootstrap exec — the C# replacement for the launcher tests that used to drive
/// the bash <c>case</c> dispatch. It runs <c>&lt;real-dotnet&gt; exec VisualRelay.Cli.dll
/// &lt;args&gt;</c> with a crafted PATH (so the CLI's own shell-outs to
/// <c>dotnet</c> and <c>nono</c> hit stubs), a sandbox repo as
/// <c>VISUAL_RELAY_SCRIPT_DIR</c>, and the env seams the moved gates honor. The
/// real dotnet is passed by absolute path so loading the CLI is unaffected by
/// the stubbed PATH.
/// </summary>
internal static class CliHarness
{
    private static string CliDll
    {
        get
        {
            // The test assembly references VisualRelay.Cli, so its DLL is copied
            // next to the test DLL in the output directory.
            var local = Path.Combine(AppContext.BaseDirectory, "VisualRelay.Cli.dll");
            if (File.Exists(local))
                return local;
            throw new FileNotFoundException("VisualRelay.Cli.dll not found next to the test assembly", local);
        }
    }

    private static string RealDotnet
    {
        get
        {
            // DOTNET_ROOT/dotnet or the dotnet that launched these tests.
            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root))
            {
                var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate))
                    return candidate;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(candidate))
                    return candidate;
            }
            return "dotnet";
        }
    }

    /// <summary>
    /// Runs the CLI with <paramref name="cliArgs"/> inside <paramref name="repoRoot"/>
    /// (set as both cwd and VISUAL_RELAY_SCRIPT_DIR), with <paramref name="stubBin"/>
    /// prepended to PATH, and the supplied <paramref name="env"/> overrides. Returns
    /// (exitCode, stdout, stderr).
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string repoRoot,
        string stubBin,
        IReadOnlyList<string> cliArgs,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(RealDotnet)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(CliDll);
        foreach (var a in cliArgs)
            psi.ArgumentList.Add(a);

        psi.Environment["PATH"] = stubBin + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin";
        psi.Environment["VISUAL_RELAY_SCRIPT_DIR"] = repoRoot;
        psi.Environment["ORIGINAL_CWD"] = repoRoot;
        // Hermetic + host-independent for any git the CLI spawns under this harness.
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // .NET resolves a bare "dotnet" against the running host, not PATH, so a
        // PATH stub cannot intercept the CLI's own dotnet shell-outs. Point the
        // CLI at the stub dotnet (when present) via the production override seam.
        var stubDotnet = Path.Combine(stubBin, "dotnet");
        if (File.Exists(stubDotnet))
            psi.Environment["VISUAL_RELAY_DOTNET"] = stubDotnet;

        if (env is not null)
            foreach (var (k, v) in env)
                psi.Environment[k] = v;

        using var process = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Writes an executable bash stub <paramref name="name"/> into
    /// <paramref name="stubBin"/> with the given body (default: <c>exit 0</c>).</summary>
    public static void WriteStub(string stubBin, string name, string? body = null)
    {
        Directory.CreateDirectory(stubBin);
        var path = Path.Combine(stubBin, name);
        File.WriteAllText(path, "#!/bin/bash\n" + (body ?? "exit 0") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A <c>dotnet</c> stub body that appends its argv to
    /// <paramref name="argvLog"/>, so a test can assert what the launcher ran —
    /// or, by the log's absence, that it ran nothing at all.</summary>
    public static string ArgvRecordingDotnetStub(string argvLog) =>
        $"printf '%s ' \"$@\" >> '{argvLog}'; printf '\\n' >> '{argvLog}'\nexit 0";

    /// <summary>Creates a sandbox repo dir with a minimal <c>.relay/config.json</c>
    /// and a <c>bin/</c> stub dir. The sandbox is always on, so the config carries
    /// no bypass key. Returns (repoRoot, stubBin).</summary>
    public static (string RepoRoot, string StubBin) NewSandboxRepo()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-cli-" + Guid.NewGuid().ToString("N"));
        var stubBin = Path.Combine(repoRoot, "bin");
        Directory.CreateDirectory(stubBin);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".relay"));
        File.WriteAllText(Path.Combine(repoRoot, ".relay", "config.json"),
            "{\"testCmd\":\"true\"}");
        // A `visual-relay` marker file so any walk-up resolution lands here too.
        File.WriteAllText(Path.Combine(repoRoot, "visual-relay"), "#!/usr/bin/env bash\n");
        return (repoRoot, stubBin);
    }
}
