using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Drives the built <c>VisualRelay.Cli</c> the way the launcher does after the
/// bootstrap exec — the C# replacement for the launcher tests that used to drive
/// the bash <c>case</c> dispatch. It runs <c>&lt;real-dotnet&gt; exec VisualRelay.Cli.dll
/// &lt;args&gt;</c> with a crafted PATH (so the CLI's own shell-outs to
/// <c>dotnet</c>/<c>nono</c>/<c>swival</c>/<c>backend.sh</c> hit stubs), a
/// sandbox repo as <c>VISUAL_RELAY_SCRIPT_DIR</c>, and the env seams the moved
/// gates honor. The real dotnet is passed by absolute path so loading the CLI is
/// unaffected by the stubbed PATH.
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

    /// <summary>
    /// A flag-aware <c>dotnet</c> stub body for the launch tests: when invoked as
    /// <c>dotnet run --project …VisualRelay.Backend… -- start</c> (the launch
    /// preamble's repointed backend start) it touches <c>$VR_BACKEND_FLAG</c>;
    /// every other dotnet invocation (the app's <c>dotnet run</c>) is a no-op
    /// success. Replaces the old stub <c>tools/backend/backend.sh</c> signal.
    /// </summary>
    public const string BackendAwareDotnetStub =
        "for a in \"$@\"; do case \"$a\" in *VisualRelay.Backend*) IS_BACKEND=1;; start) IS_START=1;; esac; done\n" +
        "if [[ -n \"${IS_BACKEND:-}\" && -n \"${IS_START:-}\" && -n \"${VR_BACKEND_FLAG:-}\" ]]; then echo ran >> \"$VR_BACKEND_FLAG\"; fi\n" +
        "exit 0";

    /// <summary>Creates a sandbox repo dir with <c>.relay/config.json</c> (carrying
    /// the given bypassSandbox flag) and a <c>bin/</c> stub dir. The backend start
    /// signal now flows through the <see cref="BackendAwareDotnetStub"/> dotnet
    /// stub (launch runs <c>VisualRelay.Backend start</c> via dotnet, not a shell
    /// script). Returns (repoRoot, stubBin).</summary>
    public static (string RepoRoot, string StubBin) NewSandboxRepo(bool bypassSandbox)
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-cli-" + Guid.NewGuid().ToString("N"));
        var stubBin = Path.Combine(repoRoot, "bin");
        Directory.CreateDirectory(stubBin);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".relay"));
        File.WriteAllText(Path.Combine(repoRoot, ".relay", "config.json"),
            $"{{\"testCmd\":\"true\",\"bypassSandbox\":{(bypassSandbox ? "true" : "false")}}}");
        // A `visual-relay` marker file so any walk-up resolution lands here too.
        File.WriteAllText(Path.Combine(repoRoot, "visual-relay"), "#!/usr/bin/env bash\n");
        return (repoRoot, stubBin);
    }
}
